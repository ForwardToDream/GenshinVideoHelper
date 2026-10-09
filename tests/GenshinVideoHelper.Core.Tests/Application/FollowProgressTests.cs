using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Core.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Application;

public sealed class FollowProgressTests
{
    private static readonly VideoIdentity Identity = new("BV1hjgG6jEa6", 1);
    private static readonly long Cid = FakeEpisodes.Info(Identity).Episodes[0].Cid;
    private readonly ManualClock _clock = new();
    private readonly WatchProgressService _progress;
    private readonly List<VideoCommand> _commands = [];
    private readonly FakeVideo _video = new();
    private double _playhead;

    public FollowProgressTests()
    {
        _progress = new(clock: _clock);
        _progress.Describe(FakeEpisodes.Info(Identity));
        _video.Reply = (page, command, _) =>
        {
            _commands.Add(command);
            if (command is { Action: "seek", Absolute: true }) _playhead = command.Value;
            return Task.FromResult(new VideoState("fixture", page.Url, false, _playhead, 60, false, 1, command.Action == "ensurePip", 640, 360));
        };
    }

    private FollowCoordinator Create() => new(new FakeBrowser(), _video, new FakeEpisodes(), new FakePip(), new(), _clock, _progress);
    private IEnumerable<string> Actions => _commands.Select(command => command.Action);

    [Fact]
    public async Task Poll_SavedPosition_ResumesOnceBeforePlaying()
    {
        _progress.SetPosition(Identity.Bvid, Cid, 25);
        using var follow = Create();
        await follow.OpenAsync(Identity);
        await follow.PollAsync();
        Assert.Equal(["status", "seek", "ensurePlay", "ensurePip"], Actions);
        Assert.Equal(new VideoCommand("seek", 25, Absolute: true), _commands[1]);
        Assert.Contains("已从 00:25 继续", follow.Current.Message);
        follow.Retry();
        await follow.PollAsync();
        // Already at the resume point, so a new attempt does not jump again.
        Assert.Single(Actions, action => action == "seek");
    }

    [Fact]
    public async Task Poll_AutomaticSequenceInterrupted_DoesNotResumeTwice()
    {
        _progress.SetPosition(Identity.Bvid, Cid, 25);
        var inner = _video.Reply!;
        var failures = 1;
        _video.Reply = (page, command, token) => command.Action == "ensurePlay" && failures-- > 0
            ? Task.FromException<VideoState>(new VideoNotReadyException("buffering"))
            : inner(page, command, token);
        using var follow = Create();
        await follow.OpenAsync(Identity);
        await follow.PollAsync();
        _playhead = 3;
        await follow.PollAsync();
        Assert.Equal(FollowPhase.Following, follow.Current.Phase);
        Assert.Single(Actions, action => action == "seek");
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(25, true)]
    public async Task Poll_NothingWorthResuming_PlaysFromWhereThePlayerIs(double position, bool completed)
    {
        _progress.SetPosition(Identity.Bvid, Cid, position);
        if (completed) _progress.SetMark(Identity.Bvid, Cid, ProgressMark.Completed);
        using var follow = Create();
        await follow.OpenAsync(Identity);
        await follow.PollAsync();
        Assert.DoesNotContain("seek", Actions);
        Assert.DoesNotContain("继续", follow.Current.Message);
    }

    [Fact]
    public async Task Poll_ValidatedPlayback_IsRecordedAndMismatchedStatesAreNot()
    {
        using var follow = Create();
        await follow.OpenAsync(Identity);
        for (var second = 0; second < 10; second++)
        {
            await follow.PollAsync();
            _clock.Advance(1);
            _playhead += 1;
        }
        var watched = _progress.Get(Identity.Bvid, Cid)!.Watched;
        Assert.InRange(watched, 8, 10);

        // The page reports another part: nothing may be attributed to either episode.
        var inner = _video.Reply!;
        _video.Reply = async (page, command, token) => await inner(page, command, token) with { Url = new VideoIdentity(Identity.Bvid, 2).Url };
        for (var second = 0; second < 10; second++)
        {
            await follow.PollAsync();
            _clock.Advance(1);
            _playhead += 1;
        }
        Assert.Equal(watched, _progress.Get(Identity.Bvid, Cid)!.Watched);
        Assert.False(_progress.Get(Identity.Bvid)!.FindPart(2)!.HasActivity);
    }

    [Fact]
    public async Task Poll_SeekCommand_LeavesTheSkippedPartUnwatched()
    {
        using var follow = Create();
        await follow.OpenAsync(Identity);
        for (var second = 0; second < 6; second++) { await follow.PollAsync(); _clock.Advance(1); _playhead += 1; }
        _playhead += 20;
        await follow.ExecuteAsync(new("status"));
        for (var second = 0; second < 6; second++) { await follow.PollAsync(); _clock.Advance(1); _playhead += 1; }
        var segments = _progress.Get(Identity.Bvid, Cid)!.Segments;
        Assert.Equal(2, segments.Count);
        Assert.True(segments[1].Start - segments[0].End >= 19);
    }
}
