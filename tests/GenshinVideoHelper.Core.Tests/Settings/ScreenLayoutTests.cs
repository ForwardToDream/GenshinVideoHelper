using GenshinVideoHelper.Core.Settings;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Settings;

public sealed class ScreenLayoutTests
{
    public static IEnumerable<object[]> Resolutions()
    {
        foreach (var (width, height) in new[] { (1280, 720), (1366, 768), (1920, 1080), (1920, 1200),
                     (2560, 1440), (3440, 1440), (3840, 2160), (1080, 1920) })
            yield return [width, height];
    }

    public static IEnumerable<object[]> Displays()
    {
        foreach (var resolution in Resolutions())
            foreach (var dpi in new[] { 96, 120, 144, 168, 192, 216, 240, 288 })
                yield return [resolution[0], resolution[1], dpi];
    }

    [Theory]
    [MemberData(nameof(Displays))]
    public void DefaultPip_KeepsVisibleBoundsAndDpiAwareControls(int width, int height, int dpi)
    {
        var work = new DisplayArea(-width + 30, 20, width - 30, height - 70);
        var bounds = ScreenLayout.DefaultPip(work, dpi, 420, 20, 16d / 9);
        AssertVisible(bounds, work);
        Assert.True(bounds.Width >= Math.Min(work.Width, 202 * dpi / 96));
        Assert.True(bounds.Height >= Math.Min(work.Height, 140 * dpi / 96));
        var remembered = ScreenLayout.Remember(bounds, work, dpi);
        Assert.True(remembered.IsValid);
        Assert.Equal(bounds, ScreenLayout.Restore(remembered, work, dpi));
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void DefaultPip_IsBottomLeftInWorkArea_WithNativeVideoAspect(int width, int height)
    {
        var work = new DisplayArea(-width + 30, 10, width - 30, height - 50);
        var layout = ScreenLayout.DefaultPip(work, 96, 420, 20, 16d / 9);
        var margin = 20;
        Assert.Equal(work.Left + margin, layout.Left);
        Assert.Equal(work.Bottom - margin, layout.Top + layout.Height);
        Assert.True(layout.IsValid);
        Assert.InRange(layout.Width, 1, work.Width / 2 + 1);
        Assert.InRange(layout.Height, 1, work.Height / 2 + 1);
        var videoWidth = layout.Width - 2;
        var videoHeight = layout.Height - 26;
        Assert.InRange(Math.Abs(videoWidth - videoHeight * 16d / 9), 0, 1.8);
        if (width / (double)height < 2 && height > width / 2)
            Assert.InRange(Math.Abs(videoWidth / (double)work.Width - 420 / 1920d), 0, 1d / work.Width);
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void CustomPlacement_RescalesPositionAndSizeAcrossResolutionAndNegativeOrigins(int width, int height)
    {
        var source = new DisplayArea(0, 0, 1920, 1040);
        var saved = new PipLayout(.3, 16d / 9, .1, .1, "display");
        var target = new DisplayArea(-width, 40, width, height - 40);
        var restored = ScreenLayout.Restore(saved, target, 96);
        Assert.InRange(Math.Abs((restored.Left - target.Left) / (double)(target.Width - restored.Width) - .1), 0, 1d / (width - restored.Width));
        Assert.InRange(Math.Abs((restored.Top - target.Top) / (double)(target.Height - restored.Height) - .1), 0, 1d / (target.Height - restored.Height));
        Assert.InRange(Math.Abs((restored.Width - 2) / (double)target.Width - .3), 0, 1d / width);
        Assert.InRange(Math.Abs((restored.Width - 2) / (double)(restored.Height - 26) - 16d / 9), 0, .02);
        var roundTrip = ScreenLayout.Restore(ScreenLayout.Remember(restored, target, 96), source, 96);
        var original = ScreenLayout.Restore(saved, source, 96);
        Assert.InRange(Math.Abs(original.Width - roundTrip.Width), 0, 2);
        Assert.InRange(Math.Abs(original.Left - roundTrip.Left), 0, 2);
    }

    [Fact]
    public void FullHdAt100Percent_UsesFactoryVideoSizeAndDips()
    {
        var work = new DisplayArea(0, 0, 1920, 1032);
        Assert.Equal(new PipPlacement(20, 750, 422, 262), ScreenLayout.DefaultPip(work, 96, 420, 20, 16d / 9));
        Assert.Equal(26, ScreenLayout.Pixels(13, 192));
        Assert.Equal(48, ScreenLayout.Pixels(24, 192));
    }

    [Theory]
    [MemberData(nameof(Displays))]
    public void RestoredLayout_StaysAtBottomRight_AcrossResolutionDpiAndOrigins(int width, int height, int dpi)
    {
        var saved = new PipLayout(.25, 16d / 9, 1, 1, "removed");
        var work = new DisplayArea(-width, -height, width, height - 48);
        var restored = ScreenLayout.Restore(saved, work, dpi);
        AssertVisible(restored, work);
        Assert.Equal(work.Right, restored.Left + restored.Width);
        Assert.Equal(work.Bottom, restored.Top + restored.Height);
        Assert.Equal(new PipLayout(.25, 16d / 9, 1, 1, "removed"), saved);
    }

    [Fact]
    public void TemporarySmallWorkArea_DoesNotChangeSavedSizeOrAspect()
    {
        var saved = new PipLayout(.25, 16d / 9, .3, .8);
        var large = new DisplayArea(0, 0, 3840, 2080);
        var first = ScreenLayout.Restore(saved, large, 192);
        AssertVisible(ScreenLayout.Restore(saved, new(0, 0, 320, 200), 288), new(0, 0, 320, 200));
        Assert.Equal(first, ScreenLayout.Restore(saved, large, 192));
        var work = new DisplayArea(0, 0, 1920, 1032);
        var offscreen = ScreenLayout.Remember(new(-1000, 5000, 640, 400), work, 96);
        Assert.Equal(0, offscreen.HorizontalPosition);
        Assert.Equal(1, offscreen.VerticalPosition);
        AssertVisible(ScreenLayout.Restore(offscreen, work, 96), work);
    }

    private static void AssertVisible(PipPlacement bounds, DisplayArea work)
    {
        Assert.True(bounds.IsValid);
        Assert.InRange(bounds.Left, work.Left, work.Right - bounds.Width);
        Assert.InRange(bounds.Top, work.Top, work.Bottom - bounds.Height);
    }
}
