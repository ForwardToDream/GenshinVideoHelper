using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace GenshinVideoHelper.Core.Settings;

public sealed class AppSettings
{
    public string VideoUrl { get; set; } = "";
    public string? SelectedVideoLibraryId { get; set; } = Library.VideoLibraryCatalog.DefaultLibraryId;
    public int SeekSeconds { get; set; } = 5;
    public int PipWidth { get; set; } = 420;
    public int PipMargin { get; set; } = 20;
    public bool HotkeysEnabled { get; set; } = true;
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = HotkeyBindings.Defaults();
    [JsonIgnore] public string? LoadWarning { get; private set; }
    private string? _loadedPath;
    private bool _preserveInvalidFile;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // Chrome profile and root settings use the current product name.
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinVideoHelper");
    public static string SettingsPath => Path.Combine(FindApplicationRoot(AppContext.BaseDirectory), "GenshinVideoHelper.settings.json");

    public static string FindApplicationRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GenshinVideoHelper.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "start.cmd"))) return directory.FullName;
        return Path.GetFullPath(startDirectory);
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        var settings = new AppSettings { _loadedPath = path };
        var exists = File.Exists(path);
        try
        {
            if (exists)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                settings = JsonSerializer.Deserialize<AppSettings>(document, JsonOptions) ?? throw new JsonException("配置内容为空。");
                settings._loadedPath = path;
            }
            settings.SeekSeconds = Math.Clamp(settings.SeekSeconds, 1, 60);
            settings.PipWidth = Math.Clamp(settings.PipWidth, 280, 800);
            settings.PipMargin = Math.Clamp(settings.PipMargin, 0, 100);
            if (!Browser.VideoIdentity.TryParse(settings.VideoUrl, out _)) settings.VideoUrl = "";
            settings.Hotkeys = HotkeyBindings.Validate(settings.Hotkeys ?? HotkeyBindings.Defaults());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            settings = new AppSettings { _loadedPath = path, _preserveInvalidFile = exists, LoadWarning = $"配置读取失败，暂用默认设置：{ex.Message}。保存时会备份原配置。" };
        }
        if (!exists)
        {
            try { settings.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { settings.LoadWarning = $"无法创建根目录配置：{ex.Message}"; }
        }
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= _loadedPath ?? SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var contents = JsonSerializer.Serialize(this, JsonOptions);
        if (_preserveInvalidFile && File.Exists(path))
        {
            File.Copy(path, path + $".{DateTime.Now:yyyyMMddHHmmssfff}.bak", overwrite: false);
            _preserveInvalidFile = false;
        }
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
