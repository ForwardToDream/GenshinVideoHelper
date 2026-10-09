using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private const string OtherVideosGroup = "其他视频";
    private readonly ProgressLookup _progressLookup;
    private readonly HashSet<string> _progressLoadFailed = [];
    private readonly CancellationTokenSource _progressLoading = new();
    private ProgressVideoRow[] _progressVideos = [];
    private ProgressEpisodeRow[] _progressEpisodes = [];
    private string? _progressBvid;
    private long? _progressCid;
    private bool _updatingProgress, _loadingProgressParts;
    private Func<bool>? _pendingProgressAction;

    private WatchProgressService Progress => Services.Progress;
    private bool ProgressPageVisible => ProgressPage.Visibility == Visibility.Visible;

    private abstract class Row : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new(name));
        }
    }

    private sealed class ProgressVideoRow(string bvid, string title, string group) : Row
    {
        private string _countText = "";
        private double _ratio;
        public string Bvid { get; } = bvid;
        public string Title { get; } = title;
        public string Group { get; } = group;
        public string CountText { get => _countText; set => Set(ref _countText, value); }
        public double Ratio { get => _ratio; set => Set(ref _ratio, value); }
    }

    private sealed class ProgressEpisodeRow(long cid) : Row
    {
        private string _title = "", _stateText = "", _detail = "";
        private double _duration;
        private IReadOnlyList<WatchSegment> _segments = [];
        private Brush _dotFill = Brushes.Transparent, _dotStroke = Brushes.Transparent;
        private Visibility _checkVisibility = Visibility.Collapsed, _manualVisibility = Visibility.Collapsed;
        public long Cid { get; } = cid;
        public string Title { get => _title; set => Set(ref _title, value); }
        public string StateText { get => _stateText; set => Set(ref _stateText, value); }
        public string Detail { get => _detail; set => Set(ref _detail, value); }
        public double Duration { get => _duration; set => Set(ref _duration, value); }
        public IReadOnlyList<WatchSegment> Segments { get => _segments; set => Set(ref _segments, value); }
        public Brush DotFill { get => _dotFill; set => Set(ref _dotFill, value); }
        public Brush DotStroke { get => _dotStroke; set => Set(ref _dotStroke, value); }
        public Visibility CheckVisibility { get => _checkVisibility; set => Set(ref _checkVisibility, value); }
        public Visibility ManualVisibility { get => _manualVisibility; set => Set(ref _manualVisibility, value); }
    }

    /// <summary>Progress text for the start page's lists. Bindings also watch <see cref="Version"/>, so one
    /// increment refreshes every visible item without rebuilding the lists.</summary>
    private sealed class ProgressLookup(WatchProgressService progress, Func<string?> selectedBvid) : Row, IMultiValueConverter
    {
        private int _version;
        public int Version { get => _version; set => Set(ref _version, value); }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values[0] switch
        {
            EpisodeInfo part => selectedBvid() is { } bvid && progress.Get(bvid, part.Cid) is { } episode ? Short(episode) : "",
            LibraryVideo video => progress.Get(video.Bvid) is { } known ? Count(known) : "",
            _ => ""
        };

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private static string Short(EpisodeProgress episode) => episode.Status switch
    {
        EpisodeStatus.Completed => "已完成",
        EpisodeStatus.InProgress => Percent(episode.Coverage),
        _ => ""
    };

    private static string Count(VideoProgress video) =>
        video.PartsKnown ? $"{video.Completed}/{video.Episodes.Count}" : video.HasProgress ? "进行中" : "";

    // Rounded down so 100% is only ever shown for a fully watched episode.
    private static string Percent(double ratio) => $"{Math.Floor(ratio * 100):0}%";

    private void InitializeProgress()
    {
        Progress.Changed += OnProgressChanged;
        Progress.SaveFailed += OnProgressSaveFailed;
        ProgressTimeline.TimeClicked += OnProgressTimeClicked;
    }

    private void DisposeProgress()
    {
        Progress.Changed -= OnProgressChanged;
        Progress.SaveFailed -= OnProgressSaveFailed;
    }

    private void OnProgressSaveFailed(string message) => SetStatus($"进度未能保存，稍后自动重试：{message}", true);

    private void OnProgressChanged(string bvid)
    {
        _progressLookup.Version++;
        UpdateWatchProgressText();
        if (ProgressPageVisible) RefreshProgressPage();
    }

    private void UpdateWatchProgressText()
    {
        var identity = _page is null ? null : Services.Follow.Session.Current?.Identity;
        var episode = identity is null ? null : Progress.Get(identity.Bvid)?.FindPart(identity.Part);
        WatchProgressText.Text = episode?.Status switch
        {
            EpisodeStatus.Completed => "本集已完成",
            EpisodeStatus.InProgress => $"本集已看 {Percent(episode.Coverage)} · 续播点 {TimeText.Format(episode.Position)}",
            _ => ""
        };
    }

    /// <summary>The part to offer for a video: its first one not yet completed, when there is progress to go by.</summary>
    private int ResumePart(string bvid, int fallbackPart, out string? note)
    {
        var part = Progress.FirstUnfinishedPart(bvid, out var allCompleted);
        note = allCompleted ? "这张地图已全部完成，已回到 P1。" : part is { } next ? $"已定位到第一个未完成的 P{next}。" : null;
        return allCompleted ? 1 : part ?? fallbackPart;
    }

    private string ResumeUrl(string bvid, int fallbackPart, out string? note) => new VideoIdentity(bvid, ResumePart(bvid, fallbackPart, out note)).Url;

    private void ShowProgressPage()
    {
        _progressLoadFailed.Clear();
        CancelProgressConfirmation();
        // Open on what the user is working with: the video being followed, else the one selected on the start page.
        var current = (_page is null ? null : Services.Follow.Session.Current?.Identity) ?? _selectedIdentity;
        if (current is not null)
        {
            if (_progressBvid != current.Bvid) _progressCid = null;
            _progressBvid = current.Bvid;
            _progressCid = Progress.Get(current.Bvid)?.FindPart(current.Part)?.Cid ?? _progressCid;
        }
        RefreshProgressPage();
        if (ProgressVideoList.SelectedItem is { } video) ProgressVideoList.ScrollIntoView(video);
        if (ProgressEpisodeList.SelectedItem is { } part) ProgressEpisodeList.ScrollIntoView(part);
        // Queued so the awaits inside always resume on the dispatcher.
        _ = Dispatcher.BeginInvoke(() => _ = LoadMissingPartsAsync());
    }

    private void RefreshProgressPage()
    {
        _updatingProgress = true;
        try
        {
            RefreshProgressVideos();
            RefreshProgressEpisodes();
        }
        finally { _updatingProgress = false; }
        RefreshProgressEditor();
    }

    private void RefreshProgressVideos()
    {
        var library = _activeLibrary?.Videos ?? [];
        var inLibrary = library.Select(video => video.Bvid).ToHashSet(StringComparer.Ordinal);
        var playing = _page is null ? null : Services.Follow.Session.Current?.Identity.Bvid;
        var others = Progress.Videos.Where(video => !inLibrary.Contains(video.Bvid) && (video.HasProgress || video.Bvid == playing || video.Bvid == _progressBvid))
            .OrderByDescending(video => video.UpdatedAt ?? DateTimeOffset.MinValue).ToArray();
        var wanted = library.Select(video => (video.Bvid, Title: video.DisplayText, Group: ""))
            .Concat(others.Select(video => (video.Bvid, Title: string.IsNullOrWhiteSpace(video.Title) ? video.Bvid : video.Title, Group: library.Count > 0 ? OtherVideosGroup : ""))).ToArray();
        if (!wanted.Select(item => (item.Bvid, item.Title)).SequenceEqual(_progressVideos.Select(row => (row.Bvid, row.Title))))
        {
            _progressVideos = wanted.Select(item => new ProgressVideoRow(item.Bvid, item.Title, item.Group)).ToArray();
            var view = new ListCollectionView(_progressVideos);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProgressVideoRow.Group)));
            ProgressVideoList.ItemsSource = view;
        }
        int completed = 0, total = 0, unread = 0;
        foreach (var row in _progressVideos)
        {
            var video = Progress.Get(row.Bvid);
            row.CountText = video is { PartsKnown: true } ? $"{video.Completed}/{video.Episodes.Count}" :
                _progressLoadFailed.Contains(row.Bvid) ? "读取失败" : video is { HasProgress: true } ? "进行中" : "未读取";
            row.Ratio = video is { PartsKnown: true, Episodes.Count: > 0 } ? (double)video.Completed / video.Episodes.Count : 0;
            if (row.Group == OtherVideosGroup) continue;
            if (video is { PartsKnown: true }) { completed += video.Completed; total += video.Episodes.Count; }
            else unread++;
        }
        ProgressLibraryText.Text = _activeLibrary?.DisplayName ?? "全部视频";
        ProgressTotalText.Text = _progressVideos.Length == 0 ? "还没有任何观看记录" :
            $"已完成 {completed} / {total} 集" + (unread > 0 ? $" · {unread} 个待读取" : "");
        ProgressTotalBar.Value = total > 0 ? (double)completed / total : 0;
        if (_progressBvid is null || _progressVideos.All(row => row.Bvid != _progressBvid))
        {
            _progressBvid = _progressVideos.FirstOrDefault()?.Bvid;
            _progressCid = null;
        }
        ProgressVideoList.SelectedItem = _progressVideos.FirstOrDefault(row => row.Bvid == _progressBvid);
    }

    private void RefreshProgressEpisodes()
    {
        var row = _progressVideos.FirstOrDefault(item => item.Bvid == _progressBvid);
        var video = _progressBvid is null ? null : Progress.Get(_progressBvid);
        var episodes = video?.Episodes ?? [];
        ProgressVideoTitle.Text = row?.Title ?? "没有可显示的视频";
        ProgressVideoCountText.Text = video is { Episodes.Count: > 0 } ? $"{video.Completed}/{episodes.Count} 集" : "";
        if (!episodes.Select(episode => episode.Cid).SequenceEqual(_progressEpisodes.Select(item => item.Cid)))
        {
            _progressEpisodes = episodes.Select(episode => new ProgressEpisodeRow(episode.Cid)).ToArray();
            ProgressEpisodeList.ItemsSource = _progressEpisodes;
        }
        var playing = _page is not null && Services.Follow.Session.Current?.Identity is { } identity && identity.Bvid == _progressBvid ? identity.Part : 0;
        foreach (var (item, episode) in _progressEpisodes.Zip(episodes))
        {
            item.Title = (episode.Part == playing ? "▶ " : "") + $"P{episode.Part} · {episode.Title}";
            item.Duration = episode.Duration;
            item.Segments = episode.Segments;
            item.StateText = episode.Status == EpisodeStatus.Completed ? "完成" : episode.Segments.Count > 0 ? Percent(episode.Coverage) : "";
            item.ManualVisibility = episode.IsManual ? Visibility.Visible : Visibility.Collapsed;
            item.CheckVisibility = episode.Status == EpisodeStatus.Completed ? Visibility.Visible : Visibility.Collapsed;
            item.DotStroke = episode.Status switch
            {
                EpisodeStatus.Completed => Brushes.Transparent,
                EpisodeStatus.InProgress => (Brush)FindResource("PrimaryBrush"),
                _ => (Brush)FindResource("InputBorderBrush")
            };
            item.DotFill = episode.Status == EpisodeStatus.InProgress ? (Brush)FindResource("SelectedBrush") : Brushes.Transparent;
            item.Detail = $"{episode.Title}\n{Describe(episode)}";
        }
        if (_progressCid is null || _progressEpisodes.All(item => item.Cid != _progressCid))
            _progressCid = (episodes.FirstOrDefault(episode => episode.Part == playing) ?? episodes.FirstOrDefault(episode => episode.Status != EpisodeStatus.Completed) ?? episodes.FirstOrDefault())?.Cid;
        ProgressEpisodeList.SelectedItem = _progressEpisodes.FirstOrDefault(item => item.Cid == _progressCid);
        ProgressEmptyText.Visibility = episodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressEmptyText.Text = _progressBvid is null ? "开始跟随视频后，这里会显示每一集看到了哪里。" :
            _progressLoadFailed.Contains(_progressBvid) ? "分集读取失败，请检查网络后重新打开本页。" : "正在读取分集…";
        var selected = episodes.FirstOrDefault(episode => episode.Cid == _progressCid);
        ProgressCompleteBeforeButton.IsEnabled = selected is not null && episodes.Any(episode => episode.Part < selected.Part && episode.Status != EpisodeStatus.Completed);
        ProgressCompleteAllButton.IsEnabled = episodes.Any(episode => episode.Status != EpisodeStatus.Completed);
        ProgressResetVideoButton.IsEnabled = episodes.Any(episode => episode.HasActivity || episode.IsManual);
    }

    private string Describe(EpisodeProgress episode)
    {
        var state = episode.Mark switch
        {
            ProgressMark.Completed => "手动标记完成",
            ProgressMark.NotCompleted => "手动标记未完成",
            _ => episode.Status == EpisodeStatus.Completed ? "已完成" : episode.HasActivity ? "进行中" : "未开始"
        };
        var watched = episode.Segments.Count > 0 ? $" · 已看 {TimeText.Format(episode.Watched)}（{Percent(episode.Coverage)}）" : "";
        var resume = episode.Position > 0 ? $" · 续播点 {TimeText.Format(episode.Position)}" : "";
        return $"{state} · 时长 {TimeText.Format(episode.Duration)}{watched}{resume}";
    }

    private EpisodeProgress? SelectedProgressEpisode =>
        _progressBvid is { } bvid && _progressCid is { } cid ? Progress.Get(bvid, cid) : null;

    private void RefreshProgressEditor()
    {
        var episode = SelectedProgressEpisode;
        ProgressEditor.IsEnabled = episode is not null;
        ProgressEditorTitle.Text = episode is null ? "选择一个分集查看和修改进度" : $"P{episode.Part} · {episode.Title}";
        ProgressEditorState.Text = episode is null ? "" : Describe(episode);
        ProgressTimeline.Segments = episode?.Segments;
        ProgressTimeline.Duration = episode?.Duration ?? 0;
        ProgressTimeline.Position = episode?.Position ?? double.NaN;
        ProgressTimelineEnd.Text = episode is null ? "" : TimeText.Format(episode.Duration);
        // Never overwrite what the user is typing with the position of a playing video.
        if (!ProgressPositionInput.IsKeyboardFocusWithin) ProgressPositionInput.Text = episode is { Position: > 0 } ? TimeText.Format(episode.Position) : "";
        ProgressCompleteButton.IsEnabled = episode is not null && episode.Mark != ProgressMark.Completed;
        ProgressIncompleteButton.IsEnabled = episode is not null && episode.Mark != ProgressMark.NotCompleted;
        ProgressAutoButton.IsEnabled = episode is { IsManual: true };
        ProgressResetEpisodeButton.IsEnabled = episode is not null && (episode.HasActivity || episode.IsManual);
        ProgressUndoButton.IsEnabled = Progress.UndoDescription is not null;
        ProgressUndoButton.ToolTip = Progress.UndoDescription is { } description ? $"撤销：{description}" : "没有可撤销的手动修改";
    }

    private async Task LoadMissingPartsAsync()
    {
        if (_loadingProgressParts) return;
        _loadingProgressParts = true;
        try
        {
            var token = _progressLoading.Token;
            // The selected video first, so the list the user is looking at fills in before the rest.
            var pending = _progressVideos.Select(row => row.Bvid).OrderBy(bvid => bvid == _progressBvid ? 0 : 1).ToArray();
            var loaded = 0;
            foreach (var bvid in pending)
            {
                if (_closing || token.IsCancellationRequested) return;
                if (Progress.Get(bvid) is { PartsKnown: true }) continue;
                try { await Services.LoadEpisodesAsync(bvid, token); loaded++; }
                catch (OperationCanceledException) when (token.IsCancellationRequested || _closing) { return; }
                catch (Exception ex)
                {
                    // Any failure of one video (network, format, removed) must not stop the others.
                    AppLog.Warn("Progress", $"读取 {bvid} 的分集失败。", ex);
                    _progressLoadFailed.Add(bvid);
                    if (ProgressPageVisible) RefreshProgressPage();
                }
                try { await Task.Delay(120, token); } catch (OperationCanceledException) { return; }
            }
            if (loaded > 0)
            {
                AppLog.Info("Progress", $"已读取 {loaded} 个视频的分集。");
                Progress.Flush();
            }
        }
        finally { _loadingProgressParts = false; }
    }

    private void ProgressVideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingProgress || ProgressVideoList.SelectedItem is not ProgressVideoRow row || row.Bvid == _progressBvid) return;
        _progressBvid = row.Bvid;
        _progressCid = null;
        CancelProgressConfirmation();
        ClearProgressRange();
        RefreshProgressPage();
    }

    private void ProgressEpisodeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingProgress || ProgressEpisodeList.SelectedItem is not ProgressEpisodeRow row || row.Cid == _progressCid) return;
        _progressCid = row.Cid;
        CancelProgressConfirmation();
        ClearProgressRange();
        RefreshProgressPage();
    }

    private void ClearProgressRange()
    {
        _updatingProgress = true;
        try { ProgressRangeStart.Text = ProgressRangeEnd.Text = ""; }
        finally { _updatingProgress = false; }
        ProgressTimeline.ClearSelection();
    }

    private void ProgressTimeline_SelectionChanged(object? sender, EventArgs e)
    {
        _updatingProgress = true;
        try
        {
            ProgressRangeStart.Text = TimeText.Format(ProgressTimeline.SelectionStart);
            ProgressRangeEnd.Text = TimeText.Format(ProgressTimeline.SelectionEnd);
        }
        finally { _updatingProgress = false; }
    }

    private void OnProgressTimeClicked(double seconds)
    {
        ProgressPositionInput.Text = TimeText.Format(seconds);
        SetStatus($"已选中 {TimeText.Format(seconds)}，点击“设为续播点”保存；拖动可以选出一段。");
    }

    private void ProgressRange_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingProgress || ProgressTimeline is null) return;
        if (TimeText.TryParse(ProgressRangeStart.Text, out var start) && TimeText.TryParse(ProgressRangeEnd.Text, out var end) && end > start)
        { ProgressTimeline.SelectionStart = start; ProgressTimeline.SelectionEnd = end; }
        else ProgressTimeline.ClearSelection();
    }

    private bool TryReadProgressRange(EpisodeProgress episode, out double start, out double end)
    {
        end = 0;
        if (!TimeText.TryParse(ProgressRangeStart.Text, out start) || !TimeText.TryParse(ProgressRangeEnd.Text, out end))
        { SetStatus("请先在时间轴上拖出一段，或填写开始和结束时间，例如 3:20 和 8:45。", true); return false; }
        if (end <= start) { SetStatus("结束时间需要晚于开始时间。", true); return false; }
        if (episode.Duration > 0 && start >= episode.Duration) { SetStatus($"开始时间超出了本集时长 {TimeText.Format(episode.Duration)}。", true); return false; }
        if (episode.Duration > 0) end = Math.Min(end, episode.Duration);
        return true;
    }

    private void ProgressMarkWatched_Click(object sender, RoutedEventArgs e) => MarkProgressRange(watched: true);
    private void ProgressMarkUnwatched_Click(object sender, RoutedEventArgs e) => MarkProgressRange(watched: false);

    private void MarkProgressRange(bool watched)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode || !TryReadProgressRange(episode, out var start, out var end)) return;
        var changed = watched ? Progress.MarkWatched(bvid, episode.Cid, start, end) : Progress.MarkUnwatched(bvid, episode.Cid, start, end);
        var range = $"{TimeText.Format(start)}–{TimeText.Format(end)}";
        SetStatus(changed ? $"已把 P{episode.Part} 的 {range} 标记为{(watched ? "已看" : "未看")}。" : $"P{episode.Part} 的 {range} 本来就是{(watched ? "已看" : "未看")}。");
        if (changed) ClearProgressRange();
    }

    private void ProgressSetPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode) return;
        if (!TimeText.TryParse(ProgressPositionInput.Text, out var position))
        { SetStatus("请填写续播时间，例如 12:34，或在时间轴上点击一个位置。", true); return; }
        if (episode.Duration > 0 && position > episode.Duration)
        { SetStatus($"续播时间超出了本集时长 {TimeText.Format(episode.Duration)}。", true); return; }
        SetStatus(Progress.SetPosition(bvid, episode.Cid, position)
            ? $"P{episode.Part} 的续播点已设为 {TimeText.Format(position)}。" : $"P{episode.Part} 的续播点已经是 {TimeText.Format(position)}。");
    }

    private void ProgressPositionInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        ProgressSetPosition_Click(sender, e);
    }

    private void ProgressComplete_Click(object sender, RoutedEventArgs e) => ApplyProgressMark(ProgressMark.Completed, "已标记为完成");
    private void ProgressIncomplete_Click(object sender, RoutedEventArgs e) => ApplyProgressMark(ProgressMark.NotCompleted, "已固定为未完成，看完后也不会自动完成");
    private void ProgressAuto_Click(object sender, RoutedEventArgs e) => ApplyProgressMark(ProgressMark.Auto, "已恢复按观看时长自动判定");

    private void ApplyProgressMark(ProgressMark mark, string result)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode) return;
        if (Progress.SetMark(bvid, episode.Cid, mark)) SetStatus($"P{episode.Part} {result}。");
    }

    private void ProgressResetEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode) return;
        if (Progress.ResetEpisode(bvid, episode.Cid)) SetStatus($"P{episode.Part} 的记录已清空，可点“撤销”恢复。");
    }

    private void ProgressCompleteBefore_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode || Progress.Get(bvid) is not { } video) return;
        var count = video.Episodes.Count(item => item.Part < episode.Part && item.Status != EpisodeStatus.Completed);
        ConfirmProgressAction($"把 P{episode.Part} 之前的 {count} 集标记为已完成？", () => Progress.CompleteBefore(bvid, episode.Part), $"P{episode.Part} 之前的 {count} 集已标记完成");
    }

    private void ProgressCompleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || Progress.Get(bvid) is not { } video) return;
        var count = video.Episodes.Count(item => item.Status != EpisodeStatus.Completed);
        ConfirmProgressAction($"把这张地图剩下的 {count} 集全部标记为已完成？", () => Progress.CompleteAll(bvid), $"{count} 集已标记完成");
    }

    private void ProgressResetVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || Progress.Get(bvid) is not { } video) return;
        ConfirmProgressAction($"清空这张地图全部 {video.Episodes.Count} 集的观看记录？", () => Progress.ResetVideo(bvid), "整张地图的记录已清空");
    }

    // Bulk changes take a second, explicit click; one stray press must not rewrite a whole map.
    private void ConfirmProgressAction(string question, Func<bool> action, string result)
    {
        _pendingProgressAction = () =>
        {
            var changed = action();
            SetStatus(changed ? $"{result}，可点“撤销”恢复。" : "没有需要修改的分集。");
            return changed;
        };
        ProgressConfirmText.Text = question;
        ProgressBulkPanel.Visibility = Visibility.Collapsed;
        ProgressConfirmPanel.Visibility = Visibility.Visible;
    }

    private void CancelProgressConfirmation()
    {
        _pendingProgressAction = null;
        ProgressConfirmPanel.Visibility = Visibility.Collapsed;
        ProgressBulkPanel.Visibility = Visibility.Visible;
    }

    private void ProgressConfirm_Click(object sender, RoutedEventArgs e)
    {
        var action = _pendingProgressAction;
        CancelProgressConfirmation();
        action?.Invoke();
    }

    private void ProgressCancel_Click(object sender, RoutedEventArgs e) => CancelProgressConfirmation();

    private void ProgressUndo_Click(object sender, RoutedEventArgs e)
    {
        var description = Progress.UndoDescription;
        if (Progress.Undo()) SetStatus($"已撤销：{description}。");
    }

    private async void ProgressFollow_Click(object sender, RoutedEventArgs e)
    {
        if (_progressBvid is not { } bvid || SelectedProgressEpisode is not { } episode) return;
        var identity = new VideoIdentity(bvid, episode.Part);
        AppLog.Info("App", $"用户从进度页开始跟随 {bvid} P{episode.Part}。");
        NavFollow.IsChecked = true;
        if (_page is not null && Services.Follow.Session.Current?.Identity is { } current && current.Bvid == bvid && _info?.Bvid == bvid)
        {
            // Already following this video: change part in the same tab.
            if (current.Part != episode.Part) await NavigateActiveEpisodeAsync(episode.Part);
            return;
        }
        UrlInput.Text = identity.Url;
        await OpenVideoAsync();
    }
}
