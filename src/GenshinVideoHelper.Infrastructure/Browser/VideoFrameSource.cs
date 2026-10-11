using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;

namespace GenshinVideoHelper.Infrastructure.Browser;

/// <summary>Uses the player's persistent CDP connection, without acquiring the playback command lock.</summary>
public sealed class VideoFrameSource(CdpClient client) : IVideoFrameSource
{
    private sealed record CachedFrame(string Endpoint, string Key, byte[] Pixels);
    private CachedFrame? _cached;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Script = ReadScript();
    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "GenshinVideoHelper.Infrastructure.Browser.VideoFrameCapture.js")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public async Task<VisionFrame?> CaptureAsync(BrowserPage page, VideoIdentity identity, CapturePlan plan, CancellationToken token)
    {
        var cached = _cached;
        var request = JsonSerializer.Serialize(new { bvid = identity.Bvid, part = identity.Part, region = plan.Region, epoch = plan.Epoch,
            frameKey = cached?.Endpoint == page.WebSocketDebuggerUrl ? cached.Key : null }, Json);
        var reply = await client.SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate",
            new { expression = Script.Replace("__REQUEST__", request), returnByValue = true }, token).ConfigureAwait(false);
        if (reply.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("视频帧暂不可读取");
        if (!reply.GetProperty("result").TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null)
        { _cached = null; return null; }
        var data = value.Deserialize<CaptureReply>(Json)!;
        var region = data.Region;
        if (region.X < 0 || region.Y < 0 || region.Width < 1 || region.Height < 1 ||
            (long)region.X+region.Width > data.Width/3 || (long)region.Y+region.Height > data.Height/2 ||
            (long)region.Width*region.Height > 3_000_000 || data.Pixels.Length > 16_000_000)
            throw new InvalidOperationException("视频区域响应无效或过大");
        int length = checked(region.Width*region.Height*4);
        byte[] pixels;
        if (data.Pixels.Length == 0)
        {
            if (cached?.Endpoint != page.WebSocketDebuggerUrl || cached.Key != data.FrameKey || cached.Pixels.Length != length)
            { _cached = null; return null; }
            pixels = cached.Pixels;
        }
        else
        {
            pixels = Convert.FromBase64String(data.Pixels);
            if (pixels.Length != length) throw new InvalidOperationException("视频像素长度不正确");
            // Canvas returns RGBA; the game and OpenCV adapters consume BGRA. No image codec or resizing.
            for (int i = 0; i < length; i += 4) (pixels[i], pixels[i+2]) = (pixels[i+2], pixels[i]);
            _cached = new(page.WebSocketDebuggerUrl, data.FrameKey, pixels);
        }
        var frame = new VisionFrame(region.Width, region.Height, 4, data.Width, data.Height, region, data.Epoch,
            Stopwatch.GetTimestamp(), data.Paused, data.Time, data.FrameKey);
        try { pixels.CopyTo(frame.Pixels, 0); return frame; }
        catch { frame.Dispose(); throw; }
    }
    private sealed record CaptureReply(int Width, int Height, FrameRegion Region, string Epoch, bool Paused, double Time, string FrameKey, string Pixels);
}
