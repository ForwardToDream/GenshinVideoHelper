using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;

namespace GenshinVideoHelper.Smoke;

// Opt-in measurements, not network-dependent assertions in the ordinary test suite.
internal static class LatencyTests
{
    private const string Example = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3";

    public static void Run(string root, bool background = false)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => background ? BackgroundAsync(root) : RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task BackgroundAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "background-" + Guid.NewGuid().ToString("N"));
        var otherProfile = Path.Combine(directory, "other-chrome");
        using var other = new ChromeBrowser(otherProfile);
        using var cdp = new CdpClient();
        await other.WarmupAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(otherProfile, "DevToolsActivePort"));
        var socket = new Uri($"ws://127.0.0.1:{lines[0]}{lines[1]}");
        var target = await cdp.SendAsync(socket, "Target.createTarget", new { url = "about:blank", newWindow = true });
        var otherPage = await other.GetPageAsync(target.GetProperty("targetId").GetString()!);
        await cdp.SendAsync(new Uri(otherPage!.WebSocketDebuggerUrl), "Page.bringToFront", new { });
        using var otherProcess = Process.GetProcessById(await other.GetBrowserProcessIdAsync());
        _ = otherProcess.Handle;
        var profile = Path.Combine(directory, "helper-chrome");
        var window = new MainWindow(new AppServices(new AppSettings { VideoUrl = Example, SelectedVideoLibraryId = null, HotkeysEnabled = false }, profile, enableWarmup: true))
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        var stolen = false;
        try
        {
            window.Show();
            await window.Services.WarmupAsync();
            var helperPid = await window.Services.Browser.GetBrowserProcessIdAsync();
            var foreignWindow = await cdp.SendAsync(socket, "Browser.getWindowForTarget", new { targetId = otherPage.Id });
            await cdp.SendAsync(socket, "Browser.setWindowBounds", new { windowId = foreignWindow.GetProperty("windowId").GetInt32(), bounds = new { windowState = "normal" } });
            await cdp.SendAsync(new Uri(otherPage.WebSocketDebuggerUrl), "Page.bringToFront", new { });
            await Task.Delay(250);
            otherProcess.Refresh();
            if (otherProcess.MainWindowHandle != 0) SetForegroundWindow(otherProcess.MainWindowHandle);
            await Task.Delay(100);
            NativeInput.GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
            Console.WriteLine($"Background setup: foreground PID {foregroundPid}, unrelated Chrome {otherProcess.Id}, helper {helperPid}, unrelated HWND {otherProcess.MainWindowHandle}.");
            Check(foregroundPid == otherProcess.Id, "Independent Chrome is foreground before silent follow");
            heartbeat.Tick += (_, _) => { NativeInput.GetWindowThreadProcessId(GetForegroundWindow(), out var pid); if (pid == helperPid) stolen = true; };
            heartbeat.Start();
            var clock = Stopwatch.StartNew();
            ((Button)window.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => window.Services.Follow.Current is { Phase: FollowPhase.Following, Video: { Paused: false, PictureInPicture: true } }, TimeSpan.FromSeconds(45));
            Console.WriteLine($"Background P3 ready in {clock.ElapsedMilliseconds} ms without clicking helper Chrome.");
            var helperLines = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"));
            var helperSocket = new Uri($"ws://127.0.0.1:{helperLines[0]}{helperLines[1]}");
            var bounds = await cdp.SendAsync(helperSocket, "Browser.getWindowForTarget", new { targetId = window.Services.Follow.Current.Page!.Id });
            Check(bounds.GetProperty("bounds").GetProperty("windowState").GetString() == "minimized", "Helper Chrome remains minimized while PiP plays");
            await window.Services.Follow.NavigateAsync(4);
            await window.Services.Follow.PollAsync();
            await WaitAsync(() => window.Services.Follow.Current is { Phase: FollowPhase.Following, Request.Identity.Part: 4, Video: { Paused: false, PictureInPicture: true } }, TimeSpan.FromSeconds(45));
            NativeInput.GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
            Check(!stolen && foregroundPid == otherProcess.Id, "Silent load, native PiP and episode changes preserve other Chrome focus");
            Console.WriteLine("Background P4, minimized browser and foreground isolation passed.");
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!otherProcess.HasExited, "Exiting helper preserves foreground Chrome");
        }
        finally
        {
            heartbeat.Stop();
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(4));
            await other.CloseAsync();
        }
    }

    private static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "latency", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var results = new List<Measurement>();
        try
        {
            var profile = Path.Combine(directory, "profile");
            await MeasureFollowAsync("fresh-profile", profile, results);
            await MeasureFollowAsync("reused-profile", profile, results);
            await MeasureFollowAsync("prewarmed-profile", profile, results, prewarm: true);
            await MeasureWarmupExitAsync(Path.Combine(directory, "warmup-exit"), results);
            await MeasureIdleExitAsync(profile, results);
            // A listening but unresponsive stale endpoint reproduces the bounded connection probe.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var staleProfile = Path.Combine(directory, "stale-profile");
            Directory.CreateDirectory(staleProfile);
            await File.WriteAllLinesAsync(Path.Combine(staleProfile, "DevToolsActivePort"),
                [((IPEndPoint)listener.LocalEndpoint).Port.ToString(), "/devtools/browser/stale-test"]);
            await MeasureFollowAsync("unresponsive-stale-endpoint", staleProfile, results);
            await MeasurePendingExitAsync(Path.Combine(directory, "pending-profile"), results);
        }
        finally
        {
            var report = new { RecordedAt = DateTimeOffset.Now, Environment.OSVersion, Runtime = Environment.Version.ToString(),
                Video = Example, Notes = "Independent Chrome profiles; cached OS resources; thresholds only produce warnings. Network and Chrome startup vary.", Results = results };
            var reportPath = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Latency report: " + reportPath);
        }
    }

    private static async Task MeasureFollowAsync(string name, string profile, List<Measurement> results, bool prewarm = false)
    {
        var measurement = new Measurement(name);
        results.Add(measurement);
        using var browser = new ChromeBrowser(profile);
        var measuredBrowser = new MeasuredBrowser(browser, measurement);
        using var episodes = new BilibiliEpisodeService();
        using var player = new VideoController();
        var video = new MeasuredVideo(player, measurement);
        var window = CreateWindow(profile, measuredBrowser, episodes, video, prewarm: prewarm);
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        var previousTick = 0d;
        heartbeat.Tick += (_, _) =>
        {
            var now = measurement.Clock.Elapsed.TotalMilliseconds;
            if (measurement.Started)
            {
                measurement.MaxUiGapMs = Math.Max(measurement.MaxUiGapMs, now - previousTick);
                // Read only the exact processes this adapter launched, never all Chrome processes.
                var launched = GetField<List<Process>>(browser, "_launchedProcesses")!;
                if (!measurement.Metrics.ContainsKey("chromeWindowMs"))
                    foreach (var process in launched)
                    {
                        if (process.HasExited) continue;
                        process.Refresh();
                        var handle = process.MainWindowHandle;
                        if (handle == 0 || !IsWindowVisible(handle)) continue;
                        measurement.Metric("chromeWindowMs", now);
                        break;
                    }
            }
            previousTick = now;
        };
        string? lastPhase = null;
        window.Services.Follow.Changed += state =>
        {
            var phase = state.Phase.ToString();
            if (phase != lastPhase) { lastPhase = phase; measurement.Event("phase:" + phase); }
            if (state.Video is { PictureInPicture: true }) measurement.Once("pipReportedMs");
            if (state.Phase == FollowPhase.Following) measurement.Once("followReadyMs");
        };
        try
        {
            window.Show();
            if (prewarm)
            {
                await window.Services.WarmupAsync();
                using var process = Process.GetProcessById(await browser.GetBrowserProcessIdAsync());
                Check(process.MainWindowHandle == 0 && (await browser.GetPagesAsync()).Count == 0 && window.Services.Follow.Current.Request is null,
                    "Warmup creates no visible window and loads no Bilibili video");
            }
            await WaitAsync(() => !window.Services.Preview.Current.Loading, TimeSpan.FromSeconds(12));
            measurement.Start();
            previousTick = 0;
            heartbeat.Start();
            measurement.Event("start-click");
            var handler = Stopwatch.StartNew();
            ((Button)window.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            measurement.Metric("startHandlerMs", handler.Elapsed.TotalMilliseconds);
            await WaitAsync(() => window.Services.Follow.Current.Phase == FollowPhase.Following ||
                window.Services.Follow.Current is { Phase: FollowPhase.Failed, CanRetry: true }, TimeSpan.FromSeconds(45));
            Check(window.Services.Follow.Current.Phase == FollowPhase.Following, "Latency case reaches automatic PiP: " + name);
            await MeasureCloseAsync(window, measurement);
        }
        catch (Exception ex) { measurement.Error = ex.Message; throw; }
        finally
        {
            heartbeat.Stop();
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            measurement.Metric("maxUiHeartbeatGapMs", measurement.MaxUiGapMs);
            measurement.Print();
        }
    }

    private static async Task MeasureWarmupExitAsync(string profile, List<Measurement> results)
    {
        var measurement = new Measurement("prewarm-only-exit");
        results.Add(measurement);
        var services = new AppServices(new AppSettings { VideoUrl = "", SelectedVideoLibraryId = null, HotkeysEnabled = false }, profile, enableWarmup: true);
        var window = new MainWindow(services) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            await services.WarmupAsync();
            using var process = Process.GetProcessById(await services.Browser.GetBrowserProcessIdAsync());
            _ = process.Handle;
            measurement.Start();
            await MeasureCloseAsync(window, measurement);
            Check(process.HasExited, "Exiting without following releases the prewarmed Chrome");
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10)); measurement.Print(); }
        using var starting = new ChromeBrowser(profile + "-canceled");
        var warming = starting.WarmupAsync();
        await Task.Delay(25);
        var owned = GetField<List<Process>>(starting, "_launchedProcesses")!.Select(p => Process.GetProcessById(p.Id)).ToArray();
        foreach (var process in owned) _ = process.Handle;
        try
        {
            await starting.CloseAsync();
            try { await warming; } catch (OperationCanceledException) { }
            Check(owned.All(p => p.HasExited), "Exit during warmup leaves no launched Chrome process");
        }
        finally { foreach (var process in owned) process.Dispose(); }
    }

    private static async Task MeasureIdleExitAsync(string profile, List<Measurement> results)
    {
        var measurement = new Measurement("idle-exit-with-stale-profile");
        results.Add(measurement);
        using var browser = new ChromeBrowser(profile);
        using var episodes = new BilibiliEpisodeService();
        var window = CreateWindow(profile, new MeasuredBrowser(browser, measurement), episodes, new VideoController(), url: "");
        try
        {
            window.Show();
            measurement.Start();
            await MeasureCloseAsync(window, measurement);
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20)); measurement.Print(); }
    }
    private static async Task MeasurePendingExitAsync(string profile, List<Measurement> results)
    {
        var measurement = new Measurement("exit-with-noncooperative-preview");
        results.Add(measurement);
        using var browser = new ChromeBrowser(profile);
        var episodes = new DelayedEpisodes();
        var window = CreateWindow(profile, new MeasuredBrowser(browser, measurement), episodes, new VideoController());
        try
        {
            window.Show();
            await episodes.Started.Task;
            measurement.Start();
            await MeasureCloseAsync(window, measurement);
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20)); measurement.Print(); }
    }

    private static MainWindow CreateWindow(string profile, IBrowserSession browser, IEpisodeProvider episodes, IVideoPlayer video, string url = Example, bool prewarm = false) =>
        new(new AppServices(new AppSettings { VideoUrl = url, SelectedVideoLibraryId = null, HotkeysEnabled = false },
            profile, browser: browser, episodes: episodes, video: video, enableWarmup: prewarm))
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };

    private static async Task MeasureCloseAsync(MainWindow window, Measurement measurement)
    {
        var started = measurement.Clock.Elapsed.TotalMilliseconds;
        measurement.Event("close-click");
        var closeButton = Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == "关闭工具");
        var handler = Stopwatch.StartNew();
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        measurement.Metric("closeHandlerMs", handler.Elapsed.TotalMilliseconds);
        measurement.Event("close-handler-returned:visible=" + window.IsVisible);
        Check(!window.IsVisible, "Close hides the helper before awaiting cleanup");
        var visibility = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        visibility.Tick += (_, _) => { if (!window.IsVisible) measurement.OnceRelative("windowDisappearedMs", started); };
        if (!window.IsVisible) measurement.OnceRelative("windowDisappearedMs", started);
        visibility.Start();
        try
        {
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            measurement.OnceRelative("windowDisappearedMs", started);
            measurement.OnceRelative("shutdownMs", started);
            Check(measurement.Metrics["shutdownMs"] < 3200, "Shutdown is bounded even with noncooperative work");
        }
        finally { visibility.Stop(); }
    }

    private static async Task WaitAsync(Func<bool> ready, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > timeout) throw new TimeoutException("Latency measurement did not reach its next phase."); await Task.Delay(50); }
    }

    private sealed class MeasuredBrowser(ChromeBrowser inner, Measurement measurement) : IBrowserSession, IBrowserWarmup
    {
        public Task WarmupAsync(CancellationToken token = default) => inner.WarmupAsync(token);
        public async Task<BrowserPage> OpenAsync(string url, CancellationToken token = default)
        { var page = await measurement.TimeAsync("browser-open", () => inner.OpenAsync(url, token)); measurement.Once("browserAttachedMs"); return page; }
        public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default) => inner.NavigateAsync(page, identity, token);
        public Task<BrowserPage?> GetPageAsync(string id, CancellationToken token = default) => measurement.TimeAsync("page-query", () => inner.GetPageAsync(id, token));
        public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => measurement.TimeAsync("pid-query", () => inner.GetBrowserProcessIdAsync(token));
        public async Task CloseAsync()
        {
            measurement.Event("browser-close-start");
            var timer = Stopwatch.StartNew();
            try { await inner.CloseAsync(); }
            finally { measurement.Metric("browserCloseMs", timer.Elapsed.TotalMilliseconds); measurement.Event("browser-close-end"); }
        }
    }

    private sealed class MeasuredVideo(VideoController inner, Measurement measurement) : IVideoPlayer, IVideoActivitySource
    {
        public event Action<string>? MediaActivity { add => inner.MediaActivity += value; remove => inner.MediaActivity -= value; }
        public async Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command, CancellationToken cancellationToken = default)
        {
            var state = await measurement.TimeAsync("video:" + command.Action, () => inner.ExecuteAsync(page, command, cancellationToken));
            if (command.Action == "status") measurement.Once("videoReadyMs");
            return state;
        }
    }

    private sealed class DelayedEpisodes : IEpisodeProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BilibiliVideoInfo> ReadAsync(BrowserPage page, CancellationToken token = default) => ReadFromApiAsync(VideoIdentity.Parse(page.Url), token);
        public async Task<BilibiliVideoInfo> ReadFromApiAsync(VideoIdentity identity, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Task.Delay(5000); // Deliberately exceeds the 2.5-second exit budget.
            return new(identity.Bvid, "timing fixture", identity.Part, [new(3, 40835484789L, "fixture", 604)]);
        }
    }

    private sealed class Measurement(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, double> Metrics { get; } = [];
        public List<TraceEntry> Trace { get; } = [];
        public List<string> Warnings { get; } = [];
        public string? Error { get; set; }
        public double MaxUiGapMs { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public Stopwatch Clock { get; } = new();
        [System.Text.Json.Serialization.JsonIgnore] public bool Started => Clock.IsRunning;
        public void Start() { Clock.Restart(); Event("measurement-start"); }
        public void Metric(string key, double ms) { Metrics[key] = Math.Round(ms, 1); }
        public void Once(string key) { if (!Metrics.ContainsKey(key)) Metric(key, Clock.Elapsed.TotalMilliseconds); }
        public void OnceRelative(string key, double origin) { if (!Metrics.ContainsKey(key)) Metric(key, Clock.Elapsed.TotalMilliseconds - origin); }
        public void Event(string step, double? duration = null) { Trace.Add(new(Math.Round(Clock.Elapsed.TotalMilliseconds, 1), step, duration is null ? null : Math.Round(duration.Value, 1))); }
        public async Task<T> TimeAsync<T>(string name, Func<Task<T>> operation)
        {
            var timer = Stopwatch.StartNew();
            try { var value = await operation(); Event(name, timer.Elapsed.TotalMilliseconds); return value; }
            catch (Exception ex) { Event(name + ":" + ex.GetType().Name, timer.Elapsed.TotalMilliseconds); throw; }
        }
        public void Print()
        {
            // Informational UX budgets; never assert a website or cold Chrome must satisfy them.
            var budgets = new Dictionary<string, double> { ["startHandlerMs"] = 150, ["chromeWindowMs"] = 2000,
                ["followReadyMs"] = 5000, ["closeHandlerMs"] = 150, ["windowDisappearedMs"] = 250,
                ["shutdownMs"] = 2700, ["maxUiHeartbeatGapMs"] = 200 };
            foreach (var (key, budget) in budgets)
                if (Metrics.TryGetValue(key, out var actual) && actual > budget) Warnings.Add($"{key}: {actual:0.0} ms > {budget:0} ms");
            Console.WriteLine(Name + ": " + JsonSerializer.Serialize(Metrics));
            foreach (var warning in Warnings) Console.WriteLine("LATENCY WARNING: " + warning);
        }
    }

    private sealed record TraceEntry(double AtMs, string Step, double? DurationMs);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
