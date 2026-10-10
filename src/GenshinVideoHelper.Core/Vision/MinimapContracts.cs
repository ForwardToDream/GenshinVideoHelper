using System.Buffers;
using System.Diagnostics;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Vision;

public sealed class VisionSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 100;
    public int SearchIntervalMs { get; set; } = 1000;
    public bool ShowDirection { get; set; } = true;
    public int MarkerSize { get; set; } = 5;
    public void Normalize()
    {
        IntervalMs = Math.Clamp(IntervalMs, 50, 200);
        SearchIntervalMs = Math.Clamp(SearchIntervalMs, 1000, 5000);
        MarkerSize = Math.Clamp(MarkerSize, 3, 9);
    }
}

// All geometry is in source/client physical pixels, never WPF DIP.
public readonly record struct FrameRegion(int X, int Y, int Width, int Height);
public readonly record struct MapDisk(double X, double Y, double Radius)
{
    public FrameRegion CaptureRegion(int width, int height)
    {
        int x = Math.Max(0, (int)Math.Floor(X - Radius * 1.22));
        int y = Math.Max(0, (int)Math.Floor(Y - Radius * 1.22));
        return new(x, y, Math.Max(1, Math.Min(width, (int)Math.Ceiling(X + Radius * 1.22)) - x),
            Math.Max(1, Math.Min(height, (int)Math.Ceiling(Y + Radius * 1.22)) - y));
    }
}

/// <summary>Exclusive pooled pixel ownership. A source's crop is described in full source coordinates.</summary>
public sealed class VisionFrame : IDisposable
{
    private byte[]? _pixels;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VisionFrame));
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public int Stride => Width * Channels;
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public FrameRegion Region { get; }
    public long CapturedAt { get; }
    public string Epoch { get; }
    public bool Paused { get; }
    public double MediaTime { get; }
    public VisionFrame(int width, int height, int channels, int sourceWidth, int sourceHeight,
        FrameRegion region, string epoch, long capturedAt = 0, bool paused = false, double mediaTime = 0)
    {
        if (width <= 0 || height <= 0 || channels is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        _pixels = ArrayPool<byte>.Shared.Rent(checked(width * height * channels));
        Width = width; Height = height; Channels = channels; SourceWidth = sourceWidth; SourceHeight = sourceHeight;
        Region = region; Epoch = epoch; CapturedAt = capturedAt == 0 ? Stopwatch.GetTimestamp() : capturedAt;
        Paused = paused; MediaTime = mediaTime;
    }
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _pixels, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

public sealed record CapturePlan(FrameRegion? Region = null, string? Epoch = null);
public interface IVideoFrameSource
{
    Task<VisionFrame?> CaptureAsync(BrowserPage page, VideoIdentity identity, CapturePlan plan, CancellationToken token);
}
public interface IGameFrameSource : IDisposable
{
    bool IsForeground(nint window);
    ValueTask<VisionFrame?> CaptureAsync(nint window, CapturePlan plan, CancellationToken token);
    void Reset();
}
public sealed record MapMarker(double X, double Y, double? DirectionRadians, uint Color, MapDisk Disk);
public sealed record MapAnalysis(string Status, MapMarker? Marker = null, double Correlation = 0, int Inliers = 0);
public interface IMinimapAnalyzer : IDisposable
{
    CapturePlan VideoPlan { get; }
    CapturePlan GamePlan { get; }
    void Reset();
    MapAnalysis Analyze(VisionFrame video, VisionFrame game, bool direction, int searchIntervalMs);
}
public sealed record VisionUpdate(long Generation, string Status, MapMarker? Marker = null, nint Window = 0,
    long CapturedAt = 0, double CaptureMs = 0, double AnalysisMs = 0, double Correlation = 0, int Inliers = 0);

public static class MapProjection
{
    public static (double X, double Y) Position(double a, double b, double tx, double ty) =>
        (a * 106 - b * 106 + tx, b * 106 + a * 106 + ty);
    public static double Direction(double radians, double a, double b) => Math.Atan2(
        b * Math.Cos(radians) + a * Math.Sin(radians), a * Math.Cos(radians) - b * Math.Sin(radians));
    public static double AngularDistance(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 360));
}
