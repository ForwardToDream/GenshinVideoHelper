using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class PreviewTests
{
    public static void PreviewUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => PreviewUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    public static async Task PreviewUiTestAsync(string root)
    {
        var library = VideoLibraryLoader.LoadBuiltIn().Libraries.Single();
        var handler = new PreviewHandler();
        var settings = new AppSettings { HotkeysEnabled = false };
        var window = new GenshinVideoHelper.App.MainWindow(new AppServices(settings, TestArtifacts.PathFor(root, "preview-test-profile"), episodeFactory: () => new BilibiliEpisodeService(handler)));
        var url = (TextBox)window.FindName("UrlInput");
        var maps = (ComboBox)window.FindName("GuideVideoSelector");
        var parts = (ComboBox)window.FindName("EpisodeSelector");
        var retry = (Button)window.FindName("EpisodeRetryButton");
        var episodeStatus = (TextBlock)window.FindName("EpisodeStatusText");
        try
        {
            Check(url.Text == library.Videos[0].Url && maps.SelectedIndex == 0 && maps.Items.Count == 22, "Startup selects first real map without synthetic input item");
            await Until(() => parts.Items.Count == 4, "Startup automatically reads episodes before Chrome opens");
            Check(episodeStatus.Text == "" && episodeStatus.Visibility == Visibility.Collapsed, "Ready episodes do not show a helper line or reserve its space");
            Check(handler.Requests.Count == 1 && window.Services.Follow.Current.Page is null && window.Services.Follow.Session.Current is null, "Metadata preview does not create a browser session");
            Capture(window, root, "ui-follow-preloaded.png", 1020, 730);
            Capture(window, root, "ui-follow-preloaded-compact.png", 860, 600);
            Check(!((Button)window.FindName("PreviousEpisodeButton")).IsEnabled, "First preview part boundary");
            parts.SelectedIndex = 3;
            Check(url.Text.EndsWith("?p=4") && !((Button)window.FindName("NextEpisodeButton")).IsEnabled && handler.Requests.Count == 1, "Preselect P4 only edits link and reuses metadata");
            Check(settings.VideoUrl == "", "Preview does not persist an unfollowed URL");
            parts.SelectedIndex = 3;
            Check(handler.Requests.Count == 1, "Repeated selection does not reload");
            url.Clear();
            Check(maps.SelectedIndex == -1 && parts.Items.Count == 0 && !parts.IsEnabled, "Clearing URL enables manual entry without a mode choice");
            url.Text = "BV1hjgG6jEa6";
            url.Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=episodes&p=3";
            await Until(() => window.Services.Preview.Current.Info?.Bvid == "BV1hjgG6jEa6", "Typed link debounces into one read");
            Check(handler.Requests.Count == 2 && ((EpisodeInfo)parts.SelectedItem).Number == 3 && maps.SelectedIndex == -1, "Manual P3 and tracking parameters resolved before following");
            url.Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=99";
            Check(parts.Items.Count == 4 && parts.SelectedIndex == -1 && episodeStatus.Text.Contains("没有 P99"), "Invalid P preserves list for correction");
            parts.SelectedIndex = 0;
            Check(url.Text.EndsWith("?p=1"), "Invalid P can be corrected using preview selector");

            var held = new TaskCompletionSource<string>();
            handler.Reply = (bvid, token) => bvid == library.Videos[1].Bvid ? held.Task : Task.FromResult(PreviewHandler.Metadata(bvid, single: true));
            maps.SelectedIndex = 1;
            await Until(() => handler.Requests.Count == 3, "Selected map loads without Start");
            Check(window.Services.Follow.Current.Request is null, "Pending preview does not create an active session");
            maps.SelectedIndex = 2;
            await Until(() => window.Services.Preview.Current.Info?.Bvid == library.Videos[2].Bvid, "New selection overtakes slow metadata request");
            Check(handler.Requests[2].Token.IsCancellationRequested && parts.Items.Count == 1 && !((Button)window.FindName("PreviousEpisodeButton")).IsEnabled && !((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Old request canceled and single episode boundaries disabled");
            held.SetResult(PreviewHandler.Metadata(library.Videos[1].Bvid));
            await Task.Delay(100);
            Check(window.Services.Preview.Current.Info?.Bvid == library.Videos[2].Bvid && retry.Visibility == Visibility.Collapsed, "Late old result does not replace newer episodes or show failure");

            handler.Reply = (_, _) => Task.FromResult("{\"code\":-404}");
            maps.SelectedIndex = 3;
            await Until(() => retry.Visibility == Visibility.Visible, "Metadata failure exposes retry before following");
            Check(window.Services.Follow.Current.Page is null && episodeStatus.Text.Contains("可重试") && episodeStatus.Visibility == Visibility.Visible, "Failure still shows feedback without starting browser or blocking manual start");
            Capture(window, root, "ui-follow-preview-error-compact.png", 860, 600);
            handler.Reply = null;
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => parts.Items.Count == 4 && retry.Visibility == Visibility.Collapsed, "Retry recovers episode preview");

            handler.Reply = (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith<string>(_ => throw new OperationCanceledException(token));
            maps.SelectedIndex = 5;
            await Until(() => handler.Requests.Last().Bvid == library.Videos[5].Bvid, "Last pending request started");
            window.Close();
            Check(handler.Requests.Last().Token.IsCancellationRequested, "Closing window cancels preview");
        }
        finally { window.Close(); await window.ShutdownCompletion; }
        var restored = new GenshinVideoHelper.App.MainWindow(new AppServices(new AppSettings { HotkeysEnabled = false, VideoUrl = library.Videos[8].Url.Replace("?p=1", "?p=3") }, TestArtifacts.PathFor(root, "preview-test-profile"), episodeFactory: PreviewService));
        try
        {
            await Until(() => ((ComboBox)restored.FindName("EpisodeSelector")).Items.Count == 4, "Restore preloads saved in-library part");
            Check(((ComboBox)restored.FindName("GuideVideoSelector")).SelectedIndex == 8 && ((EpisodeInfo)((ComboBox)restored.FindName("EpisodeSelector")).SelectedItem).Number == 3, "Restore keeps saved map and P3");
        }
        finally { restored.Close(); await restored.ShutdownCompletion; }
        Console.WriteLine("Preview: startup/default map, manual/debounced P, cache, single/multiple parts, invalid P correction, retry, cancellation, stale results and active-session isolation passed.");

        static async Task Until(Func<bool> condition, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(condition(), label);
        }
    }

}
