using System.Reflection;
using System.Text.Json;

using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Contracts;

namespace GenshinVideoHelper.Infrastructure.Browser;

public sealed class VideoController : IVideoPlayer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string Script = LoadScript();
    private readonly CdpClient _client = new();

    public async Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command,
        CancellationToken cancellationToken = default)
    {
        var expression = CreateExpression(command);
        var reply = await _client.SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate",
            new { expression, awaitPromise = true, returnByValue = true, userGesture = command.Action != "status" },
            cancellationToken);
        if (reply.TryGetProperty("exceptionDetails", out var details))
        {
            var message = details.TryGetProperty("exception", out var exception) &&
                          exception.TryGetProperty("description", out var description)
                ? description.GetString() : details.GetProperty("text").GetString();
            if (message?.Contains("VIDEO_NOT_READY:") == true)
                throw new VideoNotReadyException("视频尚未加载，请在浏览器中完成登录或等待视频加载。");
            throw new InvalidOperationException(message?.Split('\n')[0] ?? "视频操作失败。");
        }
        if (!reply.GetProperty("result").TryGetProperty("value", out var value))
            throw new InvalidOperationException("未收到视频状态，请稍后重试。");
        return value.Deserialize<VideoState>(JsonOptions)
               ?? throw new InvalidOperationException("无法读取视频状态。");
    }

    public static string CreateExpression(VideoCommand command) =>
        Script.Replace("__COMMAND__", JsonSerializer.Serialize(command, JsonOptions), StringComparison.Ordinal);

    private static string LoadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "GenshinVideoHelper.Infrastructure.Browser.VideoControl.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
