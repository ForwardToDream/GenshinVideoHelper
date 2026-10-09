using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Progress;

/// <summary>Owns every video's watch progress: applies tracked playback and manual edits, derives completion,
/// keeps an undo history for manual edits and decides when to persist. Called by one UI owner.</summary>
public sealed class WatchProgressService
{
    public const double SaveIntervalSeconds = 15;
    /// <summary>A resume point this close to the start or the end is not worth a jump.</summary>
    public const double ResumeMinimum = 15, ResumeTailMargin = 30;
    private const int UndoLimit = 20, RetiredLimit = 50;

    private readonly IProgressStore? _store;
    private readonly TimeProvider _clock;
    private readonly ProgressTracker _tracker = new();
    private readonly Dictionary<string, VideoProgress> _videos;
    private readonly List<UndoEntry> _undo = [];
    private (string Bvid, long Cid)? _active;
    private bool _activePaused, _dirty;
    private long _lastSave;
    private string? _saveError;

    private sealed record UndoEntry(string Description, string Bvid, IReadOnlyList<EpisodeProgress> Before);

    public double CompletionRatio { get; }
    public string? LoadWarning { get; }
    /// <summary>Raised with the BV id whose progress changed.</summary>
    public event Action<string>? Changed;
    /// <summary>Raised once per distinct failure; progress stays in memory and saving is retried.</summary>
    public event Action<string>? SaveFailed;

    public WatchProgressService(IProgressStore? store = null, double completionRatio = 0.9, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        CompletionRatio = double.IsFinite(completionRatio) ? Math.Clamp(completionRatio, 0.5, 1) : 0.9;
        var loaded = store?.Load();
        LoadWarning = loaded?.Warning;
        _videos = new(loaded?.Document.Normalize().Videos ?? new Dictionary<string, VideoProgress>(), StringComparer.Ordinal);
        if (store is not null) AppLog.Info("Progress", $"进度已加载：{_videos.Count} 个视频，{_videos.Values.Sum(video => video.Completed)} 集已完成，完成线 {CompletionRatio:P0}。");
        _lastSave = _clock.GetTimestamp();
    }

    public IReadOnlyCollection<VideoProgress> Videos => _videos.Values;
    public VideoProgress? Get(string bvid) => _videos.GetValueOrDefault(bvid);
    public EpisodeProgress? Get(string bvid, long cid) => Get(bvid)?.Find(cid);
    public ProgressDocument Document => new() { Videos = new Dictionary<string, VideoProgress>(_videos, StringComparer.Ordinal) };

    /// <summary>Records the video's current parts so progress can be shown without loading the video.
    /// A part whose cid changed keeps its progress when the number and duration still match.</summary>
    public void Describe(BilibiliVideoInfo info)
    {
        var existing = Get(info.Bvid);
        var pool = (existing?.Episodes ?? []).Concat(existing?.Retired ?? []).ToList();
        var episodes = new List<EpisodeProgress>(info.Episodes.Count);
        foreach (var part in info.Episodes)
        {
            var match = pool.FirstOrDefault(episode => episode.Cid == part.Cid) ??
                        pool.FirstOrDefault(episode => episode.Part == part.Number && Math.Abs(episode.Duration - part.Duration) <= 2 &&
                                                       info.Episodes.All(other => other.Cid != episode.Cid));
            if (match is null) { episodes.Add(new(part.Cid, part.Number, part.Title, part.Duration)); continue; }
            pool.Remove(match);
            var resized = part.Duration > 0 && Math.Abs(match.Duration - part.Duration) > 0.05;
            episodes.Add(match with
            {
                Cid = part.Cid, Part = part.Number, Title = part.Title, Duration = part.Duration,
                Segments = resized ? WatchSegments.Normalize(match.Segments, part.Duration) : match.Segments,
                Position = resized ? Math.Min(match.Position, part.Duration) : match.Position
            });
        }
        var retired = pool.Where(episode => episode.HasActivity || episode.IsManual).TakeLast(RetiredLimit).ToArray();
        if (existing is { PartsKnown: true } && existing.Title == info.Title && SameParts(existing.Episodes, episodes) && existing.Retired.Count == retired.Length) return;
        if (existing is not null && existing.Episodes.Count > 0 && retired.Length > existing.Retired.Count)
            AppLog.Info("Progress", $"{info.Bvid} 的分集有替换，{retired.Length - existing.Retired.Count} 条旧进度已归档。");
        _videos[info.Bvid] = (existing ?? new(info.Bvid, info.Title)) with { Title = info.Title, Episodes = episodes, Retired = retired, PartsKnown = true };
        _dirty = true;
        Changed?.Invoke(info.Bvid);
    }

