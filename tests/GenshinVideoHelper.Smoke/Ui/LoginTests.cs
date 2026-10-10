using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class LoginTests
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
        var browser = new Browser();
        var video = new Player();
        var services = new AppServices(new AppSettings { HotkeysEnabled = false, SelectedVideoLibraryId = null, VideoUrl = "" },
            TestArtifacts.PathFor(root, "login-profile"), browser: browser, video: video, episodeFactory: PreviewService);
        var window = new MainWindow(services) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        ((TextBox)window.FindName("UrlInput")).Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=1";
        try
        {
            await (Task)Invoke(window, "OpenVideoAsync")!;
            var prompt = Application.Current.Windows.OfType<LoginPromptWindow>().Single();
            Check(prompt.Owner == window && browser.Shows == 0, "Anonymous start opens owned prompt without activating Chrome");
            Capture(prompt, root, "ui-login.png", 450, 245);
            Capture(prompt, root, "ui-login-compact.png", 400, 245);
            ((Button)prompt.FindName("ContinueButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await services.Follow.PollAsync();
            Check(!Application.Current.Windows.OfType<LoginPromptWindow>().Any() && browser.Shows == 0, "Continue anonymously keeps playback and does not re-prompt on polls");
            await services.Follow.NavigateAsync(2);
            Check(!Application.Current.Windows.OfType<LoginPromptWindow>().Any(), "Switching parts never requests another prompt");

            await (Task)Invoke(window, "OpenVideoAsync")!;
            prompt = Application.Current.Windows.OfType<LoginPromptWindow>().Single();
            ((Button)prompt.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(browser.Shows == 1 && !Application.Current.Windows.OfType<LoginPromptWindow>().Any(), "Only login choice shows owned browser and dismisses prompt");
            video.LoggedIn = true;
            await UntilAsync(() => video.QualityRequests == 1);
            Check(!((TextBlock)window.FindName("StatusText")).Text.Contains("1920"), "Login is not treated as proof of HD");
            video.Height = 1080;
            await UntilAsync(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("1920×1080"));
            Check(browser.Shows == 1, "HD verification does not activate the browser repeatedly");

            await (Task)Invoke(window, "OpenVideoAsync")!;
            Check(!Application.Current.Windows.OfType<LoginPromptWindow>().Any(), "Logged-in start skips login prompt");
            var delayed = new TaskCompletionSource<BilibiliAccountState>(TaskCreationOptions.RunContinuationsAsynchronously);
            video.Pending = delayed.Task;
            await (Task)Invoke(window, "OpenVideoAsync")!;
            var staleUrl = services.Follow.Current.Page!.Url;
            await services.Follow.NavigateAsync(3);
            delayed.SetResult(new(staleUrl, false, 852, 480));
            await Task.Delay(100);
            Check(!Application.Current.Windows.OfType<LoginPromptWindow>().Any(), "Stale anonymous result cannot prompt after part switch");
            video.Pending = null; video.LoggedIn = false;
            await (Task)Invoke(window, "OpenVideoAsync")!;
            Check(Application.Current.Windows.OfType<LoginPromptWindow>().Count() == 1, "Fresh anonymous start prompts again");
            await services.Follow.NavigateAsync(4);
            Check(!Application.Current.Windows.OfType<LoginPromptWindow>().Any(), "Changing session closes stale modeless prompt");
            Console.WriteLine("Login UI: anonymous, continue, login, HD dimensions, part changes and stale results passed.");
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var i = 0; i < 100; i++) { if (ready()) return; await Task.Delay(50); }
        throw new TimeoutException("Login workflow test timed out.");
    }

    private sealed class Browser : IBrowserSession, IBrowserWindow
    {
        private BrowserPage? _page;
        public int Shows { get; private set; }
        public Task<BrowserPage> OpenAsync(string url, CancellationToken token = default) => Task.FromResult(_page = new("fixture", "Fixture", url, "ws://127.0.0.1:1"));
        public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default) { _page = page with { Url = identity.Url }; return Task.CompletedTask; }
        public Task<BrowserPage?> GetPageAsync(string targetId, CancellationToken token = default) => Task.FromResult(_page);
        public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => Task.FromResult(0);
        public Task CloseAsync() => Task.CompletedTask;
        public Task ShowAsync(BrowserPage page, CancellationToken token = default) { Shows++; return Task.CompletedTask; }
    }
    private sealed class Player : IVideoPlayer, IBilibiliAccountService
    {
        public bool? LoggedIn { get; set; } = false;
        public int Height { get; set; } = 480;
        public int QualityRequests { get; private set; }
        public Task<BilibiliAccountState>? Pending { get; set; }
        public Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command, CancellationToken cancellationToken = default) => throw new VideoNotReadyException("Fixture still loading");
        public Task<BilibiliAccountState> ReadAccountAsync(BrowserPage page, CancellationToken token = default) => Pending ?? Task.FromResult(new BilibiliAccountState(page.Url, LoggedIn, Height == 1080 ? 1920 : 852, Height));
        public Task RequestHighQualityAsync(BrowserPage page, CancellationToken token = default) { QualityRequests++; return Task.CompletedTask; }
    }
}
