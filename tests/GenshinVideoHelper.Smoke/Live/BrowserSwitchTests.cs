using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class BrowserSwitchTests
{
    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    private static async Task RunAsync(string root)
    {
        Check(new BrowserCatalog().Detect().Count == 2, "Browser-switch suite requires Chrome and Edge");
        using var temporary = TestArtifacts.CreateTemp(root, "browser-switch");
        var data = Path.Combine(temporary.Path, ".gvh");
        var store = new JsonSettingsStore(Path.Combine(data, "config.json"));
        var settings = new AppSettings { BrowserPreference = BrowserPreference.Chrome, HotkeysEnabled = false,
            SelectedVideoLibraryId = null, VideoUrl = new VideoIdentity("BV1MXfEY4EQ2", 2).Url,
            PipLayout = new(.25, 16d / 9, .1, .1) };
        settings.Vision.Enabled = false;
        var services = new AppServices(settings, Path.Combine(data, "Chrome"), store, enableWarmup: true);
        var window = new MainWindow(services) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        using var video = new VideoController();
        try
        {
            window.Show();
            await services.WarmupAsync();
            var manager = (BrowserSessionManager)services.Browser;
            using var chrome = Process.GetProcessById(await manager.GetBrowserProcessIdAsync());
            _ = chrome.Handle;
            await services.Follow.OpenAsync(new("BV1MXfEY4EQ2", 2));
            await ReadyAsync(window, 2);
            await CaptureAsync(window, video, BrowserKind.Chrome, 2);
            ((ComboBox)window.FindName("BrowserSelector")).SelectedIndex = 2;
            Check(store.Load().Settings.BrowserPreference == BrowserPreference.Edge && manager.ActiveBrowser!.Kind == BrowserKind.Chrome,
                "Edge choice is persisted while Chrome video continues");
            await services.Follow.NavigateAsync(3);
            await ReadyAsync(window, 3);
            Check(manager.ActiveBrowser!.Kind == BrowserKind.Chrome && await manager.GetBrowserProcessIdAsync() == chrome.Id,
                "Pending preference does not switch browser during P2 -> P3");
            await CaptureAsync(window, video, BrowserKind.Chrome, 3);

            await services.Follow.OpenAsync(new("BV1MXfEY4EQ2", 2));
            await ReadyAsync(window, 2);
            Check(manager.ActiveBrowser!.Kind == BrowserKind.Edge, "Next Start applies Edge");
            await WaitForAsync(() => chrome.HasExited, "Retired dedicated Chrome exits", window);
            using var edge = Process.GetProcessById(await manager.GetBrowserProcessIdAsync());
            _ = edge.Handle;
            Check(edge.Id != chrome.Id, "New session has a separate browser process");
            await CaptureAsync(window, video, BrowserKind.Edge, 2);
            await services.Follow.NavigateAsync(3);
            await ReadyAsync(window, 3);
            await CaptureAsync(window, video, BrowserKind.Edge, 3);
            Check(Directory.Exists(Path.Combine(data, "Chrome")) && Directory.Exists(Path.Combine(data, "Edge")),
                "Browser profiles remain independent");
            Check(services.Pip.MouseVisibility.Layout == settings.PipLayout, "Browser switch restores saved frame proportions");
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(4));
            Check(edge.HasExited, "Exit closes the active dedicated Edge");
            Console.WriteLine("Browser switch: deferred settings, Chrome/Edge P2 -> P3, background autoplay/PiP, native frame capture, independent profiles, geometry and exit passed.");
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    private static Task ReadyAsync(MainWindow window, int part) => WaitForAsync(() => window.Services.Follow.Current is
        { Phase: FollowPhase.Following, Video: { Paused: false, PictureInPicture: true } } state && state.Request?.Identity.Part == part,
        $"Bilibili P{part} autoplay and PiP", window);

    private static async Task CaptureAsync(MainWindow window, VideoController video, BrowserKind kind, int part)
    {
        var current = window.Services.Follow.Current;
        using var frame = await video.FrameSource.CaptureAsync(current.Page!, current.Request!.Identity, new(), CancellationToken.None);
        Check(frame is not null && frame.SourceWidth > 0 && frame.Region.Width == frame.SourceWidth / 3 && frame.Region.Height == frame.SourceHeight / 2,
            $"{kind} P{part} captures the original decoded one-sixth frame");
        var account = await window.Services.VideoAccount!.ReadAccountAsync(current.Page!);
        Console.WriteLine($"{kind} P{part}: decoded {frame!.SourceWidth}x{frame.SourceHeight}, account={account.LoggedIn}, time={current.Video!.CurrentTime:0.0}s.");
    }
}
