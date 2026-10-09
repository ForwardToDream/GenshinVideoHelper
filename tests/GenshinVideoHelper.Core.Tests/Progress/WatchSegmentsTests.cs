using GenshinVideoHelper.Core.Progress;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Progress;

public sealed class WatchSegmentsTests
{
    private static IReadOnlyList<WatchSegment> Of(params (double Start, double End)[] spans) =>
        spans.Select(span => new WatchSegment(span.Start, span.End)).ToArray();

    [Fact]
    public void Add_OverlappingAndNearbySpans_MergesIntoOne()
    {
        var segments = WatchSegments.Add(Of((0, 10), (30, 40)), 9, 29, duration: 100);
        Assert.Equal(Of((0, 40)), segments);
    }

    [Fact]
    public void Add_FarFromOthers_StaysSeparateAndSorted()
    {
        var segments = WatchSegments.Add(Of((0, 10), (80, 90)), 40, 50, duration: 100);
        Assert.Equal(Of((0, 10), (40, 50), (80, 90)), segments);
    }

    [Fact]
    public void Add_DoesNotCloseExistingGapsItDoesNotTouch()
    {
        // The one-second hole was removed on purpose; adding elsewhere must not fill it back in.
        var segments = WatchSegments.Add(Of((0, 10), (11, 20)), 19, 30, duration: 100);
        Assert.Equal(Of((0, 10), (11, 30)), segments);
    }

    [Fact]
    public void Add_WithoutMergeGap_KeepsAdjacentSpanApart()
    {
        var segments = WatchSegments.Add(Of((0, 10)), 11, 20, duration: 100, mergeGap: 0);
        Assert.Equal(Of((0, 10), (11, 20)), segments);
    }

    [Theory]
    [InlineData(-5, 5, 0, 5)]
    [InlineData(95, 500, 95, 100)]
    [InlineData(20, 10, 10, 20)]
    public void Add_OutOfRangeOrReversed_IsClampedToDuration(double start, double end, double expectedStart, double expectedEnd) =>
        Assert.Equal(Of((expectedStart, expectedEnd)), WatchSegments.Add([], start, end, duration: 100));

    [Theory]
    [InlineData(double.NaN, 5)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(5, 5.04)]
    public void Add_InvalidOrNegligibleSpan_ChangesNothing(double start, double end)
    {
        var original = Of((0, 10));
        Assert.Same(original, WatchSegments.Add(original, start, end, duration: 100));
    }

    [Fact]
    public void Remove_MiddleOfSpan_SplitsIt()
    {
        var segments = WatchSegments.Remove(Of((0, 100)), 40, 60);
        Assert.Equal(Of((0, 40), (60, 100)), segments);
    }

    [Fact]
    public void Remove_AcrossSeveralSpans_TrimsAndDrops()
    {
        var segments = WatchSegments.Remove(Of((0, 10), (20, 30), (40, 50)), 5, 45);
        Assert.Equal(Of((0, 5), (45, 50)), segments);
    }

    [Fact]
    public void Coverage_IsUnionLengthOverDuration()
    {
        Assert.Equal(0.3, WatchSegments.Coverage(Of((0, 10), (50, 70)), 100), 6);
        Assert.Equal(0, WatchSegments.Coverage(Of((0, 10)), 0));
    }

    [Fact]
    public void Normalize_UntrustedSpans_DropsInvalidSortsAndJoinsOverlaps()
    {
        var raw = new[] { new WatchSegment(50, 60), new(double.NaN, 3), new(0, 10), new(5, 20), new(90, 400), new(70, 70) };
        Assert.Equal(Of((0, 20), (50, 60), (90, 100)), WatchSegments.Normalize(raw, duration: 100));
        Assert.Empty(WatchSegments.Normalize(null, 100));
    }

    [Fact]
    public void Add_BeyondMaximumCount_MergesNarrowestGapsInsteadOfGrowing()
    {
        IReadOnlyList<WatchSegment> segments = [];
        for (var index = 0; index < WatchSegments.MaxCount + 50; index++)
            segments = WatchSegments.Add(segments, index * 10, index * 10 + 3, duration: 100000);
        Assert.Equal(WatchSegments.MaxCount, segments.Count);
        Assert.True(segments.Zip(segments.Skip(1)).All(pair => pair.First.End < pair.Second.Start));
    }
}
