using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class UpdateUiTests
{
    public static void Run(string root)
    {
        var dispatcher = Application.Current.Dispatcher;
        var task = dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string root)
    {
        var assembly = typeof(MainWindow).Assembly.GetName().Version!;
        var current = new Version(assembly.Major, assembly.Minor, assembly.Build);
        var newer = new Version(current.Major, current.Minor + 1, 0);
        var checker = new Checker { Result = new(newer, new("https://github.com/ForwardToDream/GenshinVideoHelper/releases")) };
        var window = new MainWindow(new AppServices(new AppSettings { HotkeysEnabled = false },
            TestArtifacts.PathFor(root, "update-ui"), episodeFactory: PreviewService, updateFactory: () => checker))
            { ShowActivated = false, ShowInTaskbar = false };
        var retry = (Button)window.FindName("CheckUpdateButton");
        var notice = (Button)window.FindName("UpdateNoticeButton");
        var state = (TextBlock)window.FindName("UpdateStateText");
        try
        {
            window.Show();
            await Until(() => checker.Calls == 1 && retry.IsEnabled, "Startup checks updates without waiting for a browser");
            Check(checker.Current == current && ((TextBlock)window.FindName("VersionText")).Text == $"v{current}", "Update comparison uses the displayed executable version");
            Check(notice.Visibility == Visibility.Visible && notice.Content.ToString()!.Contains(newer.ToString()), "New release appears as a nonmodal footer action");
            Check(window.Services.Follow.Session.Current is null, "Update notification does not open or change a video session");
            Capture(window, root, "ui-update-notice.png", 1020, 730);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check(checker.Calls == 1, "Repeated Loaded events do not repeat the startup check");

            checker.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(checker.Calls == 2 && !retry.IsEnabled, "Concurrent manual checks share the active request");
            ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
            Check(((TextBlock)window.FindName("PageHeading")).Text == "设置", "Network wait leaves navigation responsive");
            checker.Pending.SetResult(null);
            await Until(() => retry.IsEnabled, "Manual check completes");
            Check(notice.Visibility == Visibility.Collapsed && state.Text == "暂无新版本", "Equal or absent release clears obsolete update notices");

            checker.Pending = null;
            checker.Failure = new System.Net.Http.HttpRequestException("offline");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => retry.IsEnabled, "Offline check can be retried");
            Check(state.Text.Contains("暂时无法") && notice.Visibility == Visibility.Collapsed && window.Services.Follow.Session.Current is null,
                "Offline failure stays in update settings without a modal or playback error");
            var settings = (ScrollViewer)window.FindName("SettingsPage");
            settings.ScrollToEnd();
            await Task.Delay(20);
            Capture(window, root, "ui-update-offline.png", 1020, 730);
            window.Width = 860;
            window.Height = 600;
            window.UpdateLayout();
            settings.ScrollToEnd();
            await Task.Delay(20);
            Capture(window, root, "ui-update-offline-compact.png", 860, 600);
            var top = retry.TranslatePoint(new Point(), settings).Y;
            Check(top >= 0 && top + retry.ActualHeight <= settings.ActualHeight, "Update controls remain visible at the minimum size when scrolled to the bottom");
            checker.Failure = new TaskCanceledException("timeout");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(retry.IsEnabled && state.Text.Contains("暂时无法"), "Timeout is retryable rather than reported as up to date");

            checker.Failure = null;
            checker.Result = new(newer, new("https://github.com/ForwardToDream/GenshinVideoHelper/releases"));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(notice.Visibility == Visibility.Visible, "Retry after a network failure restores a new-release notice");
            Capture(window, root, "ui-update-settings-compact.png", 860, 600);

            checker.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!retry.IsEnabled, "Exit scenario has a pending network check");
            var watch = Stopwatch.StartNew();
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(checker.Token.IsCancellationRequested && checker.Disposed && watch.Elapsed < TimeSpan.FromSeconds(1.5),
                "Exit cancels and disposes update checking without waiting for the network timeout");
        }
        finally { window.Close(); }
    }

    private static async Task Until(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Check(condition(), label);
    }

    private sealed class Checker : IUpdateChecker, IDisposable
    {
        public int Calls { get; private set; }
        public Version? Current { get; private set; }
        public CancellationToken Token { get; private set; }
        public ReleaseUpdate? Result { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource<ReleaseUpdate?>? Pending { get; set; }
        public bool Disposed { get; private set; }
        public Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken token = default)
        {
            Calls++;
            Current = currentVersion;
            Token = token;
            if (Failure is not null) return Task.FromException<ReleaseUpdate?>(Failure);
            return Pending?.Task.WaitAsync(token) ?? Task.FromResult(Result);
        }
        public void Dispose() => Disposed = true;
    }
}
