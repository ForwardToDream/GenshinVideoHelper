using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class HistoryUiTests
{
    private sealed class Browser : IBrowserSession
    {
        public Func<string, Task<BrowserPage>>? Open;
        private BrowserPage? _page;
        public async Task<BrowserPage> OpenAsync(string url, CancellationToken token = default)
        {
            var page = Open is null ? Page(url) : await Open(url);
            if (!token.IsCancellationRequested) _page = page;
            return page;
        }
        public static BrowserPage Page(string url) => new("history-fixture", "video", url, "ws://fixture");
        public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default)
        { _page = page with { Url = identity.Url }; return Task.CompletedTask; }
        public Task<BrowserPage?> GetPageAsync(string id, CancellationToken token = default) => Task.FromResult(_page);
        public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => Task.FromResult(0);
        public Task CloseAsync() => Task.CompletedTask;
    }
    private sealed class Video : IVideoPlayer
    {
        public Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command, CancellationToken cancellationToken = default) =>
            Task.FromException<VideoState>(new VideoNotReadyException("测试媒体正在加载。"));
    }
    private sealed class Episodes : IEpisodeProvider
    {
        public Task<BilibiliVideoInfo> ReadAsync(BrowserPage page, CancellationToken token = default) => ReadFromApiAsync(VideoIdentity.Parse(page.Url), token);
        public Task<BilibiliVideoInfo> ReadFromApiAsync(VideoIdentity identity, CancellationToken token = default) =>
            Task.FromResult(new BilibiliVideoInfo(identity.Bvid, "历史测试", identity.Part,
                Enumerable.Range(1, 4).Select(part => new EpisodeInfo(part, part, $"分集 {part}", 60)).ToArray()));
    }

    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) => frame.Continue = false;
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        try { Check(task.IsCompleted, "History UI finishes within timeout"); task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string root)
    {
        using var folder = TestArtifacts.CreateTemp(root, "history-ui");
        var config = Path.Combine(folder.Path, "config.json");
        var store = new JsonSettingsStore(config);
        var settings = store.Load().Settings;
        settings.IsFirstLaunch = false; settings.HotkeysEnabled = false; settings.Vision.Enabled = false;
        var browser = new Browser();
        var window = new MainWindow(new AppServices(settings, Path.Combine(folder.Path, "Chrome"), store,
            browser: browser, video: new Video(), episodes: new Episodes()));
        var url = (TextBox)window.FindName("UrlInput");
        var toggle = (ToggleButton)window.FindName("LinkHistoryToggle");
        var popup = (Popup)window.FindName("LinkHistoryPopup");
        var list = (ListBox)window.FindName("LinkHistoryList");
        var library = VideoLibraryLoader.LoadBuiltIn().Libraries[0];
        Task Follow() => (Task)Invoke(window, "OpenVideoAsync")!;
        Button DeleteAt(int index) => Descendants<Button>((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).Single(button =>
            AutomationProperties.GetName(button) is "删除记录" or "确认删除");
        Button SelectAt(int index) => Descendants<Button>((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).Single(button =>
            AutomationProperties.GetName(button).StartsWith("https://"));
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        GetCursorPos(out var cursor);
        async Task ClickToggle()
        {
            var center = toggle.PointToScreen(new Point(toggle.ActualWidth / 2, toggle.ActualHeight / 2));
            var point = new ScreenPoint { X = (int)center.X, Y = (int)center.Y };
            GetWindowThreadProcessId(WindowFromPoint(point), out var owner);
            Check(owner == Environment.ProcessId, "Mouse test targets only its own window");
            SetCursorPos(point.X, point.Y);
            mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
            await Task.Delay(80);
            await Render();
        }
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = true; window.Show();
            await Render();
            Check(toggle.Visibility == Visibility.Visible && settings.ManualFollowHistory.Count == 0, "Manual mode exposes history");
            await ClickToggle();
            Check(popup.IsOpen, "Mouse click opens history");
            await ClickToggle();
            Check(!popup.IsOpen, "Second mouse click closes history");
            await ClickToggle();
            Check(popup.IsOpen && ((TextBlock)window.FindName("LinkHistoryEmpty")).Visibility == Visibility.Visible, "Empty history dropdown");
            CapturePopup(popup, url, root, "ui-history-empty.png");
            popup.IsOpen = false;
            url.Text = "BV1hjgG6jEa6";
            await Render();
            Check(settings.ManualFollowHistory.Count == 0, "Typing and preview do not create history");
            await Follow();
            url.Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=test&p=3";
            await Follow();
            url.Text = "BV1hjgG6jEa6";
            await Follow();
            Check(settings.ManualFollowHistory.Select(entry => VideoIdentity.Parse(entry.Url).Part).SequenceEqual([1, 3]),
                "Canonical parts, newest first, repeated follow moves link to top while media is loading");
            Check(new JsonSettingsStore(config).Load().Settings.ManualFollowHistory.SequenceEqual(settings.ManualFollowHistory), "History persists across reload");
            Invoke(window, "SelectLibrary", library);
            Check(toggle.Visibility == Visibility.Collapsed && !popup.IsOpen, "Library mode hides history");
            await Follow();
            url.Text = "BV1E9duBYEBV";
            await Follow();
            Check(settings.ManualFollowHistory.Count == 2, "Library follows and custom overrides in library mode do not record");
            var heldLibrary = new TaskCompletionSource<BrowserPage>();
            browser.Open = _ => heldLibrary.Task;
            url.Text = library.Videos[0].Url;
            var pendingLibrary = Follow();
            Invoke(window, "SelectLibrary", (object)null!);
            heldLibrary.SetResult(Browser.Page(library.Videos[0].Url));
            await pendingLibrary;
            Check(settings.ManualFollowHistory.Count == 2, "Changing mode during open cannot turn library follow into manual history");
            browser.Open = _ => Task.FromException<BrowserPage>(new IOException("浏览器启动失败"));
            url.Text = "BV1E9duBYEBV";
            await Follow();
            Check(settings.ManualFollowHistory.Count == 2, "Failed opens do not record history");
            var held = new TaskCompletionSource<BrowserPage>();
            browser.Open = _ => held.Task;
            url.Text = "BV1MXfEY4EQ2";
            var oldFollow = Follow();
            browser.Open = null;
            url.Text = "BV1E9duBYEBV";
            await Follow();
            held.SetResult(Browser.Page(new VideoIdentity("BV1MXfEY4EQ2", 1).Url));
            await oldFollow;
            Check(settings.ManualFollowHistory.Count == 3 && settings.ManualFollowHistory[0].Url.Contains("BV1E9duBYEBV") &&
                settings.ManualFollowHistory.All(entry => !entry.Url.Contains("BV1MXfEY4EQ2")), "Canceled old follow cannot add or reorder history");
            var active = window.Services.Follow.Session.Current;
            var original = settings.ManualFollowHistory.ToArray();
            toggle.IsChecked = true;
            await Render();
            Capture(window, root, "ui-history-default.png", 1020, 730);
            CapturePopup(popup, url, root, "ui-history-menu-default.png");
            var delete = DeleteAt(0);
            var typed = url.Text;
            Click(delete);
            await Render();
            Check(AutomationProperties.GetName(delete) == "确认删除" && settings.ManualFollowHistory.Count == 3 && url.Text == typed,
                "First trash click only arms confirmation without selecting URL");
            CapturePopup(popup, url, root, "ui-history-confirm-delete.png");
            popup.IsOpen = false;
            toggle.IsChecked = true;
            await Render();
            Check(AutomationProperties.GetName(DeleteAt(0)) == "删除记录", "Closing dropdown cancels confirmation");
            Click(DeleteAt(0)); Click(DeleteAt(1));
            await Render();
            Check(AutomationProperties.GetName(DeleteAt(0)) == "删除记录" && AutomationProperties.GetName(DeleteAt(1)) == "确认删除",
                "Only the last armed row can confirm");
            Click(DeleteAt(1));
            await Render();
            Check(settings.ManualFollowHistory.Count == 2 && !settings.ManualFollowHistory.Contains(original[1]) && url.Text == typed,
                "Second click deletes only that entry without selecting it");
            Check(new JsonSettingsStore(config).Load().Settings.ManualFollowHistory.Count == 2, "Deletion persists");
            Click(SelectAt(1));
            Check(!popup.IsOpen && url.Text == settings.ManualFollowHistory[1].Url && window.Services.Follow.Session.Current == active,
                "History selection fills URL and previews without changing active playback");
            Capture(window, root, "ui-history-compact.png", 860, 600);
            toggle.IsChecked = true;
            await Render();
            CapturePopup(popup, url, root, "ui-history-menu-compact.png");
            while (settings.ManualFollowHistory.Count > 0)
            { Click(DeleteAt(0)); Click(DeleteAt(0)); await Render(); }
            Check(((TextBlock)window.FindName("LinkHistoryEmpty")).Visibility == Visibility.Visible && list.Items.Count == 0,
                "Deleting the last record restores empty state");
            popup.IsOpen = false;
            settings.ManualFollowHistory = Enumerable.Range(1, 30).Select(part =>
                new FollowLinkEntry(new VideoIdentity("BV1hjgG6jEa6", part).Url, DateTimeOffset.UtcNow.AddMinutes(-part))).ToList();
            toggle.IsChecked = true;
            await Render();
            Check(list.Items.Count == 30 && Descendants<ScrollViewer>(list).Single().ScrollableHeight > 0, "Long history is scrollable");
            CapturePopup(popup, url, root, "ui-history-scroll.png");
            ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
            Check(!popup.IsOpen, "Navigation closes history popup");
        }
        finally { window.Close(); await window.ShutdownCompletion; SetCursorPos(cursor.X, cursor.Y); }
        Console.WriteLine("History UI: manual-only recording, ordering, persistence, cancellation, selection, two-click deletion and default/minimum layouts passed.");
    }

    private static async Task Render() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static void CapturePopup(Popup popup, TextBox input, string root, string name)
    {
        var child = (FrameworkElement)popup.Child;
        child.UpdateLayout();
        Check(child.ActualHeight <= 310 && Math.Abs(child.ActualWidth - input.ActualWidth) < 1, "History dropdown height and input width");
        var start = child.PointToScreen(new Point());
        var end = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight));
        var inputWidth = input.PointToScreen(new Point(input.ActualWidth, 0)).X - input.PointToScreen(new Point()).X;
        Check(Math.Abs(end.X - start.X - inputWidth) <= 2, "Popup physical width matches input under display scaling");
        var dpi = VisualTreeHelper.GetDpi(child);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(end.X - start.X), (int)Math.Ceiling(end.Y - start.Y),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, "artifacts", name));
        encoder.Save(file);
    }
}
