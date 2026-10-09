namespace GenshinVideoHelper.Infrastructure.Settings;

public static class ApplicationPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinVideoHelper");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string SettingsPath => Path.Combine(FindApplicationRoot(AppContext.BaseDirectory), "GenshinVideoHelper.settings.json");
    public static string ProgressPath => Path.Combine(FindApplicationRoot(AppContext.BaseDirectory), "GenshinVideoHelper.progress.json");

    public static string FindApplicationRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GenshinVideoHelper.sln")) || File.Exists(Path.Combine(directory.FullName, "start.cmd"))) return directory.FullName;
        return Path.GetFullPath(startDirectory);
    }
}
