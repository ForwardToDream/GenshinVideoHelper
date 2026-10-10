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

internal static class UiTests
{
    public static void TestLibraryUi(GenshinVideoHelper.App.MainWindow window)
    {
        var settings = window.Services.Settings;
        var guideSelector = (ComboBox)window.FindName("GuideVideoSelector");
        var url = (TextBox)window.FindName("UrlInput");
        var beforeUrl = url.Text;
        var beforeState = GetField<VideoState>(window, "_state");
        var request = window.Services.Follow.Session.Current;
        Check(window.FindName("LibrarySelector") is null && window.FindName("DefaultUrlInput") is null, "Settings no longer contains library or default URL controls");
        Check(guideSelector.Items.Count == 22 && guideSelector.SelectedIndex == 0, "Library contains only maps and starts on first video");
        guideSelector.SelectedIndex = 12;
        Check(VideoIdentity.Parse(url.Text).Bvid == "BV1yJi8BmEk5" && beforeState == GetField<VideoState>(window, "_state") && request == window.Services.Follow.Session.Current, "Selecting map fills URL without navigating active video");
        url.Text = "https://www.bilibili.com/video/BV1MXfEY4EQ2/?p=3";
        Check(guideSelector.SelectedIndex == 0 && url.Text.EndsWith("?p=3"), "Manual link matches library by BV and preserves requested part");
        url.Text = "BV1hjgG6jEa6";
        Check(guideSelector.SelectedIndex == -1, "Custom video remains supported without a synthetic item");
        Invoke(window, "SelectLibrary", (object)null!);
        Check(settings.SelectedVideoLibraryId is null && guideSelector.Items.Count == 0 && url.Text == "BV1hjgG6jEa6", "Disabling library keeps manual URL intact");
        Invoke(window, "SelectLibrary", VideoLibraryLoader.LoadBuiltIn().Libraries.Single());
        Check(settings.SelectedVideoLibraryId == VideoLibraryCatalog.DefaultLibraryId && guideSelector.Items.Count == 22 && url.Text == VideoLibraryLoader.LoadBuiltIn().Libraries.Single().Videos[0].Url, "Changing library fills first map without navigating playback");
        url.Text = beforeUrl;
        Check(beforeState == GetField<VideoState>(window, "_state"), "Library changes preserve playback state");
    }
    public static void TestLibraryPicker(string root, VideoLibrary library, VideoLibrary second)
    {
        var preview = new GenshinVideoHelper.App.LibraryPickerWindow(new VideoLibraryCatalog([library], []), library.Id);
        Capture(preview, root, "ui-library-picker.png", 570, 490);
        Capture(preview, root, "ui-library-picker-compact.png", 460, 380);
        preview.Close();
        second = second with { CreatorName = "测试 UP" };
        var catalog = new VideoLibraryCatalog([library, second], []);
        var picker = new GenshinVideoHelper.App.LibraryPickerWindow(catalog, library.Id);
        var search = (TextBox)picker.FindName("SearchInput");
        var results = (ListBox)picker.FindName("LibraryResults");
        var apply = (Button)picker.FindName("UseLibraryButton");
        Check(results.Items.Count == 3 && results.SelectedIndex == 1 && apply.IsEnabled, "Picker shows libraries and selects current ID");
        Capture(picker, root, "ui-library-picker-multiple.png", 570, 490);
        Capture(picker, root, "ui-library-picker-multiple-compact.png", 460, 380);
        search.Text = "汉卿";
        Check(results.Items.Count == 1, "Search by UP name");
        search.Text = "测试 up 长标题";
        Check(results.Items.Count == 1 && results.SelectedItem is null, "Case-insensitive multiword search finds matching author and library without committing choice");
        results.SelectedIndex = 0;
        Capture(picker, root, "ui-library-picker-long.png", 460, 380);
        search.Text = "沉玉谷";
        Check(results.Items.Count == 1, "Search by map name inside library");
        search.Text = "bv1tt7s6yetr";
        Check(results.Items.Count == 1, "Search by BV ID case-insensitively");
        search.Text = "不存在的库-438625";
        Check(results.Items.Count == 0 && !apply.IsEnabled && ((TextBlock)picker.FindName("EmptyStateText")).Visibility == Visibility.Visible, "No results shows empty state and prevents confirmation");
        Capture(picker, root, "ui-library-picker-empty.png", 570, 490);
        Capture(picker, root, "ui-library-picker-empty-compact.png", 460, 380);
        search.Text = "手动输入";
        Check(results.Items.Count == 1, "Manual mode can be searched");
        results.SelectedIndex = 0;
        Check((string)apply.Content == "手动输入", "Manual choice has matching confirmation label");
        Invoke(picker, "ClearSearch_Click", picker, new RoutedEventArgs());
        Check(results.Items.Count == 3 && search.Text == "", "Clear button restores full list");
        picker.Close();
    }

