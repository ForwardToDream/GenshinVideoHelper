using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Browser;

/// <summary>Uses the player's persistent CDP connection, without acquiring the playback command lock.</summary>
public sealed class VideoFrameSource(CdpClient client) : IVideoFrameSource
{
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
        var start = Stopwatch.GetTimestamp();
        var request = JsonSerializer.Serialize(new { bvid = identity.Bvid, part = identity.Part, region = plan.Region, epoch = plan.Epoch }, Json);
        var reply = await client.SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate",
            new { expression = Script.Replace("__REQUEST__", request), returnByValue = true }, token).ConfigureAwait(false);
        if (reply.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("视频帧暂不可读取");
        if (!reply.GetProperty("result").TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var data = value.Deserialize<CaptureReply>(Json)!;
        if (data.Png.Length > 2_000_000) throw new InvalidOperationException("视频区域响应过大");
        using var image = Cv2.ImDecode(Convert.FromBase64String(data.Png), ImreadModes.Color);
        if (image.Empty()) return null;
        var frame = new VisionFrame(image.Width, image.Height, 3, data.Width, data.Height, data.Region, data.Epoch,
            start, data.Paused, data.Time);
        try { Marshal.Copy(image.Data, frame.Pixels, 0, checked(image.Width * image.Height * 3)); return frame; }
        catch { frame.Dispose(); throw; }
    }
    private sealed record CaptureReply(int Width, int Height, FrameRegion Region, string Epoch, bool Paused, double Time, string Png);
}
