using System.Text.Json.Serialization;

namespace GenshinVideoHelper.Core.Settings;

public readonly record struct DisplayArea(int Left, int Top, int Width, int Height)
{
    [JsonIgnore] public int Right => Left + Width;
    [JsonIgnore] public int Bottom => Top + Height;
    [JsonIgnore] public bool IsValid => Width is > 0 and <= 32768 && Height is > 0 and <= 32768 &&
        (long)Left + Width <= int.MaxValue && (long)Top + Height <= int.MaxValue;
}

/// <summary>UI uses DIPs; video uses work-area proportions; native operations use pixels.</summary>
public static class ScreenLayout
{
    public const double PipBorder = 1, PipBarHeight = 24, PipMinimumWidth = 202, PipMinimumHeight = 140;
    public static double DpiScale(double dpi) => double.IsFinite(dpi) && dpi >= 96 ? dpi / 96 : 1;
    public static int Pixels(double dips, double dpi) => Math.Max(1, (int)Math.Round(dips * DpiScale(dpi)));
    public static int Margin(int dips, double dpi) => Math.Max(0, (int)Math.Round(dips * DpiScale(dpi)));

    public static PipPlacement DefaultPip(DisplayArea work, double dpi, int referenceWidth, int marginDips, double aspect)
    {
        var margin = Math.Min(Margin(marginDips, dpi), Math.Min(work.Width, work.Height) / 10);
        var size = PipSize(referenceWidth / 1920d, aspect, work, dpi, limitDefaultSize: true);
        return new(work.Left + Math.Min(margin, work.Width - size.Width),
            work.Bottom - Math.Min(margin, work.Height - size.Height) - size.Height, size.Width, size.Height);
    }

    public static PipLayout Remember(PipPlacement bounds, DisplayArea work, double dpi, string? monitorDevice = null)
    {
        var border = Pixels(PipBorder, dpi);
        var videoWidth = Math.Max(1, bounds.Width - border * 2);
        var videoHeight = Math.Max(1, bounds.Height - Pixels(PipBarHeight, dpi) - border * 2);
        return new(Math.Clamp(videoWidth / (double)work.Width, double.Epsilon, 1), videoWidth / (double)videoHeight,
            Position(bounds.Left - (double)work.Left, work.Width - bounds.Width),
            Position(bounds.Top - (double)work.Top, work.Height - bounds.Height), monitorDevice);
    }

    public static PipPlacement Restore(PipLayout saved, DisplayArea work, double dpi)
    {
        var size = PipSize(saved.WidthRatio, saved.AspectRatio, work, dpi, limitDefaultSize: false);
        return new(work.Left + (int)Math.Round(saved.HorizontalPosition * (work.Width - size.Width)),
            work.Top + (int)Math.Round(saved.VerticalPosition * (work.Height - size.Height)), size.Width, size.Height);
    }

    private static double Position(double offset, int travel) => travel <= 0 ? 0 : Math.Clamp(offset / travel, 0, 1);

    private static (int Width, int Height) PipSize(double widthRatio, double aspect, DisplayArea work, double dpi, bool limitDefaultSize)
    {
        var border = Pixels(PipBorder, dpi);
        var bar = Pixels(PipBarHeight, dpi);
        var minimumWidth = Math.Min(work.Width, Pixels(PipMinimumWidth, dpi));
        var minimumHeight = Math.Min(work.Height, Pixels(PipMinimumHeight, dpi));
        var maximumWidth = limitDefaultSize ? Math.Max(minimumWidth, work.Width / 2) : work.Width;
        var maximumHeight = limitDefaultSize ? Math.Max(minimumHeight, work.Height / 2) : work.Height;
        var ratio = double.IsFinite(aspect) && aspect > 0 ? aspect : 16d / 9;
        var videoWidth = Math.Clamp((int)Math.Round(widthRatio * work.Width), 1, Math.Max(1, maximumWidth - border * 2));
        var videoHeight = Math.Max(1, (int)Math.Round(videoWidth / ratio));
        var availableHeight = Math.Max(1, maximumHeight - bar - border * 2);
        if (videoHeight > availableHeight)
        {
            videoHeight = availableHeight;
            videoWidth = Math.Min(videoWidth, Math.Max(1, (int)Math.Round(videoHeight * ratio)));
        }
        return (Math.Clamp(videoWidth + border * 2, minimumWidth, maximumWidth),
            Math.Clamp(videoHeight + bar + border * 2, minimumHeight, maximumHeight));
    }
}
