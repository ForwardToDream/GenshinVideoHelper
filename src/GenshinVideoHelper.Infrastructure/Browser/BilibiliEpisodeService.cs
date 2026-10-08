using System.Text.Json;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Contracts;

namespace GenshinVideoHelper.Infrastructure.Browser;

public sealed class BilibiliEpisodeService : IEpisodeProvider, IDisposable
{
    private readonly CdpClient _cdp = new();
    private readonly HttpClient _http;
    public BilibiliEpisodeService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GenshinVideoHelper/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
    }

    public async Task<BilibiliVideoInfo> ReadAsync(BrowserPage page, CancellationToken token = default)
    {
        var identity = VideoIdentity.Parse(page.Url);
        try
        {
            var reply = await _cdp.SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "(() => {const d=window.__INITIAL_STATE__?.videoData;return d?.bvid && Array.isArray(d.pages)?{bvid:d.bvid,title:d.title,pages:d.pages}:null;})()",
                returnByValue = true
            }, token);
            if (reply.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var data) &&
                data.ValueKind == JsonValueKind.Object && data.GetProperty("bvid").GetString() == identity.Bvid)
                return ParseVideoData(data, identity);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException) { }
        token.ThrowIfCancellationRequested();
        return await ReadFromApiAsync(identity, token);
    }

    public async Task<BilibiliVideoInfo> ReadFromApiAsync(VideoIdentity identity, CancellationToken token = default)
    {
        using var response = await _http.GetAsync("https://api.bilibili.com/x/web-interface/view?bvid=" + Uri.EscapeDataString(identity.Bvid), token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (json.RootElement.GetProperty("code").GetInt32() != 0)
            throw new InvalidOperationException("未能读取分集信息，可重试；视频播放控制仍可使用。");
        return ParseVideoData(json.RootElement.GetProperty("data"), identity);
    }

    public static BilibiliVideoInfo ParseVideoData(JsonElement data, VideoIdentity identity)
    {
        if (data.GetProperty("bvid").GetString() != identity.Bvid)
            throw new InvalidOperationException("分集信息与当前视频不一致，请重试。");
        var episodes = data.GetProperty("pages").EnumerateArray().Select(p => new EpisodeInfo(
            p.GetProperty("page").GetInt32(), p.GetProperty("cid").GetInt64(),
            p.GetProperty("part").GetString() ?? "未命名分集", Math.Max(0, p.GetProperty("duration").GetDouble())))
            .Where(p => p.Number > 0).OrderBy(p => p.Number).ToArray();
        if (episodes.Length == 0) throw new InvalidOperationException("视频暂未提供分集信息，请重试。");
        if (!episodes.Any(p => p.Number == identity.Part))
            throw new InvalidOperationException($"此视频没有 P{identity.Part}，请选择有效分集。");
        return new(identity.Bvid, data.TryGetProperty("title", out var title) ? title.GetString() ?? "B 站视频" : "B 站视频", identity.Part, episodes);
    }

    public void Dispose() => _http.Dispose();
}