    private static bool SameParts(IReadOnlyList<EpisodeProgress> left, IReadOnlyList<EpisodeProgress> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Cid == pair.Second.Cid && pair.First.Part == pair.Second.Part &&
            pair.First.Title == pair.Second.Title && pair.First.Duration == pair.Second.Duration);

    /// <summary>Feeds one validated playback state. <paramref name="at"/> is monotonic time.</summary>
    public void Observe(VideoIdentity identity, long? cid, VideoState state, TimeSpan at)
    {
        if (cid is not { } id || state.Duration is not { } mediaDuration)
        {
            _tracker.Reset();
            return;
        }
        var tracked = _tracker.Observe(new(at, identity.Bvid, id, state.CurrentTime, state.Paused, state.PlaybackRate, mediaDuration));
        var key = (identity.Bvid, id);
        // Leaving an episode or pausing is when the viewer is most likely to quit, so persist then.
        var boundary = _active is { } active && (active != key || state.Paused && !_activePaused);
        _active = key;
        _activePaused = state.Paused;
        var completed = false;
        if (tracked.Watched is not null || tracked.Settled)
        {
            var episode = Ensure(identity, id, state.Title, mediaDuration);
            var duration = episode.Duration > 0 ? episode.Duration : Math.Round(mediaDuration, 1);
            var updated = episode with { Duration = duration };
            if (tracked.Watched is { } span) updated = updated with { Segments = WatchSegments.Add(updated.Segments, span.Start, span.End, duration) };
            if (tracked.Settled) updated = updated with { Position = Math.Round(Math.Min(state.CurrentTime, duration), 1) };
            if (!ReferenceEquals(updated.Segments, episode.Segments) || updated.Position != episode.Position)
            {
                var now = _clock.GetLocalNow();
                completed = updated.Mark == ProgressMark.Auto && updated.CompletedAt is null && updated.Coverage >= CompletionRatio;
                if (completed)
                {
                    updated = updated with { CompletedAt = now };
                    AppLog.Info("Progress", $"{identity.Bvid} P{updated.Part} 已看 {updated.Coverage:P0}，自动标记完成。");
                }
                Replace(identity.Bvid, [updated with { UpdatedAt = now }], now);
                Changed?.Invoke(identity.Bvid);
            }
        }
        if (completed || boundary) Save();
        else SaveIfDue();
    }

    /// <summary>Where following this part should continue, or null to start wherever the player is.</summary>
    public double? ResumePosition(VideoIdentity identity)
    {
        if (Get(identity.Bvid)?.FindPart(identity.Part) is not { } episode || episode.Status == EpisodeStatus.Completed) return null;
        if (episode.Position < ResumeMinimum || episode.Duration > 0 && episode.Position > episode.Duration - ResumeTailMargin) return null;
        return episode.Position;
    }

    /// <summary>The first part not yet completed. Null when the video has no progress to go by, or when
    /// <paramref name="allCompleted"/> says there is nothing left.</summary>
    public int? FirstUnfinishedPart(string bvid, out bool allCompleted)
    {
        allCompleted = false;
        if (Get(bvid) is not { HasProgress: true } video) return null;
        var next = video.Episodes.FirstOrDefault(episode => episode.Status != EpisodeStatus.Completed);
        allCompleted = next is null;
        return next?.Part;
    }

    public bool MarkWatched(string bvid, long cid, double start, double end) =>
        Edit("标记已看", bvid, [cid], episode => Judge(episode with { Segments = WatchSegments.Add(episode.Segments, start, end, episode.Duration, mergeGap: 0) }));

    public bool MarkUnwatched(string bvid, long cid, double start, double end) =>
        Edit("标记未看", bvid, [cid], episode => Judge(episode with { Segments = WatchSegments.Remove(episode.Segments, start, end) }));

    public bool SetPosition(string bvid, long cid, double position) =>
        Edit("修改续播位置", bvid, [cid], episode => episode with
        { Position = Math.Round(Math.Clamp(position, 0, episode.Duration > 0 ? episode.Duration : double.MaxValue), 1) });

    public bool SetMark(string bvid, long cid, ProgressMark mark) =>
        Edit(mark switch { ProgressMark.Completed => "标记完成", ProgressMark.NotCompleted => "标记未完成", _ => "恢复自动判定" },
            bvid, [cid], episode => mark == ProgressMark.Auto ? Judge(episode with { Mark = mark }) : episode with { Mark = mark });

    public bool ResetEpisode(string bvid, long cid) => Edit("重置本集", bvid, [cid], Cleared);

