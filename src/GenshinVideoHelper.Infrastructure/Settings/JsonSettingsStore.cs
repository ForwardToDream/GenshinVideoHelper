using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Infrastructure.Settings;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private readonly string _path = Path.GetFullPath(path);
    private bool _preserveInvalidFile;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsLoadResult Load()
    {
        var settings = new AppSettings();
        string? warning = null;
        var exists = File.Exists(_path);
        try
        {
            if (exists) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? throw new JsonException("配置内容为空。");
            settings.Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            _preserveInvalidFile = exists;
            settings = new AppSettings();
            warning = $"配置读取失败，暂用默认设置：{ex.Message}。保存时会备份原配置。";
        }
        if (!exists)
        {
            try { Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = $"无法创建根目录配置：{ex.Message}"; }
        }
        return new(settings, warning);
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var contents = JsonSerializer.Serialize(settings, JsonOptions);
        if (_preserveInvalidFile && File.Exists(_path))
        {
            File.Copy(_path, _path + $".{DateTime.Now:yyyyMMddHHmmssfff}.bak", overwrite: false);
            _preserveInvalidFile = false;
        }
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
