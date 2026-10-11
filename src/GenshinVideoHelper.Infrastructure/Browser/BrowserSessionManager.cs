using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Infrastructure.Browser;

/// <summary>A saved preference is applied only by OpenAsync. Navigation and login retain their page's session.</summary>
public sealed class BrowserSessionManager : IBrowserSession, IBrowserWarmup, IBrowserWindow, IDisposable
{
    private sealed record Session(BrowserInstallation Installation, IBrowserSession Browser)
    {
        public CancellationTokenSource Lifetime { get; } = new();
    }
    private readonly AppSettings _settings;
    private readonly IBrowserCatalog _catalog;
    private readonly Func<BrowserInstallation, IBrowserSession> _factory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Session> _sessions = [];
    private readonly Dictionary<BrowserKind, Task> _retiring = [];
    private readonly Dictionary<string, BrowserPage> _pages = [];
    private CancellationTokenSource? _openRequest;
    private Session? _active;
    private long _version;
    private bool _closed;
    private Task? _closeTask;

    public BrowserSessionManager(AppSettings settings, IBrowserCatalog catalog, Func<BrowserInstallation, IBrowserSession> factory)
    { _settings = settings; _catalog = catalog; _factory = factory; }

    public BrowserInstallation? ActiveBrowser { get { lock (_sync) return _active?.Installation; } }
    // Raised on the caller's context, before an old browser is retired, so the host can release its overlays.
    public event Action? SessionChanging;
    public event Action? ActiveBrowserChanged;

    public async Task WarmupAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        Session? session;
        CancellationTokenSource? warming = null;
        await _openGate.WaitAsync(linked.Token);
        try
        {
            lock (_sync) { ThrowIfClosed(); session = _active; }
            // Missing installations must not prevent using libraries or episode previews.
            if (session is null && BrowserSelection.Resolve(_settings.BrowserPreference, _catalog.Detect()) is { } installation)
                session = await SelectAsync(installation, linked.Token);
            if (session?.Browser is IBrowserWarmup)
                warming = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, session.Lifetime.Token);
        }
        finally { _openGate.Release(); }
        // The selected adapter serializes its own launch. A different browser can cancel a slow prewarm immediately.
        using (warming)
            if (session?.Browser is IBrowserWarmup warmup) await warmup.WarmupAsync(warming!.Token);
    }

    public async Task<BrowserPage> OpenAsync(string videoUrl, CancellationToken token = default)
    {
        BilibiliUrl.Validate(videoUrl);
        CancellationTokenSource request;
        long version;
        lock (_sync)
        {
            ThrowIfClosed();
            _openRequest?.Cancel();
            _openRequest = request = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            version = ++_version;
        }
        using (request)
        {
            var entered = false;
            try
            {
                await _openGate.WaitAsync(request.Token);
                entered = true;
                var installation = BrowserSelection.Resolve(_settings.BrowserPreference, _catalog.Detect())
                    ?? throw new InvalidOperationException(_settings.BrowserPreference == BrowserPreference.Auto
                        ? "未检测到 Chrome 或 Edge。请安装桌面版浏览器后，在设置中重新检测。"
                        : $"所选 {_settings.BrowserPreference} 未安装或路径已失效，请在设置中重新选择浏览器。");
                var session = await SelectAsync(installation, request.Token);
                var page = await session.Browser.OpenAsync(videoUrl, request.Token);
                lock (_sync)
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (_closed || version != _version || _active != session) throw new OperationCanceledException(request.Token);
                    _pages[page.Id] = page;
                }
                return page;
            }
            finally
            {
                if (entered) _openGate.Release();
                lock (_sync) { if (_openRequest == request) _openRequest = null; }
            }
        }
    }

    private async Task<Session> SelectAsync(BrowserInstallation installation, CancellationToken token)
    {
        Session? old;
        lock (_sync)
        {
            ThrowIfClosed();
            old = _active;
            if (old is not null && old.Installation.Kind == installation.Kind &&
                string.Equals(old.Installation.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return old;
        }
        if (old is not null) SessionChanging?.Invoke();
        lock (_sync)
        {
            ThrowIfClosed();
            token.ThrowIfCancellationRequested();
            _active = null;
            _pages.Clear();
            if (old is not null) _retiring[old.Installation.Kind] = CloseSessionAsync(old);
        }
        ActiveBrowserChanged?.Invoke();
        // A rapid Chrome -> Edge -> Chrome switch must not reopen a profile that is still shutting down.
        Task? previous;
        lock (_sync) _retiring.TryGetValue(installation.Kind, out previous);
        if (previous is not null) await previous.WaitAsync(token);
        Session next;
        lock (_sync)
        {
            ThrowIfClosed();
            token.ThrowIfCancellationRequested();
            next = new(installation, _factory(installation));
            _sessions.Add(next);
            _active = next;
        }
        ActiveBrowserChanged?.Invoke();
        return next;
    }

    private Session Current(BrowserPage? page = null)
    {
        lock (_sync)
        {
            ThrowIfClosed();
            if (_active is null) throw new InvalidOperationException("请先点击“开始跟随”。");
            if (page is not null && (!_pages.TryGetValue(page.Id, out var owned) || owned.WebSocketDebuggerUrl != page.WebSocketDebuggerUrl))
                throw new OperationCanceledException("视频所属的浏览器会话已切换。");
            return _active;
        }
    }

    public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default)
        => Current(page).Browser.NavigateAsync(page, identity, token);

    public async Task<BrowserPage?> GetPageAsync(string targetId, CancellationToken token = default)
    {
        Session session;
        lock (_sync)
        {
            ThrowIfClosed();
            if (_active is null || !_pages.ContainsKey(targetId)) return null;
            session = _active;
        }
        var page = await session.Browser.GetPageAsync(targetId, token);
        lock (_sync) return !_closed && _active == session ? page : null;
    }

    public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default)
        => Current().Browser.GetBrowserProcessIdAsync(token);

    public Task ShowAsync(BrowserPage page, CancellationToken token = default)
        => Current(page).Browser is IBrowserWindow window ? window.ShowAsync(page, token)
            : Task.CompletedTask;

    public Task CloseAsync()
    {
        lock (_sync)
        {
            if (_closeTask is not null) return _closeTask;
            _closed = true;
            ++_version;
            _shutdown.Cancel();
            _openRequest?.Cancel();
            _active = null;
            _pages.Clear();
            // No new sessions can be created after this snapshot. Cleanup does not queue behind OpenAsync.
            return _closeTask = Task.WhenAll(_sessions.Select(CloseSessionAsync).Concat(_retiring.Values).ToArray());
        }
    }

    private readonly Dictionary<Session, Task> _cleanup = [];
    private Task CloseSessionAsync(Session session)
    {
        lock (_sync)
        {
            if (_cleanup.TryGetValue(session, out var task)) return task;
            session.Lifetime.Cancel();
            // Retain a single close/dispose operation even when switch and exit race.
            return _cleanup[session] = Task.Run(async () =>
            {
                try { await session.Browser.CloseAsync(); }
                finally
                {
                    (session.Browser as IDisposable)?.Dispose();
                    session.Lifetime.Dispose();
                    lock (_sync) _sessions.Remove(session);
                }
            });
        }
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);
    public void Dispose() => _ = CloseAsync();
}
