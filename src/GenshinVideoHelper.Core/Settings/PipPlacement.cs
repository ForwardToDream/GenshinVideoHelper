using System.Text.Json.Serialization;

namespace GenshinVideoHelper.Core.Settings;

/// <summary>Transient full-frame bounds in physical screen pixels.</summary>
public sealed record PipPlacement(int Left, int Top, int Width, int Height)
{
    [JsonIgnore]
    public bool IsValid => Width is > 0 and <= 32768 && Height is > 0 and <= 32768 &&
        (long)Left + Width <= int.MaxValue && (long)Top + Height <= int.MaxValue;
}

/// <summary>Video width share, content aspect and position within the available travel range.</summary>
public sealed record PipLayout(double WidthRatio, double AspectRatio, double HorizontalPosition,
    double VerticalPosition, string? MonitorDevice = null)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(WidthRatio) && WidthRatio is > 0 and <= 1 &&
        double.IsFinite(AspectRatio) && AspectRatio is > 0 and <= 1000 &&
        double.IsFinite(HorizontalPosition) && HorizontalPosition is >= 0 and <= 1 &&
        double.IsFinite(VerticalPosition) && VerticalPosition is >= 0 and <= 1;
}
