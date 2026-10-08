using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Application;

public sealed class EpisodeCache
{
    private readonly Dictionary<string, BilibiliVideoInfo> _items = new(StringComparer.Ordinal);
    public BilibiliVideoInfo? Get(VideoIdentity identity) => _items.TryGetValue(identity.Bvid, out var info) ? info with { CurrentPart = identity.Part } : null;
    public void Store(BilibiliVideoInfo info) => _items[info.Bvid] = info;
}
