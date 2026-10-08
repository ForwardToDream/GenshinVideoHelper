using System.Text.RegularExpressions;

namespace GenshinVideoHelper.Core.Models;

public sealed record VideoIdentity(string Bvid, int Part)
{
    public string Url => $"https://www.bilibili.com/video/{Bvid}/?p={Part}";

    public static VideoIdentity Parse(string? value)
    {
        var uri = BilibiliUrl.Validate(value);
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

    public static bool TryParse(string? value, out VideoIdentity? identity)
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
