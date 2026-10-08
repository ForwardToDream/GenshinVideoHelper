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

    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
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

    private static async Task MeasureFollowAsync(string name, string profile, List<Measurement> results)
    {
        var measurement = new Measurement(name);
        results.Add(measurement);
        using var browser = new ChromeBrowser(profile);
        var measuredBrowser = new MeasuredBrowser(browser, measurement);
        using var episodes = new BilibiliEpisodeService();
        var video = new MeasuredVideo(new VideoController(), measurement);
        var window = CreateWindow(profile, measuredBrowser, episodes, video);
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

    private static MainWindow CreateWindow(string profile, IBrowserSession browser, IEpisodeProvider episodes, IVideoPlayer video, string url = Example) =>
        new(new AppServices(new AppSettings { VideoUrl = url, SelectedVideoLibraryId = null, HotkeysEnabled = false },
            profile, browser: browser, episodes: episodes, video: video))
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
        var visibility = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        visibility.Tick += (_, _) => { if (!window.IsVisible) measurement.OnceRelative("windowDisappearedMs", started); };
        if (!window.IsVisible) measurement.OnceRelative("windowDisappearedMs", started);
        visibility.Start();
        try
        {
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            measurement.OnceRelative("windowDisappearedMs", started);
            measurement.OnceRelative("shutdownMs", started);
        }
        finally { visibility.Stop(); }
    }

    private static async Task WaitAsync(Func<bool> ready, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > timeout) throw new TimeoutException("Latency measurement did not reach its next phase."); await Task.Delay(50); }
    }

    private sealed class MeasuredBrowser(ChromeBrowser inner, Measurement measurement) : IBrowserSession
    {
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

    private sealed class MeasuredVideo(IVideoPlayer inner, Measurement measurement) : IVideoPlayer
    {
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
            await Task.Delay(1500); // Intentionally ignore cancellation to measure the drain dependency.
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
                ["shutdownMs"] = 1500, ["maxUiHeartbeatGapMs"] = 200 };
            foreach (var (key, budget) in budgets)
                if (Metrics.TryGetValue(key, out var actual) && actual > budget) Warnings.Add($"{key}: {actual:0.0} ms > {budget:0} ms");
            Console.WriteLine(Name + ": " + JsonSerializer.Serialize(Metrics));
            foreach (var warning in Warnings) Console.WriteLine("LATENCY WARNING: " + warning);
        }
    }

    private sealed record TraceEntry(double AtMs, string Step, double? DurationMs);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
