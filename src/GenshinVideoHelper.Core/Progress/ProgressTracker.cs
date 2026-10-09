namespace GenshinVideoHelper.Core.Progress;

/// <summary>One observation of a validated video. <paramref name="At"/> is monotonic, not wall-clock.</summary>
public readonly record struct PlaybackSample(TimeSpan At, string Bvid, long Cid, double Position, bool Paused, double Rate, double Duration);

/// <summary>What a sample proved: a newly watched span, and whether the playhead is somewhere the viewer settled.</summary>
public readonly record struct TrackedProgress(WatchSegment? Watched, bool Settled);

/// <summary>Turns playhead samples into watched spans. Only time that advanced at playback speed between two
/// nearby samples counts, so seeking, scrubbing, resuming and sleep never mark anything as watched.</summary>
public sealed class ProgressTracker
{
    /// <summary>Media seconds a run must play before it counts; brief landings while scrubbing are discarded.</summary>
    public const double SettleSeconds = 3;
    /// <summary>Wall seconds between samples beyond which the gap is never bridged.</summary>
    public const double MaxSampleGap = 5;
    private const double Slack = 1, RateTolerance = 1.25, Backstep = 0.5, PausedDrift = 0.25;

    private Run? _run;

    private sealed class Run(PlaybackSample sample)
    {
        public string Bvid { get; } = sample.Bvid;
        public long Cid { get; } = sample.Cid;
        public double Start { get; } = sample.Position;
        public double End { get; set; } = sample.Position;
        public double Reported { get; set; } = sample.Position;
        public PlaybackSample Last { get; set; } = sample;
    }

    public void Reset() => _run = null;

    public TrackedProgress Observe(PlaybackSample sample)
    {
        if (!IsUsable(sample)) { _run = null; return default; }
        if (_run is not { } run || run.Bvid != sample.Bvid || run.Cid != sample.Cid || !Continues(run.Last, sample))
        {
            _run = new(sample);
            return default;
        }
        run.End = Math.Max(run.End, sample.Position);
        run.Last = sample;
        if (run.End - run.Start < SettleSeconds) return default;
        WatchSegment? watched = run.End > run.Reported ? new(run.Reported, run.End) : null;
        run.Reported = run.End;
        return new(watched, Settled: true);
    }

    private static bool Continues(PlaybackSample previous, PlaybackSample current)
    {
        var elapsed = (current.At - previous.At).TotalSeconds;
        var moved = current.Position - previous.Position;
        if (elapsed < 0 || elapsed > MaxSampleGap) return false;
        // Paused throughout: any movement is a seek, however small.
        if (previous.Paused && current.Paused) return Math.Abs(moved) <= PausedDrift;
        var rate = Math.Clamp(Math.Max(previous.Rate, current.Rate), 0.25, 4);
        return moved >= -Backstep && moved <= elapsed * rate * RateTolerance + Slack;
    }

    private static bool IsUsable(PlaybackSample sample) =>
        !string.IsNullOrEmpty(sample.Bvid) && double.IsFinite(sample.Duration) && sample.Duration > 0 &&
        double.IsFinite(sample.Position) && sample.Position >= 0 && sample.Position <= sample.Duration + 1 &&
        double.IsFinite(sample.Rate) && sample.Rate > 0;
}
