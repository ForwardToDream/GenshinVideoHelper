using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace GenshinVideoHelper.Core.Browser;

public sealed record VideoIdentity(string Bvid, int Part)
{
    public string Url => $"https://www.bilibili.com/video/{Bvid}/?p={Part}";

    public static VideoIdentity Parse(string value)
    {
        var uri = ChromeBrowser.ValidateVideoUrl(value);
        var match = Regex.Match(uri.AbsolutePath, @"^/video/(BV[0-9a-zA-Z]{10})/?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new ArgumentException("请输入普通 B 站视频链接或完整 BV 号。");
        var part = 1;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) != "p") continue;
            if (pair.Length != 2 || !int.TryParse(Uri.UnescapeDataString(pair[1]), out part) || part < 1)
                throw new ArgumentException("分 P 编号必须是大于零的整数。");
            break;
        }
        return new("BV" + match.Groups[1].Value[2..], part);
    }

    public static bool TryParse(string value, out VideoIdentity? identity)
    {
        try { identity = Parse(value); return true; }
        catch (ArgumentException) { identity = null; return false; }
    }
}

public sealed record EpisodeInfo(int Number, long Cid, string Title, double Duration)
{
    public string DisplayText
    {
        get
        {
            var time = TimeSpan.FromSeconds(Duration);
            var duration = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
            return $"P{Number} · {Title} · {duration}";
        }
    }
}

public sealed record BilibiliVideoInfo(string Bvid, string Title, int CurrentPart, IReadOnlyList<EpisodeInfo> Episodes);

public sealed class BilibiliEpisodeService : IDisposable
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
