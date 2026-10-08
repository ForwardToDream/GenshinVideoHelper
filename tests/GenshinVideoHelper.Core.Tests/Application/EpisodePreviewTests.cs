using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Application;

public sealed class EpisodePreviewTests
{
    [Fact]
    public async Task Select_NewVideo_CancelsOldRequestAndRejectsLateResult()
    {
        var held = new TaskCompletionSource<BilibiliVideoInfo>();
        var provider = new FakeEpisodes { Reply = (id, _) => id.Bvid == "BV1hjgG6jEa6" ? held.Task : Task.FromResult(FakeEpisodes.Info(id)) };
        using var preview = new EpisodePreviewService(provider, new());
        preview.Select("BV1hjgG6jEa6");
        var old = preview.LoadAsync();
        preview.Select("BV1MXfEY4EQ2");
        await preview.LoadAsync();
        Assert.True(provider.Tokens[0].IsCancellationRequested);
        held.SetResult(FakeEpisodes.Info(new("BV1hjgG6jEa6", 1)));
        await old;
        Assert.Equal("BV1MXfEY4EQ2", preview.Current.Info!.Bvid);
    }

    [Fact]
    public async Task Select_CachedPart_ReusesMetadataAndPreservesInvalidPartForCorrection()
    {
        var provider = new FakeEpisodes();
        using var preview = new EpisodePreviewService(provider, new());
        preview.Select("BV1hjgG6jEa6");
        await preview.LoadAsync();
        preview.Select("https://www.bilibili.com/video/BV1hjgG6jEa6/?p=99");
        Assert.Equal(99, preview.Current.Identity!.Part);
        Assert.Equal(4, preview.Current.Info!.Episodes.Count);
        Assert.False(preview.Current.Loading);
        Assert.Equal(1, provider.Reads);
    }

    [Fact]
    public async Task Load_FailureAndRetry_RecoversWithoutBrowserSession()
    {
        var provider = new FakeEpisodes { Reply = (_, _) => Task.FromException<BilibiliVideoInfo>(new IOException("fixture failure")) };
        using var preview = new EpisodePreviewService(provider, new());
        preview.Select("BV1hjgG6jEa6");
        await preview.LoadAsync();
        Assert.Equal("fixture failure", preview.Current.Error);
        provider.Reply = null;
        preview.Select("BV1hjgG6jEa6", forceReload: true);
        await preview.LoadAsync();
        Assert.Null(preview.Current.Error);
        Assert.NotNull(preview.Current.Info);
    }
}
