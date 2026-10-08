using System.Net.WebSockets;
using System.Text.Json;

namespace GenshinVideoHelper.Core.Browser;

/// <summary>A bounded, single-request CDP connection. Events may precede the reply.</summary>
public sealed class CdpClient
{
    public async Task<JsonElement> SendAsync(Uri endpoint, string method, object parameters,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, deadline.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = parameters });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult chunk;
                do
                {
                    chunk = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                    if (chunk.MessageType == WebSocketMessageType.Close)
                        throw new IOException("浏览器已断开连接，请重新打开视频。");
                    message.Write(buffer, 0, chunk.Count);
                    if (message.Length > 1024 * 1024)
                        throw new IOException("浏览器响应过大，已停止本次操作。");
                } while (!chunk.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetProperty("message").GetString());
                return root.GetProperty("result").Clone();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("浏览器响应超时，请确认视频页面仍然打开。");
        }
        catch (WebSocketException ex)
        {
            throw new IOException("无法连接视频页面，请刷新连接。", ex);
        }
    }
}
