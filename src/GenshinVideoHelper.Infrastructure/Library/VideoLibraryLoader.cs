using System.Text.Json;
using System.Text.RegularExpressions;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Infrastructure.Library;

public static class VideoLibraryLoader
{
    private const string ResourcePrefix = "GenshinVideoHelper.VideoLibraries.";

    public static VideoLibraryCatalog LoadBuiltIn()
    {
        var assembly = typeof(VideoLibraryLoader).Assembly;
        var libraries = new List<VideoLibrary>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name) ?? throw new IOException("视频库资源不存在。");
                using var reader = new StreamReader(stream);
                var library = Parse(reader.ReadToEnd());
                if (!ids.Add(library.Id)) throw new InvalidDataException($"视频库编号重复：{library.Id}");
                libraries.Add(library);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            {
                AppLog.Warn("Library", $"视频库 {name} 加载失败。", ex);
                errors.Add($"{name}: {ex.Message}");
            }
        }
        AppLog.Info("Library", $"已加载 {libraries.Count} 个内置视频库。");
        return new(libraries.AsReadOnly(), errors.AsReadOnly());
    }

    public static VideoLibrary Parse(string json)
    {
        var library = JsonSerializer.Deserialize<VideoLibrary>(json) ?? throw new InvalidDataException("视频库内容为空。");
        if (string.IsNullOrWhiteSpace(library.Id) || !Regex.IsMatch(library.Id, "^[a-z0-9-]+$") ||
            string.IsNullOrWhiteSpace(library.Name) || string.IsNullOrWhiteSpace(library.CreatorName) || library.Videos is not { Count: > 0 })
            throw new InvalidDataException("视频库缺少编号、名称、UP 主或视频列表。");
        if (!Uri.TryCreate(library.CreatorUrl, UriKind.Absolute, out var creator) || creator.Scheme != Uri.UriSchemeHttps ||
            creator.Host != "space.bilibili.com" || creator.UserInfo.Length > 0 || !creator.IsDefaultPort || !Regex.IsMatch(creator.AbsolutePath, "^/[0-9]+/?$"))
            throw new InvalidDataException("UP 主链接必须是 B 站个人空间地址。");
        var numbers = new HashSet<int>();
        var bvids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var video in library.Videos)
        {
            if (video is null || video.Number <= 0 || string.IsNullOrWhiteSpace(video.Title) ||
                !VideoIdentity.TryParse(video.Bvid, out var identity) || identity!.Bvid != video.Bvid ||
                !numbers.Add(video.Number) || !bvids.Add(video.Bvid))
                throw new InvalidDataException("视频的序号、名称或 BV 号无效或重复。");
        }
        return library with { Description = library.Description ?? "", Videos = Array.AsReadOnly(library.Videos.OrderBy(video => video.Number).ToArray()) };
    }
}
