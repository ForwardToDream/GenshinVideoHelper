using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class LibraryUiTests
{
    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timeout.Tick += (_, _) => frame.Continue = false;
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        try { Check(task.IsCompleted, "Library UI finishes within timeout"); task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string root)
    {
        using var folder = TestArtifacts.CreateTemp(root, "library-ui");
        var store = new JsonVideoLibraryStore(Path.Combine(folder.Path, "libraries"));
        var catalog = store.Load();
        var library = catalog.Libraries.Single(item => item.Id == VideoLibraryCatalog.DefaultLibraryId);
        var settings = new AppSettings { IsFirstLaunch = false, HotkeysEnabled = false, SelectedVideoLibraryId = library.Id };
        settings.Vision.Enabled = false;
        var window = new MainWindow(new AppServices(settings, Path.Combine(folder.Path, "Chrome"),
            new JsonSettingsStore(Path.Combine(folder.Path, "config.json")), episodeFactory: PreviewService, libraryStore: store))
            { ShowActivated = false, ShowInTaskbar = false };
        var picker = new LibraryPickerWindow(window.Services.Libraries, library.Id, store: store);
        picker.CatalogChanged += updated => Invoke(window, "OnLibraryCatalogChanged", updated);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var results = (ListBox)picker.FindName("LibraryResults");
        Button EditButton() => Descendants<Button>(results).Single(button => button.Tag is VideoLibrary item && item.Id == library.Id);

        async Task OpenEditor(Button button, Func<LibraryEditorWindow, Task> edit)
        {
            Task? driver = null;
            Exception? error = null;
            _ = picker.Dispatcher.BeginInvoke(new Action(() => driver = Drive()));
            async Task Drive()
            {
                var editor = Application.Current.Windows.OfType<LibraryEditorWindow>().Single();
                try { await edit(editor); }
                catch (Exception ex) { error = ex; }
                finally { if (editor.IsVisible) editor.Close(); }
            }
            Click(button);
            Check(driver is not null, "Editor button opens the common editor");
            await driver!;
            if (error is not null) throw error;
        }

        Task Save(LibraryEditorWindow editor) => (Task)Invoke(editor, "SaveAsync")!;
        try
        {
            window.Show(); picker.Owner = window; picker.Show(); picker.UpdateLayout();
            var beforeRequest = window.Services.Follow.Session.Current;
            var beforePage = window.Services.Follow.Current.Page;
            var beforeUrl = ((TextBox)window.FindName("UrlInput")).Text;
            Check(picker.FindName("ResultCountText") is null && ((Button)picker.FindName("AddLibraryButton")).IsEnabled,
                "Library count is replaced with an enabled add button");
            var manual = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
            Check(!Descendants<Button>(manual).Any(button => AutomationProperties.GetName(button) == "编辑视频库" && button.IsVisible),
                "Manual input has no edit button");
            Capture(picker, root, "ui-library-editable.png", 570, 680);
            Capture(picker, root, "ui-library-editable-compact.png", 460, 420);
            Capture(picker, root, "ui-library-editable.png", 570, 680);
            await OpenEditor(EditButton(), editor =>
            {
                Check(((TextBox)editor.FindName("LibraryIdInput")).IsReadOnly && editor.Rows.Count == library.Videos.Count,
                    "Editing preserves ID and loads every video");
                ((TextBox)editor.FindName("LibraryNameInput")).Text = "取消的改动";
                return Task.CompletedTask;
            });
            Check(store.Load().Libraries.Single(item => item.Id == library.Id).Name == library.Name, "Cancel writes nothing");

            await OpenEditor(EditButton(), async editor =>
            {
                ((TextBox)editor.FindName("LibraryNameInput")).Text = "编辑后的合集";
                editor.Rows[0].Title = "编辑后的地图";
                Capture(editor, root, "ui-library-editor.png", 740, 700);
                Capture(editor, root, "ui-library-editor-compact.png", 580, 440);
                await Save(editor);
            });
            Check(window.Services.Libraries.Libraries.Single(item => item.Id == library.Id).Name == "编辑后的合集" &&
                ((TextBlock)window.FindName("LibrarySummaryText")).Text.Contains("编辑后的合集") &&
                ((ComboBox)window.FindName("GuideVideoSelector")).Items.Cast<LibraryVideo>().First().Title == "编辑后的地图",
                "Saving an active library refreshes startup controls even before applying the picker");
            Check(settings.SelectedVideoLibraryId == library.Id && ((TextBox)window.FindName("UrlInput")).Text == beforeUrl &&
                window.Services.Follow.Session.Current == beforeRequest && window.Services.Follow.Current.Page == beforePage,
                "Editing keeps current selection, playback request and URL");

            ((TextBox)picker.FindName("SearchInput")).Text = "nothing-matches";
            await OpenEditor((Button)picker.FindName("AddLibraryButton"), async editor =>
            {
                Check(!((TextBox)editor.FindName("LibraryIdInput")).IsReadOnly, "New library ID is editable");
                ((TextBox)editor.FindName("LibraryIdInput")).Text = "my-library";
                ((TextBox)editor.FindName("LibraryNameInput")).Text = "我的合集";
                ((TextBox)editor.FindName("CreatorNameInput")).Text = "我的 UP";
                ((TextBox)editor.FindName("CreatorInput")).Text = "123";
                editor.Rows[0].Title = "第一张地图";
                editor.Rows[0].Url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3";
                Click((Button)editor.FindName("AddVideoButton"));
                editor.Rows[1].Title = "第二张地图";
                editor.Rows[1].Url = "BV1hjgG6jEa6";
                await Save(editor);
                Check(editor.IsVisible && ((TextBlock)editor.FindName("EditorStatusText")).Text.Contains("重复") &&
                    !File.Exists(Path.Combine(folder.Path, "libraries", "my-library.json")), "Duplicate videos leave the editor open without writing");
                Capture(editor, root, "ui-library-editor-error.png", 580, 440);
                editor.Rows[1].Url = "BV1E9duBYEBV";
                Click((Button)editor.FindName("AddVideoButton"));
                Invoke(editor, "RemoveVideo_Click", new Button { Tag = editor.Rows[2] }, new RoutedEventArgs());
                Check(editor.Rows.Count == 2, "Rows can be added and removed");
                await Save(editor);
            });
            Check(results.Items.Count == 6 && results.SelectedItem!.GetType().GetProperty("Library")!.GetValue(results.SelectedItem) is VideoLibrary { Id: "my-library" },
                "Saving clears the old filter and highlights the new library");
            var created = store.Load().Libraries.Single(item => item.Id == "my-library");
            Check(created.CreatorUrl == "https://space.bilibili.com/123/" && created.Videos[0].Bvid == "BV1hjgG6jEa6",
                "UID and video links normalize to the existing JSON schema");
            Invoke(window, "SelectLibrary", created);
            Check(((ComboBox)window.FindName("GuideVideoSelector")).Items.Count == 2 && settings.SelectedVideoLibraryId == created.Id,
                "New library can be selected normally");
            LibraryPickerScrollTests.Run(root, library);
            Console.WriteLine("Library UI: create/edit/cancel, active catalog refresh, input validation, row editing, search, compact layout and smooth scrolling passed.");
        }
        finally { picker.Close(); window.Close(); await window.ShutdownCompletion; }
    }
}
