using System.Reflection;
using System.Text.Json;

using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.Infrastructure.Browser;

public sealed class VideoController : IVideoPlayer, IVideoActivitySource, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string Script = LoadScript();
    private readonly CdpClient _client = new(reuseConnections: true);
    private readonly SemaphoreSlim _monitoring = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Uri, (long Connection, string Target)> _watched = new();
    public event Action<string>? MediaActivity;
    public VideoController() => _client.EventReceived += OnEvent;
    private void OnEvent(Uri endpoint, string name, JsonElement data)
    {
        if (name == "Runtime.bindingCalled" && data.TryGetProperty("name", out var binding) &&
            binding.GetString() == "__gvhMediaReady" && _watched.TryGetValue(endpoint, out var watched))
            MediaActivity?.Invoke(watched.Target);
    }
    private async Task MonitorAsync(BrowserPage page, CancellationToken token)
    {
        var endpoint = new Uri(page.WebSocketDebuggerUrl);
        await _monitoring.WaitAsync(token);
        try
        {
            if (_watched.TryGetValue(endpoint, out var watched) && watched.Connection == _client.ConnectionId(endpoint)) return;
            // Emulates page focus without activating any native browser window.
            await _client.SendAsync(endpoint, "Emulation.setFocusEmulationEnabled", new { enabled = true }, token);
            await _client.SendAsync(endpoint, "Runtime.enable", new { }, token);
            await _client.SendAsync(endpoint, "Runtime.addBinding", new { name = "__gvhMediaReady" }, token);
            await _client.SendAsync(endpoint, "Page.addScriptToEvaluateOnNewDocument", new { source = MediaObserver }, token);
            _watched[endpoint] = (_client.ConnectionId(endpoint), page.Id);
            AppLog.Info("Video", $"已挂接页面 {page.Id} 的媒体事件。");
            await _client.SendAsync(endpoint, "Runtime.evaluate", new { expression = MediaObserver }, token);
        }
        finally { _monitoring.Release(); }
    }
    private const string MediaObserver = """
        (() => {
          if (window.__gvhObserving) return;
          window.__gvhObserving = true;
          let queued = false;
          const notify = () => {
            if (queued) return;
            queued = true;
            setTimeout(() => {
              queued = false;
              if (typeof window.__gvhMediaReady === 'function') window.__gvhMediaReady(location.href);
            }, 40);
          };
          for (const name of ['DOMContentLoaded', 'loadedmetadata', 'loadeddata', 'canplay', 'playing',
                              'pause', 'ended', 'emptied', 'durationchange', 'enterpictureinpicture', 'leavepictureinpicture',
                              'seeking', 'seeked', 'ratechange'])
            document.addEventListener(name, notify, true);
          notify();
        })()
        """;

    public void Dispose() { _client.EventReceived -= OnEvent; _client.Dispose(); }

    public async Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command,
        CancellationToken cancellationToken = default)
    {
        await MonitorAsync(page, cancellationToken);
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
            AppLog.Warn("Video", $"页面脚本执行 {command.Action} 失败：{message}");
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
