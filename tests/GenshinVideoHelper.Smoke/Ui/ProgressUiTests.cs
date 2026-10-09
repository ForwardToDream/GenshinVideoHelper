using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App.Controls;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Infrastructure.Library;

namespace GenshinVideoHelper.Smoke;

/// <summary>The progress page driven through its real controls, against the fixture episode list.</summary>
internal static class ProgressUiTests
{
    public static void Run(GenshinVideoHelper.App.MainWindow window, string root)
    {
        var progress = window.Services.Progress;
        var library = VideoLibraryLoader.LoadBuiltIn().Libraries.Single();
        // Not the first map: the library tests rely on that one having no progress.
        var map = library.Videos[5];
        T Find<T>(string name) where T : class => (T)window.FindName(name);
        void Click(string name)
        {
            var button = Find<Button>(name);
            Check(button.IsEnabled, name + " is enabled when used");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        string Status() => Find<TextBlock>("StatusText").Text;
        EpisodeProgress Episode(int part) => progress.Get(map.Bvid)!.FindPart(part)!;
        static string Text(object row, string property) => (string)row.GetType().GetProperty(property)!.GetValue(row)!;

        Find<RadioButton>("NavProgress").IsChecked = true;
        var videos = Find<ListBox>("ProgressVideoList");
        var episodes = Find<ListBox>("ProgressEpisodeList");
        Check(videos.Items.Cast<object>().Take(library.Videos.Count).Select(row => Text(row, "Title")).SequenceEqual(library.Videos.Select(video => video.DisplayText)) &&
              videos.Items.Cast<object>().Skip(library.Videos.Count).All(row => Text(row, "Group") == "其他视频"), "Progress page lists every map of the library first, other videos after");
        PumpUntil(window, () => library.Videos.All(video => progress.Get(video.Bvid) is { PartsKnown: true }), "Parts of every map are read in the background");
        Check(Find<TextBlock>("ProgressTotalText").Text == $"已完成 0 / {library.Videos.Count * 4} 集", "Overview counts every known part");

        videos.SelectedIndex = 5;
        Check(episodes.Items.Count == 4 && Find<TextBlock>("ProgressVideoTitle").Text == map.DisplayText, "Selecting a map shows its parts");
        long Cid(int part) => Episode(part).Cid;
        progress.MarkWatched(map.Bvid, Cid(1), 0, 24);
        progress.SetMark(map.Bvid, Cid(2), ProgressMark.Completed);
        progress.MarkWatched(map.Bvid, Cid(3), 0, 200);
        progress.MarkWatched(map.Bvid, Cid(3), 320, 410);
        progress.SetPosition(map.Bvid, Cid(3), 410);
        Check(Episode(1).Status == EpisodeStatus.Completed && Text(episodes.Items[0], "StateText") == "完成" && Text(videos.Items[5], "CountText") == "2/4",
            "Fully watched and manually completed parts both count as completed");
        Check(Text(episodes.Items[2], "StateText") == "48%" && Find<TextBlock>("ProgressTotalText").Text.StartsWith("已完成 2 / "), "Partial coverage is shown as a percentage and totals follow");

        episodes.SelectedIndex = 2;
        var timeline = Find<WatchTimeline>("ProgressTimeline");
        Check(Find<TextBlock>("ProgressEditorTitle").Text.StartsWith("P3 · ") && timeline.Duration == 604 && timeline.Segments!.Count == 2 && timeline.Position == 410 &&
              Find<TextBox>("ProgressPositionInput").Text == "06:50", "Editor shows the selected part's spans and resume point");
        Capture(window, root, "ui-progress-editing.png", 1020, 730);
        Capture(window, root, "ui-progress-editing-compact.png", 860, 600);
        Check(Find<Border>("ProgressEditor").ActualHeight > 0 && episodes.ActualHeight >= 90, "Minimum size keeps part list and editor visible");

        timeline.Select(100, 300);
        Check(Find<TextBox>("ProgressRangeStart").Text == "01:40" && Find<TextBox>("ProgressRangeEnd").Text == "05:00", "Dragging the timeline fills the range inputs");
        Click("ProgressMarkUnwatchedButton");
        Check(Episode(3).Segments.SequenceEqual([new(0, 100), new(320, 410)]) && Status().Contains("未看") && !timeline.HasSelection, "Marking a range unwatched removes exactly that span");
        Find<TextBox>("ProgressRangeStart").Text = "5:00";
        Find<TextBox>("ProgressRangeEnd").Text = "5:20";
        Check(timeline.HasSelection && timeline.SelectionStart == 300 && timeline.SelectionEnd == 320, "Typed times select the same range on the timeline");
        Click("ProgressMarkWatchedButton");
        Check(Episode(3).Segments.SequenceEqual([new(0, 100), new(300, 410)]), "Marking a typed range watched joins the adjacent span");
        var before = Episode(3);
        Find<TextBox>("ProgressRangeStart").Text = "9:00";
        Find<TextBox>("ProgressRangeEnd").Text = "8:00";
        Click("ProgressMarkWatchedButton");
        Check(Episode(3) == before && Status().Contains("晚于"), "A reversed range is rejected with an explanation");
        Find<TextBox>("ProgressRangeStart").Text = "abc";
        Click("ProgressMarkWatchedButton");
        Check(Episode(3) == before && Status().Contains("3:20"), "An unreadable time is rejected with an example");

        Invoke(window, "OnProgressTimeClicked", 120d);
        Check(Find<TextBox>("ProgressPositionInput").Text == "02:00" && Episode(3).Position == 410, "Clicking the timeline proposes a resume point without saving it");
        Click("ProgressSetPositionButton");
        Check(Episode(3).Position == 120 && timeline.Position == 120, "Resume point is saved on request");
        Find<TextBox>("ProgressPositionInput").Text = "20:00";
        Click("ProgressSetPositionButton");
        Check(Episode(3).Position == 120 && Status().Contains("超出"), "A resume point beyond the part is rejected");

        episodes.SelectedIndex = 3;
        var snapshot = progress.Get(map.Bvid)!.Episodes.ToArray();
        Click("ProgressCompleteBeforeButton");
        Check(Find<Border>("ProgressConfirmPanel").Visibility == Visibility.Visible && Find<WrapPanel>("ProgressBulkPanel").Visibility == Visibility.Collapsed &&
              Find<TextBlock>("ProgressConfirmText").Text.Contains("P4 之前的 1 集") && Episode(3).Status == EpisodeStatus.InProgress, "Bulk change waits for confirmation");
        Click("ProgressCancelButton");
        Check(Find<Border>("ProgressConfirmPanel").Visibility == Visibility.Collapsed && Episode(3).Status == EpisodeStatus.InProgress, "Cancelling a bulk change leaves everything as it was");
        Click("ProgressCompleteBeforeButton");
        Click("ProgressConfirmButton");
        Check(Episode(3) is { Status: EpisodeStatus.Completed, IsManual: true } && Episode(4).Status == EpisodeStatus.NotStarted, "Confirmed bulk change completes only the parts before the selection");
        Click("ProgressUndoButton");
        Check(progress.Get(map.Bvid)!.Episodes.SequenceEqual(snapshot) && Status().Contains("已撤销"), "Undo restores the bulk change exactly");
        Click("ProgressResetVideoButton");
        Click("ProgressConfirmButton");
        Check(progress.Get(map.Bvid)!.Episodes.All(episode => episode.Status == EpisodeStatus.NotStarted), "Resetting a map clears every part");
        Click("ProgressUndoButton");
        Check(progress.Get(map.Bvid)!.Episodes.SequenceEqual(snapshot), "Undo restores a reset map");

        episodes.SelectedIndex = 0;
        Click("ProgressIncompleteButton");
        Check(Episode(1) is { Status: EpisodeStatus.InProgress, IsManual: true } && !Find<Button>("ProgressIncompleteButton").IsEnabled && Find<Button>("ProgressAutoButton").IsEnabled,
            "Manual not-completed overrides full coverage");
        Click("ProgressAutoButton");
        Check(Episode(1) is { Status: EpisodeStatus.Completed, IsManual: false } && !Find<Button>("ProgressAutoButton").IsEnabled, "Restoring automatic judgement follows coverage again");
        Click("ProgressResetEpisodeButton");
        Check(Episode(1).Status == EpisodeStatus.NotStarted && !Find<Button>("ProgressResetEpisodeButton").IsEnabled, "Resetting one part clears it without confirmation");
        Click("ProgressUndoButton");
        Check(Episode(1).Status == EpisodeStatus.Completed, "Undo restores a reset part");

        episodes.SelectedIndex = 2;
        Capture(window, root, "ui-progress-filled.png", 1020, 730);
        Capture(window, root, "ui-progress-filled-compact.png", 860, 600);

        // Start page: picking the map continues at its first part that is not completed.
        Find<RadioButton>("NavFollow").IsChecked = true;
        var guide = Find<ComboBox>("GuideVideoSelector");
        var url = Find<TextBox>("UrlInput");
        var previousUrl = url.Text;
        guide.SelectedIndex = 5;
        Check(VideoIdentity.Parse(url.Text) == new VideoIdentity(map.Bvid, 3) && Status().Contains("第一个未完成的 P3"), "Selecting a map continues at its first unfinished part");
        progress.CompleteAll(map.Bvid);
        guide.SelectedIndex = 4;
        guide.SelectedIndex = 5;
        Check(VideoIdentity.Parse(url.Text) == new VideoIdentity(map.Bvid, 1) && Status().Contains("全部完成"), "A finished map returns to its first part and says so");
        progress.Undo();
        url.Text = previousUrl;
        Console.WriteLine("Progress page: background part loading, timeline range and typed range editing, resume point, marks, confirmed bulk changes, undo and start-page positioning passed.");
    }

    // Runs queued dispatcher work, including awaited continuations, until the condition holds.
    private static void PumpUntil(Window window, Func<bool> ready, string label)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(30), label);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(15);
        }
    }
}
