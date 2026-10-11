namespace GenshinVideoHelper.Core.Vision;

/// <summary>Default game HUD and guide search bounds in source/client physical pixels.</summary>
public static class MinimapGeometry
{
    public const double ReferenceHeight = 1080;
    public const double CropMargin = 1.12;

    public static MapDisk GameDisk(int sourceHeight)
    {
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        double scale = sourceHeight / ReferenceHeight;
        return new(168 * scale, 125 * scale, 106 * scale);
    }

    public static FrameRegion GameCaptureRegion(int sourceWidth, int sourceHeight) =>
        GameDisk(sourceHeight).CaptureRegion(sourceWidth, sourceHeight);

    public static FrameRegion GuideSearchRegion(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        return new(0, 0, Math.Max(1, sourceWidth / 3), Math.Max(1, sourceHeight / 2));
    }
}
