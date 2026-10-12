using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class FirstLaunchUiTests
{
    public static void Run(string root)
    {
        var dispatcher = Application.Current.Dispatcher;
        var frame = new DispatcherFrame();
        var task = dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(root); }
            finally { frame.Continue = false; }
        }).Task.Unwrap();
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string root)
    {
        foreach (var action in new[] { "manual", "close", "library", "exit" })
        {
            using var temp = TestArtifacts.CreateTemp(root, "first-launch-" + action);
            var store = new JsonSettingsStore(Path.Combine(temp.Path, ".gvh", "config.json"));
            var settings = store.Load().Settings;
            Check(settings.IsFirstLaunch && settings.SelectedVideoLibraryId is null && settings.VideoUrl == "",
                "A fresh configuration starts with manual input and no preset URL");
            settings.HotkeysEnabled = false;
            store.Save(settings);
            var handler = new PreviewHandler();
            MainWindow CreateWindow() => new(new AppServices(store.Load().Settings,
                Path.Combine(temp.Path, ".gvh", "Chrome"), store, episodeFactory: () => new BilibiliEpisodeService(handler)))
                { ShowActivated = false, ShowInTaskbar = false };
            var window = CreateWindow();
            var library = window.Services.Libraries.Libraries.Single(item => item.Id == VideoLibraryCatalog.DefaultLibraryId);
            try
            {
                Check(((ComboBox)window.FindName("GuideVideoSelector")).Visibility == Visibility.Collapsed && handler.Requests.Count == 0,
                    "Manual startup hides the empty library list and does not fetch a video");
                var choose = ChooseAsync();
                window.Show();
                await choose.WaitAsync(TimeSpan.FromSeconds(5));
                var saved = store.Load().Settings;
                Check(!saved.IsFirstLaunch, "Showing the first-launch picker permanently consumes the prompt");
                if (action == "library")
                {
                    Check(saved.SelectedVideoLibraryId == library.Id && ((TextBox)window.FindName("UrlInput")).Text == library.Videos[0].Url &&
                        ((ComboBox)window.FindName("GuideVideoSelector")).Visibility == Visibility.Visible,
                        "First-launch library selection populates and persists its first video");
                }
                else Check(saved.SelectedVideoLibraryId is null && saved.VideoUrl == "", "Manual, close and exit keep the default manual input");
                Check(window.Services.Follow.Session.Current is null, "First-launch choices do not begin following or open a video session");
                if (action == "manual") Capture(window, root, "ui-follow-manual-default.png", 1020, 730);
            }
            finally { window.Close(); await window.ShutdownCompletion; }

            var restored = CreateWindow();
            try
            {
                restored.Show();
                await Task.Delay(100);
                Check(!Application.Current.Windows.OfType<LibraryPickerWindow>().Any(picker => picker.Owner == restored),
                    "Restarting from the saved configuration does not reopen the first-launch prompt");
                restored.Hide();
                restored.Show();
                await Task.Delay(50);
                Check(!Application.Current.Windows.OfType<LibraryPickerWindow>().Any(picker => picker.Owner == restored),
                    "Restoring the main window does not repeat the prompt");
                Check(restored.Services.Settings.SelectedVideoLibraryId == (action == "library" ? library.Id : null),
                    "Restart preserves the chosen library or manual input");
                Check(((TextBox)restored.FindName("UrlInput")).Text == (action == "library" ? library.Videos[0].Url : ""),
                    "Restart restores the first library video or an empty manual input");
            }
            finally { restored.Close(); await restored.ShutdownCompletion; }

            async Task ChooseAsync()
            {
                LibraryPickerWindow? picker = null;
                try
                {
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(3))
                    {
                        picker = Application.Current.Windows.OfType<LibraryPickerWindow>().FirstOrDefault(item => item.Owner == window);
                        if (picker is { IsLoaded: true }) break;
                        await Task.Delay(10);
                    }
                    Check(picker is { IsLoaded: true }, "First render opens the welcome picker");
                    if (picker is null) throw new InvalidOperationException("First-launch picker was not created.");
                    Check(!store.Load().Settings.IsFirstLaunch && ((TextBlock)picker.FindName("FirstLaunchIntro")).Visibility == Visibility.Visible,
                        "The prompt explains both choices and is persisted before dismissal");
                    var use = (Button)picker.FindName("UseLibraryButton");
                    var cancel = (Button)picker.FindName("CancelLibraryButton");
                    Check((string)cancel.Content == "保持手动输入" && !use.IsEnabled, "Manual input is the initial choice without a preselected library");
                    if (action == "manual")
                    {
                        Capture(picker, root, "ui-first-launch.png", 570, 680);
                        Capture(picker, root, "ui-first-launch-compact.png", 460, 420);
                        Invoke(cancel, "OnClick");
                    }
                    else if (action == "library")
                    {
                        ((TextBox)picker.FindName("SearchInput")).Text = library.Id;
                        var results = (ListBox)picker.FindName("LibraryResults");
                        Check(results.Items.Count == 1, "Welcome picker supports library search");
                        results.SelectedIndex = 0;
                        Check(use.IsEnabled, "A searched library can be selected");
                        use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (action == "exit") window.Close();
                    else picker.Close();
                }
                catch { picker?.Close(); window.Close(); throw; }
            }
        }
        Console.WriteLine("First launch: manual default, welcome search, library/manual/close/exit choices, saved configuration and one-time display passed.");
    }
}
