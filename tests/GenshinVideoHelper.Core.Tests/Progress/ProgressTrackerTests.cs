using GenshinVideoHelper.Core.Progress;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Progress;

public sealed class ProgressTrackerTests
{
    private const double Duration = 600;
    private readonly ProgressTracker _tracker = new();
    private IReadOnlyList<WatchSegment> _watched = [];
    private double _time, _position;
    private bool _settled;

    private void Sample(bool paused = false, double rate = 1, long cid = 1)
    {
        var result = _tracker.Observe(new(TimeSpan.FromSeconds(_time), "BV1hjgG6jEa6", cid, _position, paused, rate, Duration));
        _settled = result.Settled;
        if (result.Watched is { } span) _watched = WatchSegments.Add(_watched, span.Start, span.End, Duration);
    }

    private void Play(double seconds, double rate = 1, long cid = 1)
    {
        Sample(rate: rate, cid: cid);
        for (var second = 0; second < seconds; second++) { _time += 1; _position += rate; Sample(rate: rate, cid: cid); }
    }

    private void Seek(double position, bool paused = false) { _time += 0.2; _position = position; Sample(paused); }
    private double Covered => WatchSegments.Covered(_watched);

    [Fact]
    public void Playback_Continuous_CountsEverythingPlayed()
    {
        Play(60);
        Assert.Equal([new WatchSegment(0, 60)], _watched);
    }

    [Fact]
    public void Seek_Forward_DoesNotCountTheSkippedPart()
    {
        Play(10);
        Seek(300);
        Play(10);
        Assert.Equal([new WatchSegment(0, 10), new WatchSegment(300, 310)], _watched);
    }

    [Fact]
    public void Seek_ToTheEnd_LeavesAlmostNothingWatched()
    {
        Play(5);
        Seek(Duration - 2);
        Play(2);
        Assert.Equal(5, Covered, 1);
    }

    [Fact]
    public void Scrubbing_BriefLandings_AreDiscarded()
    {
        for (var landing = 1; landing <= 20; landing++)
        {
            Seek(landing * 25);
            Play(2);
        }
        Assert.Empty(_watched);
        Assert.False(_settled);
    }

    [Fact]
    public void Pause_AddsNothingAndDoesNotBreakTheRun()
    {
        Play(10);
        for (var second = 0; second < 120; second++) { _time += 1; Sample(paused: true); }
        Play(10);
        Assert.Equal([new WatchSegment(0, 20)], _watched);
    }

    [Fact]
    public void Seek_WhilePaused_IsAJumpHoweverSmall()
    {
        Play(10);
        _time += 1; Sample(paused: true);
        for (var step = 1; step <= 30; step++) { _time += 1; _position += 0.8; Sample(paused: true); }
        Assert.Equal([new WatchSegment(0, 10)], _watched);
    }

    [Fact]
    public void PlaybackRate_Double_StillCountsAsContinuous()
    {
        Play(30, rate: 2);
        Assert.Equal([new WatchSegment(0, 60)], _watched);
    }

    [Fact]
    public void SampleGap_LongerThanLimit_IsNotBridged()
    {
        Play(10);
        // The position is consistent with having played through a suspend, but nothing proves it.
        _time += 30; _position += 30; Sample();
        Play(10);
        Assert.Equal([new WatchSegment(0, 10), new WatchSegment(40, 50)], _watched);
    }

    [Fact]
    public void Rewatching_DoesNotCountTwice()
    {
        Play(20);
        Seek(5);
        Play(15);
        Assert.Equal([new WatchSegment(0, 20)], _watched);
    }

    [Fact]
    public void EpisodeChange_StartsANewRun()
    {
        Play(10, cid: 1);
        // Same position in another episode must not continue the previous episode's run.
        _time += 1; _position += 1;
        var result = _tracker.Observe(new(TimeSpan.FromSeconds(_time), "BV1hjgG6jEa6", 2, _position, false, 1, Duration));
        Assert.Null(result.Watched);
        Assert.False(result.Settled);
    }

    [Fact]
    public void Settled_OnlyAfterStablePlayback()
    {
        Play(2);
        Assert.False(_settled);
        Play(2);
        Assert.True(_settled);
        Seek(400);
        Assert.False(_settled);
    }

    [Theory]
    [InlineData(7.2, true)]
    [InlineData(8.0, false)]
    public void SingleSample_CannotAddMoreThanPlaybackSpeedAllows(double moved, bool counted)
    {
        Play(10);
        _time += ProgressTracker.MaxSampleGap; _position += moved; Sample();
        Assert.Equal(counted ? 10 + moved : 10, Covered, 1);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(600, double.NaN)]
    [InlineData(600, 900)]
    public void UnusableSample_ResetsInsteadOfRecording(double duration, double position)
    {
        Play(10);
        var result = _tracker.Observe(new(TimeSpan.FromSeconds(_time + 1), "BV1hjgG6jEa6", 1, position, false, 1, duration));
        Assert.Null(result.Watched);
        _time += 2; _position += 2; Sample();
        // The run restarted, so the two seconds around the bad sample are not bridged.
        Assert.Equal([new WatchSegment(0, 10)], _watched);
    }
}
