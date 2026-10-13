using System.Text.Encodings.Web;
using System.Text.Json;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Infrastructure.Library;

/// <summary>One editable JSON per library. Defaults seed a missing directory once.</summary>
public sealed class JsonVideoLibraryStore(string directory) : IVideoLibraryStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public VideoLibraryCatalog Load()
    {
        var libraries = new List<VideoLibrary>();
        var errors = new List<string>();
        try
        {
            if (!Directory.Exists(_directory))
            {
                Directory.CreateDirectory(_directory);
                var defaults = VideoLibraryLoader.LoadBuiltIn();
                errors.AddRange(defaults.Errors);
                foreach (var library in defaults.Libraries) Save(library);
            }
            foreach (var path in Directory.GetFiles(_directory, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var library = VideoLibraryLoader.Parse(File.ReadAllText(path));
                    if (library.Id != Path.GetFileNameWithoutExtension(path))
                        throw new InvalidDataException("库编号必须与 JSON 文件名一致。");
                    libraries.Add(library);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    AppLog.Warn("Library", $"视频库读取失败：{path}", ex);
                    errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn("Library", $"视频库目录读取失败：{_directory}", ex);
            errors.Add($"视频库目录：{ex.Message}");
        }
        return new(libraries.AsReadOnly(), errors.AsReadOnly());
    }

    public void Save(VideoLibrary library, bool overwrite = false)
    {
        library = library.Normalize();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, library.Id + ".json");
        if (!overwrite && File.Exists(path)) throw new IOException("库编号已存在，请换一个编号。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(library, JsonOptions));
            if (overwrite && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
