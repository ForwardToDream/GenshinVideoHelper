namespace GenshinVideoHelper.Core.Progress;

/// <summary>A span of media time, in seconds, that was actually played.</summary>
public readonly record struct WatchSegment(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>Sorted, non-overlapping watched spans. Every operation returns a list and never mutates its input.</summary>
public static class WatchSegments
{
    public const double MergeGap = 2;
    public const int MaxCount = 200;
    private const double Shortest = 0.1;

    /// <summary>Adds a span, absorbing neighbours within <paramref name="mergeGap"/> of it. Gaps between other spans are left alone.</summary>
    public static IReadOnlyList<WatchSegment> Add(IReadOnlyList<WatchSegment> segments, double start, double end, double duration, double mergeGap = MergeGap)
    {
        if (!TryClamp(ref start, ref end, duration)) return segments;
        var result = new List<WatchSegment>(segments.Count + 1);
        var inserted = false;
        foreach (var segment in segments)
        {
            if (segment.End < start - mergeGap) result.Add(segment);
            else if (segment.Start > end + mergeGap)
            {
                if (!inserted) { result.Add(new(start, end)); inserted = true; }
                result.Add(segment);
            }
            else { start = Math.Min(start, segment.Start); end = Math.Max(end, segment.End); }
        }
        if (!inserted) result.Add(new(start, end));
        return Limit(result);
    }

    public static IReadOnlyList<WatchSegment> Remove(IReadOnlyList<WatchSegment> segments, double start, double end)
    {
        if (!TryClamp(ref start, ref end, duration: 0)) return segments;
        var result = new List<WatchSegment>(segments.Count + 1);
        foreach (var segment in segments)
        {
            if (segment.End <= start || segment.Start >= end) { result.Add(segment); continue; }
            if (start - segment.Start >= Shortest) result.Add(new(segment.Start, start));
            if (segment.End - end >= Shortest) result.Add(new(end, segment.End));
        }
        return result;
    }

    public static double Covered(IReadOnlyList<WatchSegment> segments)
    {
        var total = 0d;
        foreach (var segment in segments) total += segment.Length;
        return total;
    }

    public static double Coverage(IReadOnlyList<WatchSegment> segments, double duration) =>
        duration > 0 ? Math.Clamp(Covered(segments) / duration, 0, 1) : 0;

    /// <summary>Repairs spans from an untrusted source: drops invalid ones, clamps, sorts and joins overlaps.</summary>
    public static IReadOnlyList<WatchSegment> Normalize(IEnumerable<WatchSegment>? segments, double duration)
    {
        var result = new List<WatchSegment>();
        foreach (var raw in (segments ?? []).OrderBy(segment => Math.Min(segment.Start, segment.End)))
        {
            double start = raw.Start, end = raw.End;
            if (!TryClamp(ref start, ref end, duration)) continue;
            if (result.Count > 0 && start <= result[^1].End) result[^1] = new(result[^1].Start, Math.Max(result[^1].End, end));
            else result.Add(new(start, end));
        }
        return Limit(result);
    }

    private static bool TryClamp(ref double start, ref double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) return false;
        if (start > end) (start, end) = (end, start);
        start = Math.Round(Math.Max(0, start), 1);
        end = Math.Round(duration > 0 ? Math.Min(end, duration) : end, 1);
        return end - start >= Shortest;
    }

    // A pathological history costs precision at its narrowest gaps instead of growing without bound.
    private static List<WatchSegment> Limit(List<WatchSegment> segments)
    {
        while (segments.Count > MaxCount)
        {
            var narrowest = 1;
            for (var index = 2; index < segments.Count; index++)
                if (segments[index].Start - segments[index - 1].End < segments[narrowest].Start - segments[narrowest - 1].End) narrowest = index;
            segments[narrowest - 1] = new(segments[narrowest - 1].Start, segments[narrowest].End);
            segments.RemoveAt(narrowest);
        }
        return segments;
    }
}
