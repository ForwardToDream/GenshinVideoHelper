namespace GenshinVideoHelper.Infrastructure.Settings;

/// <summary>Portable user data lives beside the executable, independent of the working directory.</summary>
public static class ApplicationPaths
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, ".gvh");
    public static string SettingsPath => Path.Combine(DataDirectory, "config.json");
    public static string ProgressPath => Path.Combine(DataDirectory, "progress.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string BrowserProfileDirectory => Path.Combine(DataDirectory, "Chrome");
}
