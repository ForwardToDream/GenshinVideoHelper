using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Tests.Support;

internal sealed class FakeBrowser : IBrowserSession
{
    public BrowserPage Page { get; private set; } = new("tab", "fixture", new VideoIdentity("BV1hjgG6jEa6", 1).Url, "ws://unused");
    public Task<BrowserPage> OpenAsync(string url, CancellationToken token = default) => Task.FromResult(Page = Page with { Url = url });
    public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default) { Page = page with { Url = identity.Url }; return Task.CompletedTask; }
    public Task<BrowserPage?> GetPageAsync(string targetId, CancellationToken token = default) => Task.FromResult<BrowserPage?>(Page);
    public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => Task.FromResult(42);
    public Task CloseAsync() => Task.CompletedTask;
}

internal sealed class FakeVideo : IVideoPlayer
{
    public Func<BrowserPage, VideoCommand, CancellationToken, Task<VideoState>>? Reply { get; set; }
    public List<string> Commands { get; } = [];
    public Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command, CancellationToken token = default)
    {
        Commands.Add(command.Action);
        return Reply?.Invoke(page, command, token) ?? Task.FromResult(new VideoState("fixture", page.Url, false, 0, 60, false, 1, command.Action == "ensurePip", 640, 360));
    }
}

internal sealed class FakeEpisodes : IEpisodeProvider
{
    public Func<VideoIdentity, CancellationToken, Task<BilibiliVideoInfo>>? Reply { get; set; }
    public List<CancellationToken> Tokens { get; } = [];
    public int Reads { get; private set; }
    public static BilibiliVideoInfo Info(VideoIdentity identity) => new(identity.Bvid, "fixture", identity.Part,
        Enumerable.Range(1, 4).Select(p => new EpisodeInfo(p, 40000000000L + p, "Part " + p, 60)).ToArray());
    public Task<BilibiliVideoInfo> ReadAsync(BrowserPage page, CancellationToken token = default) => ReadFromApiAsync(VideoIdentity.Parse(page.Url), token);
    public Task<BilibiliVideoInfo> ReadFromApiAsync(VideoIdentity identity, CancellationToken token = default)
    { Reads++; Tokens.Add(token); return Reply?.Invoke(identity, token) ?? Task.FromResult(Info(identity)); }
}

internal sealed class FakePip : IPipController
{
    public int Placements { get; private set; }
    public Task ObserveAsync(int pid, bool enabled, CancellationToken token) => Task.CompletedTask;
    public Task PlaceAsync(int pid, VideoState state, CancellationToken token) { Placements++; return Task.CompletedTask; }
}
