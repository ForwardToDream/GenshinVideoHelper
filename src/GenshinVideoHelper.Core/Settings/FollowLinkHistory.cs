using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Settings;

public sealed record FollowLinkEntry(string Url, DateTimeOffset FollowedAt);

public static class FollowLinkHistory
{
    public static List<FollowLinkEntry> Normalize(IEnumerable<FollowLinkEntry>? entries) =>
        (entries ?? []).Where(entry => entry is not null && VideoIdentity.TryParse(entry.Url, out _))
            .Select(entry => entry with { Url = VideoIdentity.Parse(entry.Url).Url })
            .OrderByDescending(entry => entry.FollowedAt)
            .DistinctBy(entry => entry.Url, StringComparer.Ordinal).ToList();

    public static List<FollowLinkEntry> Remember(IEnumerable<FollowLinkEntry> entries,
        VideoIdentity identity, DateTimeOffset followedAt) =>
        [new(identity.Url, followedAt), .. entries.Where(entry => entry.Url != identity.Url)];
}
