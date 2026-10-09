using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.Infrastructure.Browser;

public sealed class ChromeBrowser : IBrowserSession, IBrowserWarmup, IDisposable
{
    private readonly string _profileDirectory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CdpClient _client = new(reuseConnections: true);
    private Uri? _endpoint;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<Process> _launchedProcesses = [];
    private bool _closing;
    private readonly CancellationTokenSource _shutdown = new();
    private Uri? _browserSocket;
    private Process? _ownedBrowser;
    private string? _failedEndpoint;
    private long _retryEndpointAfter;
    private Task? _closeTask;

    public ChromeBrowser(string profileDirectory) => _profileDirectory = profileDirectory;

    public async Task WarmupAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _lifecycle.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var started = Stopwatch.GetTimestamp();
            if (!await TryConnectAsync(linked.Token)) await LaunchAsync(linked.Token);
            var pid = await GetBrowserProcessIdAsync(linked.Token);
            AppLog.Info("Chrome", $"预热完成：PID {pid}，{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms。");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<BrowserPage> OpenAsync(string videoUrl, CancellationToken token = default)
    {
        var url = ValidateVideoUrl(videoUrl);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _lifecycle.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            token = linked.Token;
            var started = Stopwatch.GetTimestamp();
            if (!await TryConnectAsync(token)) await LaunchAsync(token);
            var reply = await _client.SendAsync(await GetBrowserSocketAsync(token), "Target.createTarget",
                new { url = url.AbsoluteUri, newWindow = true, background = true, windowState = "minimized" }, token);
            var page = await WaitForPageAsync(reply.GetProperty("targetId").GetString(), url.AbsoluteUri, token);
            AppLog.Info("Chrome", $"已打开页面 {page.Id}：{url.AbsoluteUri}，{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms。");
            return page;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task LaunchAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_profileDirectory);
        var chrome = FindChrome();
        if (chrome is null)
        {
            AppLog.Error("Chrome", "未找到 chrome.exe（已检查 Program Files、Program Files (x86)、LocalAppData）。");
            throw new FileNotFoundException("未找到 Chrome，请先安装桌面版 Google Chrome。");
        }
        var startInfo = new ProcessStartInfo(chrome) { UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden };
        startInfo.ArgumentList.Add($"--user-data-dir={_profileDirectory}");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--no-startup-window");
        startInfo.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
        startInfo.ArgumentList.Add("--disable-background-timer-throttling");
        startInfo.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        startInfo.ArgumentList.Add("--disable-renderer-backgrounding");
        token.ThrowIfCancellationRequested();
        var launched = Process.Start(startInfo);
        if (launched is not null)
        {
            _ = launched.Handle;
            lock (_launchedProcesses) _launchedProcesses.Add(launched);
        }
        AppLog.Info("Chrome", $"启动 {chrome}，PID {launched?.Id}，配置目录 {_profileDirectory}。");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (!await TryConnectAsync(startup.Token)) await Task.Delay(50, startup.Token);
            await GetBrowserProcessIdAsync(startup.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            AppLog.Error("Chrome", "启动后 12 秒内未能连接调试端口。");
            throw new TimeoutException("Chrome 启动超时，请重试开始跟随。");
        }
    }
    private async Task<BrowserPage> WaitForPageAsync(string? targetId, string url, CancellationToken token)
    {
        VideoIdentity.TryParse(url, out var identity);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pages = await GetPagesAsync(token);
            var page = pages.FirstOrDefault(p => targetId is not null ? p.Id == targetId :
                p.Url == url || (identity is not null && VideoIdentity.TryParse(p.Url, out var actual) && actual == identity));
            if (page is not null) return page;
            await Task.Delay(50, token);
        }
        AppLog.Warn("Chrome", $"新页面 {targetId} 未出现在页面列表中：{url}");
        throw new TimeoutException("视频页面打开超时，请重新开始跟随。");
    }

    public async Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default)
    {
        var current = await GetPageAsync(page.Id, token)
            ?? throw new InvalidOperationException("视频页面已关闭，请重新开始跟随。");
        if (!IsBilibiliUrl(current.Url) && current.Url is not "" and not "about:blank")
            throw new InvalidOperationException("视频页面已离开 B 站，请重新开始跟随。");
        var result = await _client.SendAsync(new Uri(current.WebSocketDebuggerUrl), "Page.navigate", new { url = identity.Url }, token);
        if (result.TryGetProperty("errorText", out var error))
        {
            AppLog.Warn("Chrome", $"页面 {page.Id} 导航到 {identity.Url} 失败：{error.GetString()}");
            throw new IOException(error.GetString());
        }
        AppLog.Info("Chrome", $"页面 {page.Id} 导航到 {identity.Url}。");
    }

    public async Task<IReadOnlyList<BrowserPage>> GetPagesAsync(CancellationToken token = default)
        => (await GetAllPagesAsync(token)).Where(page => IsBilibiliUrl(page.Url)).ToArray();

    public async Task<BrowserPage?> GetPageAsync(string targetId, CancellationToken token = default)
        => (await GetAllPagesAsync(token)).FirstOrDefault(page => page.Id == targetId);

    private async Task<IReadOnlyList<BrowserPage>> GetAllPagesAsync(CancellationToken token)
    {
        if (_endpoint is null && !await TryConnectAsync(token))
            throw new InvalidOperationException("请先点击“开始跟随”，启动专用 Chrome 窗口。");
        try
        {
            using var response = await _http.GetAsync(new Uri(_endpoint!, "/json/list"), token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            return json.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "page" &&
                           item.TryGetProperty("webSocketDebuggerUrl", out _))
            .Select(item => new BrowserPage(item.GetProperty("id").GetString()!,
                item.GetProperty("title").GetString() ?? "B 站视频", item.GetProperty("url").GetString()!,
                item.GetProperty("webSocketDebuggerUrl").GetString()!)).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { throw new BrowserConnectionException("浏览器连接已断开或暂未响应，请重新开始跟随。", ex); }
    }

    public async Task<int> GetBrowserProcessIdAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_ownedBrowser is { HasExited: false }) return _ownedBrowser.Id;
        if (_endpoint is null && !await TryConnectAsync(token)) throw new IOException("专用 Chrome 尚未连接。");
        var result = await _client.SendAsync(await GetBrowserSocketAsync(token), "SystemInfo.getProcessInfo", new { }, token);
        var browser = result.GetProperty("processInfo").EnumerateArray().First(item => item.GetProperty("type").GetString() == "browser");
        var owned = Process.GetProcessById((int)browser.GetProperty("id").GetDouble());
        _ = owned.Handle; // Retain the verified process identity, including through exit/PID reuse.
        _ownedBrowser?.Dispose();
        _ownedBrowser = owned;
        AppLog.Info("Chrome", $"已确认专用浏览器进程 PID {owned.Id}。");
        return owned.Id;
    }

    private async Task<Uri> GetBrowserSocketAsync(CancellationToken token)
    {
        if (_browserSocket is not null) return _browserSocket;
        if (!await TryConnectAsync(token)) throw new IOException("专用 Chrome 已断开连接。");
        return _browserSocket!;
    }

    private async Task<bool> TryConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ownedBrowser is { HasExited: false } && _browserSocket is not null) return true;
        string? fingerprint = null;
        try
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(_profileDirectory, "DevToolsActivePort"), token);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535) return false;
            fingerprint = port + lines[1].Trim();
            if (_failedEndpoint == fingerprint && Environment.TickCount64 < _retryEndpointAfter) return false;
            var candidate = new Uri($"http://127.0.0.1:{port}");
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
            probe.CancelAfter(TimeSpan.FromMilliseconds(180));
            var version = await _http.GetFromJsonAsync<JsonElement>(new Uri(candidate, "/json/version"), probe.Token);
            var socket = new Uri(version.GetProperty("webSocketDebuggerUrl").GetString()!);
            if (socket.Host != candidate.Host || socket.Port != port || socket.AbsolutePath != lines[1].Trim()) return false;
            _endpoint = candidate;
            _browserSocket = socket;
            _failedEndpoint = null;
            AppLog.Info("Chrome", $"已连接调试端口 {port}。");
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
                                  InvalidOperationException or UriFormatException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            if (_endpoint is not null) AppLog.Warn("Chrome", $"调试连接已失效：{ex.Message}");
            else AppLog.Debug("Chrome", $"调试端口探测失败：{ex.GetType().Name}: {ex.Message}");
            _endpoint = null;
            _browserSocket = null;
            if (fingerprint is not null) { _failedEndpoint = fingerprint; _retryEndpointAfter = Environment.TickCount64 + 500; }
            return false;
        }
    }
    public static bool IsBilibiliUrl(string? value) => BilibiliUrl.IsBilibili(value);
    public static Uri ValidateVideoUrl(string? value) => BilibiliUrl.Validate(value);

    private static string? FindChrome()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google/Chrome/Application/chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google/Chrome/Application/chrome.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }

    // A retained process handle or a profile-verified DevTools session is required for termination.
    public Task CloseAsync()
    {
        if (_closeTask is not null) return _closeTask;
        _closing = true;
        AppLog.Info("Chrome", "开始关闭专用浏览器。");
        _shutdown.Cancel();
        return _closeTask = Task.Run(CloseCoreAsync);
    }

    private async Task CloseCoreAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2.1));
        var started = Stopwatch.GetTimestamp();
        var entered = false;
        try
        {
            await _lifecycle.WaitAsync(budget.Token);
            entered = true;
            using var graceful = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            graceful.CancelAfter(TimeSpan.FromMilliseconds(600));
            try
            {
                if (await TryConnectAsync(graceful.Token))
                {
                    await GetBrowserProcessIdAsync(graceful.Token);
                    await _client.SendAsync(await GetBrowserSocketAsync(graceful.Token), "Browser.close", new { }, graceful.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or
                TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
            { AppLog.Warn("Chrome", $"优雅关闭未完成：{ex.GetType().Name}: {ex.Message}"); }

            await Task.WhenAll(OwnedProcesses().Where(p => !p.HasExited).Select(p => p.WaitForExitAsync(budget.Token)));
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var process in OwnedProcesses())
            {
                try
                {
                    if (process.HasExited) continue;
                    AppLog.Warn("Chrome", $"PID {process.Id} 未在预算内退出，强制结束进程树。");
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { AppLog.Warn("Chrome", "强制结束失败。", ex); }
            }
            AppLog.Info("Chrome", $"关闭流程结束，{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms。");
            if (entered) _lifecycle.Release();
        }
    }
    private Process[] OwnedProcesses()
    {
        lock (_launchedProcesses)
            return _launchedProcesses.Concat(_ownedBrowser is null ? [] : new[] { _ownedBrowser }).DistinctBy(p => p.Id).ToArray();
    }
    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _ownedBrowser?.Dispose();
        foreach (var process in _launchedProcesses) process.Dispose();
    }
}

public sealed class BrowserConnectionException(string message, Exception innerException) : IOException(message, innerException);