    /// <summary>Marks every part before <paramref name="part"/> as completed, for progress made before it was tracked.</summary>
    public bool CompleteBefore(string bvid, int part) =>
        Edit($"P{part} 之前全部标记完成", bvid, Parts(bvid, episode => episode.Part < part && episode.Status != EpisodeStatus.Completed),
            episode => episode with { Mark = ProgressMark.Completed });

    public bool CompleteAll(string bvid) =>
        Edit("整张地图标记完成", bvid, Parts(bvid, episode => episode.Status != EpisodeStatus.Completed), episode => episode with { Mark = ProgressMark.Completed });

    public bool ResetVideo(string bvid) => Edit("重置整张地图", bvid, Parts(bvid, _ => true), Cleared);

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Replace(entry.Bvid, entry.Before, _clock.GetLocalNow());
        AppLog.Info("Progress", $"撤销：{entry.Bvid} {entry.Description}。");
        Save();
        Changed?.Invoke(entry.Bvid);
        return true;
    }

    public void SaveIfDue()
    {
        if (_dirty && _clock.GetElapsedTime(_lastSave).TotalSeconds >= SaveIntervalSeconds) Save();
    }

    public void Flush() => Save();

    private IReadOnlyList<long> Parts(string bvid, Func<EpisodeProgress, bool> filter) =>
        Get(bvid)?.Episodes.Where(filter).Select(episode => episode.Cid).ToArray() ?? [];

    private static EpisodeProgress Cleared(EpisodeProgress episode) =>
        episode with { Segments = [], Position = 0, Mark = ProgressMark.Auto, CompletedAt = null };

    // After a manual change to the spans the latch follows the coverage in both directions.
    private EpisodeProgress Judge(EpisodeProgress episode) =>
        episode with { CompletedAt = episode.Coverage >= CompletionRatio ? episode.CompletedAt ?? _clock.GetLocalNow() : null };

    private bool Edit(string description, string bvid, IReadOnlyList<long> cids, Func<EpisodeProgress, EpisodeProgress> change)
    {
        if (Get(bvid) is not { } video) return false;
        var before = new List<EpisodeProgress>();
        var after = new List<EpisodeProgress>();
        var now = _clock.GetLocalNow();
        foreach (var episode in video.Episodes.Where(episode => cids.Contains(episode.Cid)))
        {
            var changed = change(episode);
            // Records compare the span list by reference, so compare its contents separately.
            if (changed.Segments.SequenceEqual(episode.Segments) && changed with { Segments = episode.Segments } == episode) continue;
            before.Add(episode);
            after.Add(changed with { UpdatedAt = now });
        }
        if (after.Count == 0) return false;
        _undo.Add(new(description, bvid, before));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        Replace(bvid, after, now);
        AppLog.Info("Progress", $"手动修改：{bvid} {description}，{after.Count} 集（{string.Join("、", after.Take(8).Select(episode => "P" + episode.Part))}{(after.Count > 8 ? "…" : "")}）。");
        Save();
        Changed?.Invoke(bvid);
        return true;
    }

    private EpisodeProgress Ensure(VideoIdentity identity, long cid, string title, double duration)
    {
        if (Get(identity.Bvid)?.Find(cid) is { } known) return known;
        // The part list has not been read yet; it replaces this provisional entry when it arrives.
        var episode = new EpisodeProgress(cid, identity.Part, title, Math.Round(duration, 1));
        var video = Get(identity.Bvid) ?? new VideoProgress(identity.Bvid, title);
        _videos[identity.Bvid] = video with { Episodes = video.Episodes.Append(episode).OrderBy(item => item.Part).ToArray() };
        return episode;
    }

    private void Replace(string bvid, IReadOnlyList<EpisodeProgress> episodes, DateTimeOffset now)
    {
        if (Get(bvid) is not { } video) return;
        _videos[bvid] = video with
        {
            UpdatedAt = now,
            Episodes = video.Episodes.Select(current => episodes.FirstOrDefault(episode => episode.Cid == current.Cid) ?? current).ToArray()
        };
        _dirty = true;
    }

    private void Save()
    {
        _lastSave = _clock.GetTimestamp();
        if (!_dirty || _store is null) { _dirty = false; return; }
        try
        {
            _store.Save(Document);
            _dirty = false;
            _saveError = null;
            AppLog.Debug("Progress", $"进度已保存，{_videos.Count} 个视频。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Progress", "进度未能保存，稍后重试。", ex);
            if (_saveError == ex.Message) return;
            _saveError = ex.Message;
            SaveFailed?.Invoke(ex.Message);
        }
    }
}
