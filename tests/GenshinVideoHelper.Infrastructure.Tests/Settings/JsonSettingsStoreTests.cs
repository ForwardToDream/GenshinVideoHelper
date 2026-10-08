using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Settings;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
    private string PathFor(string name = "settings.json") => Path.Combine(_folder, name);

    [Fact]
    public void Load_MissingFile_CreatesSettingsWithoutPresetUrl()
    {
        var loaded = new JsonSettingsStore(PathFor()).Load();
        Assert.Null(loaded.Warning);
        Assert.True(File.Exists(PathFor()));
        Assert.Equal("", loaded.Settings.VideoUrl);
        Assert.Equal(9, loaded.Settings.Hotkeys.Count);
        Assert.DoesNotContain("DefaultVideoUrl", File.ReadAllText(PathFor()));
    }

    [Fact]
    public void Save_CustomBindingsAndPart_RoundTripsWithoutEscapingPlus()
    {
        var store = new JsonSettingsStore(PathFor());
        var settings = store.Load().Settings;
        settings.VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=4";
        settings.SelectedVideoLibraryId = null;
        settings.SeekSeconds = 10;
        settings.PipWidth = 480;
        settings.HotkeysEnabled = false;
        settings.Hotkeys[HotkeyAction.NextEpisode] = "Ctrl+Shift+F8";
        settings.Hotkeys[HotkeyAction.ReversePipVisibility] = "F8";
        store.Save(settings);
        var actual = store.Load().Settings;
        Assert.Equal(settings.VideoUrl, actual.VideoUrl);
        Assert.Null(actual.SelectedVideoLibraryId);
        Assert.Equal(10, actual.SeekSeconds);
        Assert.Equal(480, actual.PipWidth);
        Assert.False(actual.HotkeysEnabled);
        Assert.Equal("F8", actual.Hotkeys[HotkeyAction.ReversePipVisibility]);
        Assert.Contains("Ctrl+Shift+F8", File.ReadAllText(PathFor()));
    }

    [Theory]
    [InlineData("broken JSON")] [InlineData("null")] [InlineData("{\"SeekSeconds\":\"bad\"}")]
    public void Load_InvalidFile_PreservesOriginalUntilSaveAndBacksItUp(string contents)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), contents);
        var store = new JsonSettingsStore(PathFor());
        var loaded = store.Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal(contents, File.ReadAllText(PathFor()));
        store.Save(loaded.Settings);
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Directory.GetFiles(_folder, "*.bak"))));
        Assert.Null(new JsonSettingsStore(PathFor()).Load().Warning);
    }

    [Fact]
    public void Load_NullUrl_NormalizesWithoutCrashing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), "{\"VideoUrl\":null}");
        Assert.Equal("", new JsonSettingsStore(PathFor()).Load().Settings.VideoUrl);
    }

    [Fact]
    public void FindApplicationRoot_PublishedSubdirectory_ResolvesByLauncher()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor("start.cmd"), "fixture");
        Assert.Equal(_folder, ApplicationPaths.FindApplicationRoot(Path.Combine(_folder, "artifacts", "GenshinVideoHelper")));
    }

    public void Dispose()
    {
        // Delete only the unique temporary directory allocated by this test instance.
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
