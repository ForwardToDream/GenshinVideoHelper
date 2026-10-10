using GenshinVideoHelper.Infrastructure.Settings;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Settings;

public sealed class ApplicationPathsTests
{
    [Fact]
    public void Paths_StayBesideExecutableWithoutResolvingWorkspaceRoot()
    {
        var data = Path.Combine(AppContext.BaseDirectory, ".gvh");
        Assert.Equal(data, ApplicationPaths.DataDirectory);
        Assert.Equal(Path.Combine(data, "config.json"), ApplicationPaths.SettingsPath);
        Assert.Equal(Path.Combine(data, "progress.json"), ApplicationPaths.ProgressPath);
        Assert.Equal(Path.Combine(data, "logs"), ApplicationPaths.LogDirectory);
        Assert.Equal(Path.Combine(data, "Chrome"), ApplicationPaths.BrowserProfileDirectory);
        Assert.NotEqual(Path.Combine(Directory.GetCurrentDirectory(), "GenshinVideoHelper.settings.json"), ApplicationPaths.SettingsPath);
    }

    [Fact]
    public void Stores_CreateNestedDataDirectoryAndKeepProgressBackupTogether()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(folder, ".gvh");
            var settings = new JsonSettingsStore(Path.Combine(data, "config.json")).Load();
            Assert.Null(settings.Warning);
            var path = Path.Combine(data, "progress.json");
            var store = new GenshinVideoHelper.Infrastructure.Progress.JsonProgressStore(path);
            store.Save(new());
            store = new(path);
            store.Load();
            store.Save(new());
            Assert.True(File.Exists(Path.Combine(data, "config.json")));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".bak"));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
