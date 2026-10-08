namespace GenshinVideoHelper.Core.Models;

public static class BilibiliUrl
{
    public static bool IsBilibili(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) &&
        url.Scheme == Uri.UriSchemeHttps &&
        (url.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
         url.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    public static Uri Validate(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.StartsWith("BV", StringComparison.OrdinalIgnoreCase)) value = "https://www.bilibili.com/video/" + value;
        if (!IsBilibili(value)) throw new ArgumentException("请输入完整的 https://www.bilibili.com 视频链接或 BV 号。");
        return new Uri(value);
    }
}
