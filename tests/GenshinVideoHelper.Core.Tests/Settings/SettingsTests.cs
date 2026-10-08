using GenshinVideoHelper.Core.Settings;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Settings;

public sealed class SettingsTests
{
    [Fact]
    public void Normalize_NullUrlAndOutOfRangeValues_UsesValidDefaults()
    {
        var settings = new AppSettings { VideoUrl = null!, SeekSeconds = 200, PipWidth = 1, PipMargin = -2, Hotkeys = null! };
        settings.Normalize();
        Assert.Equal("", settings.VideoUrl);
        Assert.Equal(60, settings.SeekSeconds);
        Assert.Equal(280, settings.PipWidth);
        Assert.Equal(0, settings.PipMargin);
        Assert.Equal(9, settings.Hotkeys.Count);
    }

    [Theory]
    [InlineData("Alt", false)] [InlineData("1", false)] [InlineData("Alt+Alt+1", false)]
    [InlineData("Ctrl+Potato", false)] [InlineData("Alt+F25", false)] [InlineData(" alt + left ", true)]
    public void Gesture_ValidatesCombination(string input, bool expected) => Assert.Equal(expected, HotkeyGesture.TryParse(input, out _));

    [Theory]
    [InlineData("~")] [InlineData("`")] [InlineData("Oem3")]
    public void HoldGesture_PhysicalTildeAliases_NormalizesWithoutShift(string input)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var gesture, allowUnmodified: true));
        Assert.Equal(new HotkeyGesture(0, 0xC0, "~"), gesture);
    }

    [Fact]
    public void Validate_DuplicateNormalizedBindings_RejectsDraft()
    {
        var bindings = HotkeyBindings.Defaults();
        bindings[HotkeyAction.NextEpisode] = "alt+2";
        Assert.Throws<ArgumentException>(() => HotkeyBindings.Validate(bindings));
    }
}
