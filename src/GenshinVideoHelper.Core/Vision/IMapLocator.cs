namespace GenshinVideoHelper.Core.Vision;

/// <summary>Future video/game minimap input. No image recognition is enabled in the basic app.</summary>
public interface IMapLocator
{
    ValueTask<MapLocation?> LocateAsync(FrameSample frame, CancellationToken token = default);
}

public sealed record FrameSample(ReadOnlyMemory<byte> EncodedImage, TimeSpan Timestamp, FrameSource Source);
public enum FrameSource { Video, Game }
public sealed record MapLocation(string MapId, string? LayerId, double X, double Y, double Confidence);
