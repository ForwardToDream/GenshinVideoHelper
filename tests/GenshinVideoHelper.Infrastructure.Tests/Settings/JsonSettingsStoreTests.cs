using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Models;
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
        Assert.Null(loaded.Settings.SelectedVideoLibraryId);
        Assert.True(loaded.Settings.IsFirstLaunch);
        Assert.Equal(9, loaded.Settings.Hotkeys.Count);
        Assert.Null(loaded.Settings.PipLayout);
        Assert.Equal(BrowserPreference.Auto, loaded.Settings.BrowserPreference);
        Assert.DoesNotContain("DefaultVideoUrl", File.ReadAllText(PathFor()));
    }

    [Fact]
    public void Save_CustomBindingsAndPart_RoundTripsWithoutEscapingPlus()
    {
        var store = new JsonSettingsStore(PathFor());
        var settings = store.Load().Settings;
        settings.VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=4";
        settings.SelectedVideoLibraryId = null;
        settings.IsFirstLaunch = false;
        settings.SeekSeconds = 10;
        settings.PipWidth = 480;
        settings.PipLayout = new(.333, 1.777, .25, .8, "DISPLAY2");
        settings.HotkeysEnabled = false;
        settings.Hotkeys[HotkeyAction.NextEpisode] = "Ctrl+Shift+F8";
        settings.Hotkeys[HotkeyAction.ReversePipVisibility] = "F8";
        store.Save(settings);
        var actual = store.Load().Settings;
        Assert.Equal(settings.VideoUrl, actual.VideoUrl);
        Assert.Null(actual.SelectedVideoLibraryId);
        Assert.False(actual.IsFirstLaunch);
        Assert.Equal(10, actual.SeekSeconds);
        Assert.Equal(480, actual.PipWidth);
        Assert.Equal(settings.PipLayout, actual.PipLayout);
        Assert.DoesNotContain("IsValid", File.ReadAllText(PathFor()));
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
    public void Load_FirstLaunchFieldMissing_DefaultsToFirstLaunch()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), "{\"SeekSeconds\":10}");
        var settings = new JsonSettingsStore(PathFor()).Load().Settings;
        Assert.True(settings.IsFirstLaunch);
        Assert.Null(settings.SelectedVideoLibraryId);
        Assert.Equal(10, settings.SeekSeconds);
    }

    [Fact]
    public void Load_NullUrl_NormalizesWithoutCrashing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), "{\"VideoUrl\":null}");
        Assert.Equal("", new JsonSettingsStore(PathFor()).Load().Settings.VideoUrl);
    }

    [Fact]
    public void Load_ObsoletePixelBounds_CannotOverrideProportionsOrOtherSettings()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), """
            {"SeekSeconds":10,"HotkeysEnabled":false,"PipPlacement":{"Left":15,"Top":1451,
              "Width":988,"Height":660,"WorkArea":{"Left":0,"Top":0,"Width":1920,"Height":1032}}}
            """);
        var store = new JsonSettingsStore(PathFor());
        var loaded = store.Load();
        Assert.Null(loaded.Warning);
        Assert.Null(loaded.Settings.PipLayout);
        Assert.Equal(10, loaded.Settings.SeekSeconds);
        Assert.False(loaded.Settings.HotkeysEnabled);
        store.Save(loaded.Settings);
        Assert.DoesNotContain("PipPlacement", File.ReadAllText(PathFor()));
    }

    [Theory]
    [InlineData(BrowserPreference.Auto)] [InlineData(BrowserPreference.Chrome)] [InlineData(BrowserPreference.Edge)]
    public void BrowserChoice_RoundTripsAsReadableEnum(BrowserPreference preference)
    {
        var store = new JsonSettingsStore(PathFor());
        var settings = store.Load().Settings;
        settings.BrowserPreference = preference;
        store.Save(settings);
        Assert.Contains($"\"BrowserPreference\": \"{preference}\"", File.ReadAllText(PathFor()));
        Assert.Equal(preference, store.Load().Settings.BrowserPreference);
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"BrowserPreference\":99}")]
    public void MissingOrInvalidNumericBrowserChoice_UsesAuto(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(), json);
        Assert.Equal(BrowserPreference.Auto, new JsonSettingsStore(PathFor()).Load().Settings.BrowserPreference);
    }


    public void Dispose()
    {
        // Delete only the unique temporary directory allocated by this test instance.
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
