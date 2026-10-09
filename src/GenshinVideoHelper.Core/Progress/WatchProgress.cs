using System.Text.Json.Serialization;

namespace GenshinVideoHelper.Core.Progress;

/// <summary>Who decides completion: the watched coverage, or an explicit choice by the user that tracking never overrides.</summary>
public enum ProgressMark { Auto, Completed, NotCompleted }
public enum EpisodeStatus { NotStarted, InProgress, Completed }

public sealed record EpisodeProgress(long Cid, int Part, string Title, double Duration)
{
    public IReadOnlyList<WatchSegment> Segments { get; init; } = [];
    /// <summary>Where the viewer last settled; the resume point.</summary>
    public double Position { get; init; }
    public ProgressMark Mark { get; init; }
    /// <summary>Set when coverage first reached the completion ratio; tracking never clears it.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonIgnore] public double Watched => WatchSegments.Covered(Segments);
    [JsonIgnore] public double Coverage => WatchSegments.Coverage(Segments, Duration);
    [JsonIgnore] public bool HasActivity => Segments.Count > 0 || Position > 0;
    [JsonIgnore] public bool IsManual => Mark != ProgressMark.Auto;
    [JsonIgnore] public EpisodeStatus Status => Mark switch
    {
        ProgressMark.Completed => EpisodeStatus.Completed,
        ProgressMark.Auto when CompletedAt is not null => EpisodeStatus.Completed,
        _ => HasActivity ? EpisodeStatus.InProgress : EpisodeStatus.NotStarted
    };
}

public sealed record VideoProgress(string Bvid, string Title)
{
    /// <summary>The video's current parts in order, each with its progress.</summary>
    public IReadOnlyList<EpisodeProgress> Episodes { get; init; } = [];
    /// <summary>Progress of parts the uploader has since replaced; kept, never shown.</summary>
    public IReadOnlyList<EpisodeProgress> Retired { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>False while <see cref="Episodes"/> only holds parts seen during playback, before the full list was read.</summary>
    public bool PartsKnown { get; init; }

    [JsonIgnore] public int Completed => Episodes.Count(episode => episode.Status == EpisodeStatus.Completed);
    [JsonIgnore] public bool HasProgress => Episodes.Any(episode => episode.HasActivity || episode.IsManual);
    public EpisodeProgress? Find(long cid) => Episodes.FirstOrDefault(episode => episode.Cid == cid);
    public EpisodeProgress? FindPart(int part) => Episodes.FirstOrDefault(episode => episode.Part == part);
}

public sealed record ProgressDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyDictionary<string, VideoProgress> Videos { get; init; } = new Dictionary<string, VideoProgress>();

    /// <summary>Repairs a document from an untrusted file so every later rule can rely on its shape.</summary>
    public ProgressDocument Normalize()
    {
        var videos = new Dictionary<string, VideoProgress>(StringComparer.Ordinal);
        foreach (var (key, video) in Videos ?? new Dictionary<string, VideoProgress>())
        {
            if (video is null || string.IsNullOrWhiteSpace(key)) continue;
            videos[key] = video with
            {
                Bvid = key, Title = video.Title ?? "",
                Episodes = Repair(video.Episodes).OrderBy(episode => episode.Part).ToArray(),
                Retired = Repair(video.Retired).ToArray()
            };
        }
        return new() { Videos = videos };
    }

    private static IEnumerable<EpisodeProgress> Repair(IReadOnlyList<EpisodeProgress>? episodes) =>
        (episodes ?? []).Where(episode => episode is not null && episode.Part > 0).DistinctBy(episode => episode.Cid).Select(episode =>
        {
            var duration = double.IsFinite(episode.Duration) && episode.Duration > 0 ? episode.Duration : 0;
            var position = double.IsFinite(episode.Position) ? Math.Max(0, episode.Position) : 0;
            return episode with
            {
                Title = episode.Title ?? "", Duration = duration,
                Segments = WatchSegments.Normalize(episode.Segments, duration),
                Position = duration > 0 ? Math.Min(position, duration) : position,
                Mark = Enum.IsDefined(episode.Mark) ? episode.Mark : ProgressMark.Auto
            };
        });
}
