using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace GenshinVideoHelper.Infrastructure.Browser;

/// <summary>One receive loop per endpoint; replies are correlated and events never consume a reply.</summary>
internal sealed class CdpConnections : IDisposable
{
    private readonly ConcurrentDictionary<Uri, Connection> _connections = new();
    private readonly object _sync = new();
    private bool _disposed;
    public event Action<Uri, string, JsonElement>? EventReceived;
    public long ConnectionId(Uri endpoint) => _connections.TryGetValue(endpoint, out var connection) ? connection.Id : 0;

    public async Task<JsonElement> SendAsync(Uri endpoint, string method, object parameters, CancellationToken token)
    {
        Connection connection;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connections.TryGetValue(endpoint, out connection!) || connection.IsClosed)
            {
                connection?.Dispose();
                connection = new(endpoint, (name, data) => EventReceived?.Invoke(endpoint, name, data));
                _connections[endpoint] = connection;
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await connection.SendAsync(method, parameters, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("浏览器响应超时，请确认视频页面仍然打开。"); }
        catch (WebSocketException ex) { throw new IOException("浏览器连接已断开。", ex); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var connection in _connections.Values) connection.Dispose();
            _connections.Clear();
        }
    }

    private sealed class Connection : IDisposable
    {
        private static long _nextConnection;
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Action<string, JsonElement> _event;
        private readonly Task _ready;
        private int _nextId, _closed;
        public long Id { get; } = Interlocked.Increment(ref _nextConnection);
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public Connection(Uri endpoint, Action<string, JsonElement> onEvent)
        { _event = onEvent; _ready = ConnectAsync(endpoint); }
        private async Task ConnectAsync(Uri endpoint)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await _socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                _ = ReceiveAsync();
            }
            catch { Dispose(); throw; }
        }
        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            await _ready.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (IsClosed) throw new IOException("浏览器连接已断开。");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await _send.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (IsClosed) throw new IOException("浏览器连接已断开。");
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
                    // Cancellation after sending abandons this reply without aborting other requests.
                    await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
                }
                finally { _send.Release(); }
                return await completion.Task.WaitAsync(token).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }
        private async Task ReceiveAsync()
        {
            Exception failure = new IOException("浏览器已断开连接。");
            try
            {
                var buffer = new byte[8192];
                while (!_lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult chunk;
                    do
                    {
                        chunk = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                        if (chunk.MessageType == WebSocketMessageType.Close) throw new IOException("浏览器已断开连接。");
                        message.Write(buffer, 0, chunk.Count);
                        if (message.Length > 1024 * 1024) throw new IOException("浏览器响应过大。");
                    } while (!chunk.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray());
                    var root = json.RootElement;
                    if (root.TryGetProperty("id", out var id))
                    {
                        if (!_pending.TryGetValue(id.GetInt32(), out var pending)) continue;
                        if (root.TryGetProperty("error", out var error)) pending.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                        else pending.TrySetResult(root.GetProperty("result").Clone());
                    }
                    else if (root.TryGetProperty("method", out var method) && root.TryGetProperty("params", out var data))
                    {
                        try { _event(method.GetString()!, data.Clone()); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CDP event: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { failure = ex is OperationCanceledException ? new IOException("浏览器连接已关闭。", ex) : ex; }
            finally
            {
                Dispose();
                foreach (var pending in _pending.Values) pending.TrySetException(failure);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            _socket.Abort();
            _socket.Dispose();
            foreach (var pending in _pending.Values) pending.TrySetException(new IOException("浏览器连接已关闭。"));
        }
    }
}
