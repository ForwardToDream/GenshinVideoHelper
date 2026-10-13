using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.App.Controls;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Infrastructure.Library;

namespace GenshinVideoHelper.Smoke;

/// <summary>The progress page driven through its real controls, against the fixture episode list.</summary>
internal static class ProgressUiTests
{
    public static void RunStandalone(string root)
    {
        var app = CreateTestApplication();
        var work = app.Dispatcher.InvokeAsync(() =>
        {
            var settings = new AppSettings { IsFirstLaunch = false, HotkeysEnabled = false,
                SelectedVideoLibraryId = VideoLibraryCatalog.DefaultLibraryId };
            settings.Vision.Enabled = false;
            var window = new GenshinVideoHelper.App.MainWindow(new AppServices(settings,
                TestArtifacts.PathFor(root, "progress-ui"), episodeFactory: PreviewService))
                { ShowActivated = false, ShowInTaskbar = false };
            try { window.Show(); Run(window, root); }
            finally { window.Close(); PumpUntil(window, () => window.ShutdownCompletion.IsCompleted, "Progress UI shutdown"); }
        }).Task;
        var frame = new DispatcherFrame();
        work.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { work.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    public static void Run(GenshinVideoHelper.App.MainWindow window, string root)
    {
        var progress = window.Services.Progress;
        var library = VideoLibraryLoader.LoadBuiltIn().Libraries.Single(library => library.Id == VideoLibraryCatalog.DefaultLibraryId);
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
        CheckVideoScrolling(window, root);

        videos.SelectedIndex = 5;
        Check(episodes.Items.Count == 4 && Find<TextBlock>("ProgressVideoTitle").Text == map.DisplayText, "Selecting a map shows its parts");
        long Cid(int part) => Episode(part).Cid;
        progress.MarkWatched(map.Bvid, Cid(1), 0, 24);
        progress.MarkWatched(map.Bvid, Cid(2), 0, Episode(2).Duration);
        progress.MarkWatched(map.Bvid, Cid(3), 0, 200);
        progress.MarkWatched(map.Bvid, Cid(3), 320, 410);
        progress.SetPosition(map.Bvid, Cid(3), 410);
        Check(Episode(1).Status == EpisodeStatus.Completed && Text(episodes.Items[0], "StateText") == "完成" && Text(videos.Items[5], "CountText") == "2/4",
            "Watched coverage alone determines completed parts");
        Check(Text(episodes.Items[2], "StateText") == "48%" && Find<TextBlock>("ProgressTotalText").Text.StartsWith("已完成 2 / "), "Partial coverage is shown as a percentage and totals follow");

        episodes.SelectedIndex = 2;
        var timeline = Find<WatchTimeline>("ProgressTimeline");
        Check(Find<TextBlock>("ProgressEditorTitle").Text.StartsWith("P3 · ") && timeline.Duration == 604 && timeline.Segments!.Count == 2 && timeline.Position == 410 &&
              Find<TextBox>("ProgressPositionInput").Text == "06:50", "Editor shows the selected part's spans and resume point");
        Capture(window, root, "ui-progress-editing.png", 1020, 730);
        Capture(window, root, "ui-progress-editing-compact.png", 860, 600);
        Check(Find<Border>("ProgressEditor").ActualHeight > 0 && episodes.ActualHeight >= 90, "Minimum size keeps part list and editor visible");
        CheckEditorLayout(window);
        CheckTimelineHover(window, timeline);

        timeline.Select(100, 300);
        Check(Find<TextBox>("ProgressRangeStart").Text == "01:40" && Find<TextBox>("ProgressRangeEnd").Text == "05:00", "Dragging the timeline fills the range inputs");
        Check(Find<TextBlock>("StatusText").Visibility == Visibility.Collapsed, "Drag candidates do not show footer hints");
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
        Check(Episode(3) == before && Status().Contains("晚于") && Find<TextBlock>("StatusText").Visibility == Visibility.Visible,
            "A reversed range still shows an error");
        Find<TextBox>("ProgressRangeStart").Text = "abc";
        Click("ProgressMarkWatchedButton");
        Check(Episode(3) == before && Status().Contains("3:20"), "An unreadable time is rejected with an example");

        Invoke(window, "OnProgressTimeClicked", 120d);
        Check(Find<TextBox>("ProgressPositionInput").Text == "02:00" && timeline.CandidatePosition == 120 && timeline.Position == 410 &&
            Episode(3).Position == 410 && Find<TextBlock>("StatusText").Visibility == Visibility.Collapsed,
            "Clicking previews a separate resume candidate without saving it or showing footer hints");
        Invoke(window, "RefreshProgressEditor");
        Check(timeline.CandidatePosition == 120 && Find<TextBox>("ProgressPositionInput").Text == "02:00",
            "Ordinary refresh cannot erase the unsaved candidate");
        timeline.Select(100, 300);
        Capture(window, root, "ui-progress-candidates.png", 1020, 730);
        Capture(window, root, "ui-progress-candidates-compact.png", 860, 600);
        Click("ProgressSetPositionButton");
        Check(Episode(3).Position == 120 && timeline.Position == 120 && double.IsNaN(timeline.CandidatePosition) &&
            Find<TextBlock>("StatusText").Visibility == Visibility.Collapsed, "Saving converts the candidate to the saved marker without footer hints");
        Find<TextBox>("ProgressPositionInput").Text = "04:00";
        Check(timeline.CandidatePosition == 240 && Episode(3).Position == 120, "Typing a time updates only the candidate");
        episodes.SelectedIndex = 3;
        Check(double.IsNaN(timeline.CandidatePosition), "Changing episodes discards the previous candidate");
        episodes.SelectedIndex = 2;
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
        Check(Episode(3) is { Status: EpisodeStatus.Completed, Coverage: 1 } && Episode(4).Status == EpisodeStatus.NotStarted, "Confirmed bulk change fills watched coverage only before the selection");
        Click("ProgressUndoButton");
        Check(progress.Get(map.Bvid)!.Episodes.SequenceEqual(snapshot) && Status().Contains("已撤销"), "Undo restores the bulk change exactly");
        Click("ProgressResetVideoButton");
        Click("ProgressConfirmButton");
        Check(progress.Get(map.Bvid)!.Episodes.All(episode => episode.Status == EpisodeStatus.NotStarted), "Resetting a map clears every part");
        Click("ProgressUndoButton");
        Check(progress.Get(map.Bvid)!.Episodes.SequenceEqual(snapshot), "Undo restores a reset map");

        episodes.SelectedIndex = 0;
        timeline.Select(0, Episode(1).Duration);
        Click("ProgressMarkUnwatchedButton");
        Check(Episode(1).Status == EpisodeStatus.NotStarted && Episode(2).Status == EpisodeStatus.Completed,
            "Removing all watched coverage clears only that part's completion");
        Click("ProgressUndoButton");
        Check(Episode(1).Status == EpisodeStatus.Completed, "Undo restores a reset part");

        episodes.SelectedIndex = 2;
        Capture(window, root, "ui-progress-filled.png", 1020, 730);
        CheckEditorLayout(window);
        Capture(window, root, "ui-progress-filled-compact.png", 860, 600);
        CheckEditorLayout(window);

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
        Console.WriteLine("Progress page: scrolling, watched ranges, automatic completion, resume point, bulk changes, undo and start-page positioning passed.");
    }

    private static void CheckEditorLayout(GenshinVideoHelper.App.MainWindow window)
    {
        var editor = (Border)window.FindName("ProgressEditor");
        FrameworkElement Find(string name) => (FrameworkElement)window.FindName(name);
        Rect Bounds(string name)
        {
            var control = Find(name);
            return new(control.TranslatePoint(new Point(), editor), new Size(control.ActualWidth, control.ActualHeight));
        }
        foreach (var removed in new[] { "ProgressCompleteButton", "ProgressIncompleteButton", "ProgressAutoButton", "ProgressResetEpisodeButton", "ProgressFollowButton" })
            Check(window.FindName(removed) is null, "Removed episode controls are absent");
        var selection = Bounds("ProgressRangeStart");
        var rangeEnd = Bounds("ProgressRangeEnd");
        var watched = Bounds("ProgressMarkWatchedButton");
        var unwatched = Bounds("ProgressMarkUnwatchedButton");
        var resume = Bounds("ProgressPositionInput");
        var save = Bounds("ProgressSetPositionButton");
        var undo = Bounds("ProgressUndoButton");
        Check(Math.Abs((selection.Left + rangeEnd.Right) / 2 - editor.ActualWidth / 2) < 1 &&
            rangeEnd.Left - selection.Right >= 30, "Time inputs are centered with a wider gap");
        Check(watched.Top >= selection.Bottom + 7 && Math.Abs(watched.Left - editor.Padding.Left) < 1,
            "Watched buttons occupy the next row on the left");
        Check(Math.Abs(resume.Top - watched.Top) < 1 && unwatched.Right + 16 <= resume.Left &&
            Math.Abs(save.Right - (editor.ActualWidth - editor.Padding.Right)) < 1,
            "Resume controls align right without overlapping watched buttons");
        Check(undo.Top >= watched.Bottom + 7 && Math.Abs(undo.Right - save.Right) < 1,
            "Undo occupies the following row on the right");
        Check(!Descendants<TextBlock>(editor).Any(text => text.Text == "选区"), "Redundant range label is removed");
    }

    private static void CheckTimelineHover(GenshinVideoHelper.App.MainWindow window, WatchTimeline timeline)
    {
        var task = window.Dispatcher.InvokeAsync(async () =>
        {
            GetCursorPos(out var cursor);
            var topmost = window.Topmost;
            try
            {
                window.Topmost = true;
                await Task.Delay(100);
                window.UpdateLayout();
                async Task Move(double fraction)
                {
                    // Capture renders a logical layout; let native window resizing settle before mouse input.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        window.UpdateLayout();
                        var point = timeline.PointToScreen(new Point(timeline.ActualWidth * fraction, timeline.ActualHeight / 2));
                        SetCursorPos((int)point.X, (int)point.Y);
                        await Task.Delay(100);
                    }
                }
                await Move(.25);
                var tip = GetField<ToolTip>(timeline, "_timeTip");
                Check(tip is { IsOpen: true, Content: "02:31" }, "Hover immediately shows cursor time");
                var selection = (timeline.SelectionStart, timeline.SelectionEnd, timeline.Position);
                await Move(.75);
                Check(tip!.IsOpen && (string)tip.Content == "07:33", "Moving the cursor updates hover time");
                Check(selection.Equals((timeline.SelectionStart, timeline.SelectionEnd, timeline.Position)), "Hover does not change selection or resume position");
                timeline.Duration = 7200;
                await Move(.5);
                Check((string)tip.Content == "1:00:00", "Hover formats long video time");
                timeline.Duration = 0;
                Check(!tip.IsOpen, "Unknown duration hides hover time");
                timeline.Duration = 604;
                timeline.IsInteractive = false;
                Check(!tip.IsOpen, "Disabling timeline interaction hides hover time");
                timeline.IsInteractive = true;
                var outside = window.PointToScreen(new Point(200, 100));
                SetCursorPos((int)outside.X, (int)outside.Y);
                await Task.Delay(100);
                Check(!tip.IsOpen, "Leaving timeline hides hover time");
            }
            finally
            {
                timeline.Duration = 604;
                timeline.IsInteractive = true;
                window.Topmost = topmost;
                SetCursorPos(cursor.X, cursor.Y);
            }
        }).Task.Unwrap();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void CheckVideoScrolling(GenshinVideoHelper.App.MainWindow window, string root)
    {
        var list = (ListBox)window.FindName("ProgressVideoList");
        foreach (var (width, height) in new[] { (1020, 730), (860, 600) })
        {
            Capture(window, root, $"ui-progress-scroll-{width}.png", width, height);
            var scroll = (ScrollViewer)list.Template.FindName("LibraryScroll", list);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Console.WriteLine($"Progress viewport {width}x{height}: list={list.ActualHeight:F1}, extent={scroll.ExtentHeight:F1}, viewport={scroll.ViewportHeight:F1}, scrollable={scroll.ScrollableHeight:F1}.");
            Check(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0 &&
                scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Long map list exposes a vertical scrollbar");
            var bar = Descendants<ScrollBar>(scroll).Single(item => item.Orientation == Orientation.Vertical);
            Check(bar.ActualWidth <= 6 && bar.ActualWidth > 0, "Map scrollbar remains thin");
            scroll.ScrollToTop();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            PumpUntil(window, () => scroll.VerticalOffset == 0, "Map list returns to top");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.MouseWheelEvent };
            var visibleRow = Descendants<ListBoxItem>(list).First();
            visibleRow.RaiseEvent(wheel);
            PumpUntil(window, () => scroll.VerticalOffset > 0, "Mouse wheel scrolls the map list");
            var beforeDrag = scroll.VerticalOffset;
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 10) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 10, false) { RoutedEvent = Thumb.DragCompletedEvent });
            PumpUntil(window, () => scroll.VerticalOffset > beforeDrag, "Scrollbar thumb moves the map list");
            scroll.ScrollToBottom();
            PumpUntil(window, () => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "Map list reaches its bottom");
            Check(Descendants<ListBoxItem>(list).Any(item => item.Content == list.Items[^1]), "Last map is reachable");
            Capture(window, root, $"ui-progress-scroll-bottom-{width}.png", width, height);
            scroll.ScrollToTop();
            PumpUntil(window, () => scroll.VerticalOffset == 0, "Map list restores its top");
        }
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
