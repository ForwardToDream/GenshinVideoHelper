using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Application;

public sealed class FollowCoordinatorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Poll_LateFailureOrCancellation_DoesNotSuppressNewIntent(bool cancellation)
    {
        var delayed = new TaskCompletionSource<VideoState>();
        var video = new FakeVideo { Reply = (_, _, _) => delayed.Task };
        using var follow = new FollowCoordinator(new FakeBrowser(), video, new FakeEpisodes(), new FakePip(), new());
        await follow.OpenAsync(new("BV1hjgG6jEa6", 3));
        var polling = follow.PollAsync();
        await follow.NavigateAsync(4);
        var expected = follow.Current;
        if (cancellation) delayed.SetCanceled(TestContext.Current.CancellationToken);
        else delayed.SetException(new InvalidOperationException("old polling failed"));
        await polling;
        Assert.Equal(expected, follow.Current);
        Assert.True(follow.Current.AutomaticPending);
        Assert.Equal(4, follow.Current.Request!.Identity.Part);
    }

    [Fact]
    public async Task Execute_PollIsWaiting_UserCommandExecutesWithoutDropping()
    {
        var held = new TaskCompletionSource<VideoState>();
        var video = new FakeVideo { Reply = (page, command, _) => command.Action == "status" ? held.Task :
            Task.FromResult(new VideoState("fixture", page.Url, true, 0, 60, false, 1, false, 640, 360)) };
        using var follow = new FollowCoordinator(new FakeBrowser(), video, new FakeEpisodes(), new FakePip(), new());
        await follow.OpenAsync(new("BV1hjgG6jEa6", 1));
        var polling = follow.PollAsync();
        await follow.ExecuteAsync(new("toggle"));
        Assert.Contains("toggle", video.Commands);
        Assert.False(follow.Current.AutomaticPending);
        held.SetResult(new("fixture", follow.Current.Page!.Url, true, 0, 60, false, 1, false, 640, 360));
        await polling;
        Assert.DoesNotContain("ensurePlay", video.Commands);
    }

    [Fact]
    public async Task Poll_ReadyMedia_AutomaticOperationsRunOnce()
    {
        var video = new FakeVideo();
        var pip = new FakePip();
        using var follow = new FollowCoordinator(new FakeBrowser(), video, new FakeEpisodes(), pip, new());
        await follow.OpenAsync(new("BV1hjgG6jEa6", 1));
        await follow.PollAsync();
        await follow.PollAsync();
        Assert.False(follow.Current.AutomaticPending);
        Assert.Single(video.Commands, command => command == "ensurePlay");
        Assert.Single(video.Commands, command => command == "ensurePip");
        Assert.Equal(1, pip.Placements);
    }

    [Fact]
    public async Task Navigate_CurrentPart_DoesNotRestartOrChangeVersion()
    {
        using var follow = new FollowCoordinator(new FakeBrowser(), new FakeVideo(), new FakeEpisodes(), new FakePip(), new());
        await follow.OpenAsync(new("BV1hjgG6jEa6", 1));
        var request = follow.Current.Request;
        await follow.NavigateAsync(1);
        Assert.Equal(request, follow.Current.Request);
    }

    [Fact]
    public async Task Stop_CancelsPendingPollAndDrainsWithoutPublishingLateResult()
    {
        var held = new TaskCompletionSource<VideoState>();
        CancellationToken observedToken = default;
        var video = new FakeVideo { Reply = (_, _, token) => { observedToken = token; return held.Task; } };
        using var follow = new FollowCoordinator(new FakeBrowser(), video, new FakeEpisodes(), new FakePip(), new());
        await follow.OpenAsync(new("BV1hjgG6jEa6", 1));
        var polling = follow.PollAsync();
        var before = follow.Current;
        follow.Stop();
        var draining = follow.DrainAsync();
        Assert.True(observedToken.IsCancellationRequested);
        Assert.False(draining.IsCompleted);
        held.SetException(new IOException("late response after exit"));
        await polling;
        await draining;
        Assert.Equal(before, follow.Current);
    }

    [Fact]
    public async Task Preview_NewSelection_DoesNotChangeActiveFollowSession()
    {
        var cache = new EpisodeCache();
        var episodes = new FakeEpisodes();
        using var follow = new FollowCoordinator(new FakeBrowser(), new FakeVideo(), episodes, new FakePip(), cache);
        using var preview = new EpisodePreviewService(episodes, cache);
        await follow.OpenAsync(new("BV1hjgG6jEa6", 3));
        await follow.PollAsync();
        var playing = follow.Current;
        preview.Select("BV1MXfEY4EQ2");
        await preview.LoadAsync();
        Assert.Equal("BV1MXfEY4EQ2", preview.Current.Info!.Bvid);
        Assert.Equal(playing, follow.Current);
        Assert.False(follow.Current.AutomaticPending);
    }
}