    public static void TestLibraryPickerFlow(GenshinVideoHelper.App.MainWindow window)
    {
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        var settings = window.Services.Settings;
        var beforePage = window.Services.Follow.Current.Page;
        var beforeRequest = window.Services.Follow.Session.Current;
        var beforeUrl = ((TextBox)window.FindName("UrlInput")).Text;
        var beforeHeading = ((TextBlock)window.FindName("PageHeading")).Text;
        Exception? error = null;
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try
            {
                Check(picker.Owner == window, "Change-library button opens owned picker without navigating settings");
                ((TextBox)picker.FindName("SearchInput")).Text = "手动输入";
                ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0;
                ((Button)picker.FindName("UseLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { error = ex; }
            finally { if (picker.IsVisible) picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Check(settings.SelectedVideoLibraryId is null && ((ComboBox)window.FindName("GuideVideoSelector")).Items.Count == 0, "Confirming picker applies manual mode");
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try { ((TextBox)picker.FindName("SearchInput")).Text = "汉卿"; ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0; }
            catch (Exception ex) { error = ex; }
            finally { picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Check(settings.SelectedVideoLibraryId is null && window.Services.Follow.Current.Page == beforePage &&
               window.Services.Follow.Session.Current == beforeRequest &&
               ((TextBox)window.FindName("UrlInput")).Text == beforeUrl && ((TextBlock)window.FindName("PageHeading")).Text == beforeHeading, "Cancel preserves selection, current page, session and URL");
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try
            {
                ((TextBox)picker.FindName("SearchInput")).Text = "汉卿";
                ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0;
                ((Button)picker.FindName("UseLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { error = ex; }
            finally { if (picker.IsVisible) picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Check(settings.SelectedVideoLibraryId == VideoLibraryCatalog.DefaultLibraryId && ((ComboBox)window.FindName("GuideVideoSelector")).Items.Count == 22, "Confirming searched library restores its map list");
    }

    public static void TestBindingEditor(GenshinVideoHelper.App.MainWindow window)
    {
        var rows = ((ItemsControl)window.FindName("HotkeyRows")).ItemsSource.Cast<object>().ToArray();
        var row = rows.First(item => (HotkeyAction)item.GetType().GetProperty("Action")!.GetValue(item)! == HotkeyAction.NextEpisode);
        row.GetType().GetProperty("Keys")!.SetValue(row, "Alt+2");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Check(((TextBlock)window.FindName("BindingStatus")).Text.Contains("重复"), "Binding editor reports duplicates");
        var settings = window.Services.Settings;
        Check(settings.Hotkeys[HotkeyAction.NextEpisode] == "Alt+Right", "Invalid draft leaves active bindings unchanged");
        row.GetType().GetProperty("Keys")!.SetValue(row, "Ctrl+Shift+F8");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Check(settings.Hotkeys[HotkeyAction.NextEpisode] == "Ctrl+Shift+F8" && (string)((Button)window.FindName("NextEpisodeButton")).ToolTip == "Ctrl+Shift+F8", "Valid binding saves and updates tooltip without resetting video");
        var reverseRow = rows.Single(item => (HotkeyAction)item.GetType().GetProperty("Action")!.GetValue(item)! == HotkeyAction.ReversePipVisibility);
        reverseRow.GetType().GetProperty("Keys")!.SetValue(reverseRow, "F8");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Check(settings.Hotkeys[HotkeyAction.ReversePipVisibility] == "F8", "Visibility-mode row saves a single key through normal binding editor");
        Invoke(window, "ResetBindings_Click", window, new RoutedEventArgs());
        Check(settings.Hotkeys[HotkeyAction.NextEpisode] == "Ctrl+Shift+F8", "Filling defaults does not apply until saved");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Check(settings.Hotkeys[HotkeyAction.NextEpisode] == "Alt+Right" && settings.Hotkeys[HotkeyAction.ReversePipVisibility] == "~", "Default reset is applied on save including mode key");
        ((TextBlock)window.FindName("BindingStatus")).Text = "下一分集：Alt+2 与另一项绑定重复。请修改后再保存。";
    }

    public static void RenderUi(string root)
    {
        var app = CreateTestApplication();
        var window = new GenshinVideoHelper.App.MainWindow(new AppServices(new AppSettings { HotkeysEnabled = false }, TestArtifacts.PathFor(root, "ui-test-profile"), episodeFactory: PreviewService));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        Check(window.FindName("NavConnection") is null && window.FindName("NavPip") is null &&
               ((Grid)window.FindName("PagesHost")).Children.Count == 4, "Start, progress, hotkeys and settings pages");
        Check(window.FindName("OtherPagesExpander") is null && window.FindName("PageSelector") is null, "Other-video-page section removed");
        foreach (var (key, title) in new[] { ("Follow", "启动"), ("Progress", "进度"), ("Hotkeys", "快捷键"), ("Settings", "设置") })
        {
            ((RadioButton)window.FindName("Nav" + key)).IsChecked = true;
            Check(((TextBlock)window.FindName("PageHeading")).Text == title, "Navigate " + key);
            Capture(window, root, "ui-" + key.ToLowerInvariant() + ".png", 1020, 730);
            Capture(window, root, "ui-" + key.ToLowerInvariant() + "-compact.png", 860, 600);
        }
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        ((Button)window.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("请先开始跟随"), "Playback error feedback");
        using var json = JsonDocument.Parse(EpisodeFixture);
        var info = BilibiliEpisodeService.ParseVideoData(json.RootElement, new("BV1hjgG6jEa6", 3));
        window.Services.Preview.Select(new VideoIdentity(info.Bvid, 3).Url); window.Services.Preview.LoadAsync().GetAwaiter().GetResult();
        Invoke(window, "UpdateEpisodeControls");
        Check(((Button)window.FindName("PreviousEpisodeButton")).IsEnabled && ((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Interior part buttons enabled");
        window.Services.Preview.Select(new VideoIdentity(info.Bvid, 1).Url); window.Services.Preview.LoadAsync().GetAwaiter().GetResult();
        Invoke(window, "UpdateEpisodeControls");
        Check(!((Button)window.FindName("PreviousEpisodeButton")).IsEnabled, "First part previous disabled");
        window.Services.Preview.Select(new VideoIdentity(info.Bvid, 4).Url); window.Services.Preview.LoadAsync().GetAwaiter().GetResult();
        Invoke(window, "UpdateEpisodeControls");
        Check(!((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Last part next disabled");
        window.Services.Preview.Select(new VideoIdentity(info.Bvid, 3).Url); window.Services.Preview.LoadAsync().GetAwaiter().GetResult();
        Invoke(window, "UpdateEpisodeControls");
        var state = new VideoState("【原神7.0一条龙】全宝箱 / 神瞳 / 任务 / 成就 / 观景点 · 长标题布局验证",
            "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3", false, 321, 604, false, 1.5, true, 1920, 1080);
        Invoke(window, "UpdateVideo", state);
        TestLibraryUi(window);
        Capture(window, root, "ui-follow-connected.png", 1020, 730);
        Capture(window, root, "ui-follow-connected-compact.png", 860, 600);
        ((ComboBox)window.FindName("EpisodeSelector")).IsDropDownOpen = true;
        ((ComboBox)window.FindName("EpisodeSelector")).IsDropDownOpen = false;
        var before = ((TextBlock)window.FindName("VideoTitle")).Text;
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        ((RadioButton)window.FindName("NavHotkeys")).IsChecked = true;
        TestBindingEditor(window);
        Capture(window, root, "ui-hotkeys-edited.png", 1020, 730);
        Capture(window, root, "ui-hotkeys-edited-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        Capture(window, root, "ui-settings-expanded.png", 1020, 730);
        Capture(window, root, "ui-settings-expanded-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        Check(((TextBlock)window.FindName("VideoTitle")).Text == before && GetField<VideoState>(window, "_state") == state, "Settings navigation preserves video state");
        ProgressUiTests.Run(window, root);
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        ((Button)window.FindName("FollowRetryButton")).Visibility = Visibility.Visible;
        ((TextBlock)window.FindName("StatusText")).Text = "自动跟随未完成：浏览器请求被拒绝，请完成登录后重试。此处用长状态提示验证省略显示与完整悬停提示。";
        Capture(window, root, "ui-follow-error-compact.png", 860, 600);
        Capture(window, root, "ui-preview.png", 1020, 730);
        window.Left = -10000;
        window.Top = -10000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        CaptureEpisodeMenu(window, root, "ui-episode-menu.png");
        var guideSelector = (ComboBox)window.FindName("GuideVideoSelector");
        CaptureMenu(window, guideSelector, root, "ui-library-video-menu.png");
        TestLibraryPickerFlow(window);
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        ((ScrollViewer)window.FindName("SettingsPage")).ScrollToEnd();
        Capture(window, root, "ui-settings-bottom-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavHotkeys")).IsChecked = true;
        window.UpdateLayout();
        var captureButton = Descendants<Button>((ItemsControl)window.FindName("HotkeyRows")).First(button => (string?)button.Content == "录入");
        captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var box = GetField<TextBox>(window, "_capturingBox")!;
        Check(box.IsReadOnly, "Key recording starts without changing existing text");
        var original = box.Text;
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Invoke(window, "Binding_PreviewKeyDown", box, escape);
        Check(escape.Handled && !box.IsReadOnly && box.Text == original && GetField<TextBox>(window, "_capturingBox") is null, "Escape cancels recording and preserves binding");
        var reverseCapture = Descendants<Button>((ItemsControl)window.FindName("HotkeyRows")).Single(button =>
            button.DataContext is { } row && (HotkeyAction)row.GetType().GetProperty("Action")!.GetValue(row)! == HotkeyAction.ReversePipVisibility);
        reverseCapture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reverseBox = GetField<TextBox>(window, "_capturingBox")!;
        var tildeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Oem3) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Invoke(window, "Binding_PreviewKeyDown", reverseBox, tildeKey);
        Check(tildeKey.Handled && reverseBox.Text == "~" && !reverseBox.IsReadOnly && GetField<TextBox>(window, "_capturingBox") is null, "Physical tilde recording produces a single-key mode binding");
        var hotkeysPage = (Grid)window.FindName("HotkeysPage");
        window.Width = 860;
        window.Height = 600;
        window.UpdateLayout();
        ((ScrollViewer)hotkeysPage.Children[0]).ScrollToEnd();
        Capture(window, root, "ui-hotkeys-bottom-compact.png", 860, 600);
        window.Close();
        var library = VideoLibraryLoader.LoadBuiltIn().Libraries.Single();
        var second = library with { Id = "long-test-library", Name = "仅测试 · " + string.Concat(Enumerable.Repeat("长标题的一条龙地图导航视频库", 5)), Videos = [library.Videos[0] with { Title = string.Concat(Enumerable.Repeat("蒙德、璃月、龙脊雪山 · 长标题", 5)) }] };
        var multipleSettings = new AppSettings { HotkeysEnabled = false, SelectedVideoLibraryId = second.Id };
        var multipleWindow = new GenshinVideoHelper.App.MainWindow(new AppServices(multipleSettings, TestArtifacts.PathFor(root, "ui-test-profile"), libraries: new VideoLibraryCatalog([library, second], []), episodeFactory: PreviewService));
        Check(GetField<VideoLibrary>(multipleWindow, "_activeLibrary")!.Id == second.Id && ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).Items.Count == 1, "Multiple-library saved ID selects matching list instead of first library");
        ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).SelectedIndex = 0;
        Capture(multipleWindow, root, "ui-library-long-title-compact.png", 860, 600);
        Check(((TextBlock)multipleWindow.FindName("LibrarySummaryText")).ActualHeight <= 24, "Long library summary remains one line and keeps playback controls visible");
        ((RadioButton)multipleWindow.FindName("NavSettings")).IsChecked = true;
        Capture(multipleWindow, root, "ui-library-long-settings-compact.png", 860, 600);
        Invoke(multipleWindow, "SelectLibrary", library);
        Check(multipleSettings.SelectedVideoLibraryId == library.Id && ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).Items.Count == 22, "Switching between two libraries reloads correct map list");
        multipleWindow.Close();
        TestLibraryPicker(root, library, second);
        var missingWindow = new GenshinVideoHelper.App.MainWindow(new AppServices(new AppSettings { HotkeysEnabled = false, SelectedVideoLibraryId = "missing-library" }, TestArtifacts.PathFor(root, "ui-test-profile"), episodeFactory: PreviewService));
        Check(((TextBlock)missingWindow.FindName("StatusText")).Text.Contains("不存在") && ((ComboBox)missingWindow.FindName("GuideVideoSelector")).Items.Count == 0 && ((TextBox)missingWindow.FindName("UrlInput")).IsEnabled, "Missing library preserves manual input and reports error");
        Capture(missingWindow, root, "ui-library-missing-compact.png", 860, 600);
        missingWindow.Close();
        Console.WriteLine("WPF UI: four panels, progress editing, default/minimum sizes, full-row Start below episodes, part boundaries, long title and state preservation passed.");
    }

}
