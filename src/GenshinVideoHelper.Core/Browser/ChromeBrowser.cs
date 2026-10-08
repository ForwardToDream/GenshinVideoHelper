using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace GenshinVideoHelper.Core.Browser;

public sealed class ChromeBrowser : IDisposable
{
    private readonly string _profileDirectory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CdpClient _client = new();
    private Uri? _endpoint;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<Process> _launchedProcesses = [];
    private bool _closing;

    public ChromeBrowser(string profileDirectory) => _profileDirectory = profileDirectory;

    public async Task<BrowserPage> OpenAsync(string videoUrl, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return await OpenCoreAsync(videoUrl, token);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<BrowserPage> OpenCoreAsync(string videoUrl, CancellationToken token)
    {
        var url = ValidateVideoUrl(videoUrl);
        Directory.CreateDirectory(_profileDirectory);
        if (await TryConnectAsync(token))
        {
            var browserSocket = await GetBrowserSocketAsync(token);
            var reply = await _client.SendAsync(browserSocket, "Target.createTarget", new { url = url.AbsoluteUri }, token);
            return await WaitForPageAsync(reply.GetProperty("targetId").GetString(), url.AbsoluteUri, token);
        }

        var chrome = FindChrome() ?? throw new FileNotFoundException(
            "未找到 Chrome，请先安装桌面版 Google Chrome。");
        var startInfo = new ProcessStartInfo(chrome) { UseShellExecute = false };
        startInfo.ArgumentList.Add($"--user-data-dir={_profileDirectory}");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(url.AbsoluteUri);
        var launched = Process.Start(startInfo);
        if (launched is not null) _launchedProcesses.Add(launched);

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (await TryConnectAsync(startupTimeout.Token)) return await WaitForPageAsync(null, url.AbsoluteUri, startupTimeout.Token);
                await Task.Delay(200, startupTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Chrome 启动超时。请关闭本工具的独立浏览器窗口后重试。");
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
            await Task.Delay(100, token);
        }
        throw new TimeoutException("视频页面打开超时，请在其他视频页面区域刷新。");
    }

    public async Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default)
    {
        var current = await GetPageAsync(page.Id, token)
            ?? throw new InvalidOperationException("视频页面已关闭，请重新开始跟随。");
        if (!IsBilibiliUrl(current.Url) && current.Url is not "" and not "about:blank")
            throw new InvalidOperationException("视频页面已离开 B 站，请重新开始跟随。");
        var result = await _client.SendAsync(new Uri(current.WebSocketDebuggerUrl), "Page.navigate", new { url = identity.Url }, token);
        if (result.TryGetProperty("errorText", out var error)) throw new IOException(error.GetString());
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
        var result = await _client.SendAsync(await GetBrowserSocketAsync(token), "SystemInfo.getProcessInfo", new { }, token);
        var browser = result.GetProperty("processInfo").EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "browser");
        return (int)browser.GetProperty("id").GetDouble();
    }

    private async Task<Uri> GetBrowserSocketAsync(CancellationToken token)
    {
        var version = await _http.GetFromJsonAsync<JsonElement>(new Uri(_endpoint!, "/json/version"), token);
        return new Uri(version.GetProperty("webSocketDebuggerUrl").GetString()!);
    }

    private async Task<bool> TryConnectAsync(CancellationToken token)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(_profileDirectory, "DevToolsActivePort"), token);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535) return false;
            var candidate = new Uri($"http://127.0.0.1:{port}");
            var version = await _http.GetFromJsonAsync<JsonElement>(new Uri(candidate, "/json/version"), token);
            var socket = new Uri(version.GetProperty("webSocketDebuggerUrl").GetString()!);
            if (socket.AbsolutePath != lines[1].Trim()) return false;
            _endpoint = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
                                  InvalidOperationException or UriFormatException or TaskCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
    }

    public static bool IsBilibiliUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) &&
        url.Scheme == Uri.UriSchemeHttps &&
        (url.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
         url.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    public static Uri ValidateVideoUrl(string value)
    {
        value = value.Trim();
        if (value.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
            value = "https://www.bilibili.com/video/" + value;
        if (!IsBilibiliUrl(value))
            throw new ArgumentException("请输入完整的 https://www.bilibili.com 视频链接或 BV 号。");
        return new Uri(value);
    }

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

    // Close only the browser verified against this profile's DevTools session, or the
    // process this instance launched with that exact profile. Never enumerate/kill Chrome by name.
    public async Task CloseAsync()
    {
        _closing = true;
        await _lifecycle.WaitAsync();
        Process? owned = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (File.Exists(Path.Combine(_profileDirectory, "DevToolsActivePort")) && await TryConnectAsync(deadline.Token))
                {
                    owned = Process.GetProcessById(await GetBrowserProcessIdAsync(deadline.Token));
                    _ = owned.Handle; // Keep this process identity even if Chrome exits or the PID gets reused.
                    await _client.SendAsync(await GetBrowserSocketAsync(deadline.Token), "Browser.close", new { }, deadline.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or
                TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
            { Debug.WriteLine($"Chrome graceful close: {ex.Message}"); }

            var targets = _launchedProcesses.Concat(owned is null ? [] : new[] { owned });
            foreach (var target in targets)
            {
                if (target.HasExited) continue;
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await target.WaitForExitAsync(exitDeadline.Token); }
                catch (OperationCanceledException)
                {
                    // A hung, positively identified helper browser is still part of this session.
                    if (!target.HasExited) target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync();
                }
            }
        }
        finally { owned?.Dispose(); _lifecycle.Release(); }
    }
    public void Dispose()
    {
        _http.Dispose();
        foreach (var process in _launchedProcesses) process.Dispose();
    }
}

public sealed class BrowserConnectionException(string message, Exception innerException) : IOException(message, innerException);
