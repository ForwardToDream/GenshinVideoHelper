using System.Text.Json;
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

    // Embedded JSON is a first-run template, not the user's live catalog.
    public static VideoLibrary Parse(string json) =>
        (JsonSerializer.Deserialize<VideoLibrary>(json) ?? throw new InvalidDataException("视频库内容为空。")).Normalize();
}
