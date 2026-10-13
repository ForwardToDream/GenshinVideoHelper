using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Settings;

public sealed class FollowLinkHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static VideoIdentity Video(int part) => new("BV1hjgG6jEa6", part);

    [Fact]
    public void DefaultsAndNullHistory_AreEmpty()
    {
        Assert.Empty(new AppSettings().ManualFollowHistory);
        var settings = new AppSettings { ManualFollowHistory = null! };
        settings.Normalize();
        Assert.Empty(settings.ManualFollowHistory);
    }

    [Fact]
    public void Normalize_CanonicalizesParts_DeduplicatesAndOrdersByLatestFollow()
    {
        var settings = new AppSettings
        {
            ManualFollowHistory = [
                new("BV1hjgG6jEa6", At),
                new(Video(3).Url, At.AddMinutes(1)),
                new("https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=test&p=1", At.AddMinutes(2)),
                new("https://example.com/video/BV1hjgG6jEa6/", At.AddHours(1)),
                new("https://www.bilibili.com/video/BV1hjgG6jEa6/?p=0", At),
                null!, new(null!, At)]
        };
        settings.Normalize();
        Assert.Equal([Video(1).Url, Video(3).Url], settings.ManualFollowHistory.Select(entry => entry.Url));
        Assert.Equal(At.AddMinutes(2), settings.ManualFollowHistory[0].FollowedAt);
    }

    [Fact]
    public void Remember_RepeatedPartMovesToTopWithoutDuplicates_OtherPartsRemain()
    {
        List<FollowLinkEntry> old = [new(Video(3).Url, At.AddMinutes(1)), new(Video(1).Url, At)];
        var history = FollowLinkHistory.Remember(old, Video(1), At.AddMinutes(2));
        Assert.Equal([Video(1).Url, Video(3).Url], history.Select(entry => entry.Url));
        Assert.Equal(At.AddMinutes(2), history[0].FollowedAt);
        Assert.Equal(Video(3).Url, old[0].Url);
        history = FollowLinkHistory.Remember(history, Video(2), At.AddMinutes(3));
        Assert.Equal([Video(2).Url, Video(1).Url, Video(3).Url], history.Select(entry => entry.Url));
    }
}
