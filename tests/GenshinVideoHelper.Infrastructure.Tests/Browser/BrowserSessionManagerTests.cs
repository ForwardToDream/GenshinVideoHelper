using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Browser;

public sealed class BrowserSessionManagerTests
{
    private const string Url = "https://www.bilibili.com/video/BV1MXfEY4EQ2/?p=2";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Catalog : IBrowserCatalog
    {
        public IReadOnlyList<BrowserInstallation> Installed = [new(BrowserKind.Chrome, "chrome.exe", "155"), new(BrowserKind.Edge, "msedge.exe", "155")];
        public IReadOnlyList<BrowserInstallation> Detect() => Installed;
    }
    private sealed class Browser : IBrowserSession, IBrowserWarmup, IBrowserWindow, IDisposable
    {
        public readonly TaskCompletionSource Opened = Signal(), Warming = Signal(), Closing = Signal();
        public Func<CancellationToken, Task> OpenWork = _ => Task.CompletedTask, WarmupWork = _ => Task.CompletedTask;
        public Task CloseWork = Task.CompletedTask;
        public Task<BrowserPage?>? PageWork;
        public BrowserPage Page = new(Guid.NewGuid().ToString(), "fixture", Url, "ws://fixture/" + Guid.NewGuid());
        public int Navigations, Shows, Closes, Disposes;
        public async Task<BrowserPage> OpenAsync(string url, CancellationToken token = default)
        { Opened.TrySetResult(); await OpenWork(token); return Page with { Url = url }; }
        public async Task WarmupAsync(CancellationToken token = default)
        { Warming.TrySetResult(); await WarmupWork(token); }
        public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default)
        { Navigations++; return Task.CompletedTask; }
        public Task ShowAsync(BrowserPage page, CancellationToken token = default) { Shows++; return Task.CompletedTask; }
        public Task<BrowserPage?> GetPageAsync(string id, CancellationToken token = default) => PageWork ?? Task.FromResult<BrowserPage?>(Page);
        public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => Task.FromResult(42);
        public async Task CloseAsync() { Closes++; Closing.TrySetResult(); await CloseWork; }
        public void Dispose() => Disposes++;
    }

    [Fact]
    public async Task Preference_IsDeferredUntilOpen_WhileNavigationAndLoginRetainCurrentSession()
    {
        var settings = new AppSettings(); var catalog = new Catalog();
        var chrome = new Browser(); var edge = new Browser(); var created = new List<BrowserKind>();
        using var manager = new BrowserSessionManager(settings, catalog, b => { created.Add(b.Kind); return b.Kind == BrowserKind.Chrome ? chrome : edge; });
        var switches = 0; manager.SessionChanging += () => switches++;
        await manager.WarmupAsync(Token);
        Assert.Equal(BrowserKind.Chrome, manager.ActiveBrowser!.Kind);
        var page = await manager.OpenAsync(Url, Token);
        settings.BrowserPreference = BrowserPreference.Edge;
        await manager.WarmupAsync(Token);
        await manager.NavigateAsync(page, new("BV1MXfEY4EQ2", 3), Token);
        await manager.ShowAsync(page, Token);
        Assert.Equal(1, chrome.Navigations); Assert.Equal(1, chrome.Shows);
        Assert.Equal(new[] { BrowserKind.Chrome }, created); Assert.Equal(0, switches);
        await manager.OpenAsync(Url, Token);
        Assert.Equal(BrowserKind.Edge, manager.ActiveBrowser!.Kind); Assert.Equal(1, switches);
        Assert.Null(await manager.GetPageAsync(page.Id, Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.NavigateAsync(page, new("BV1MXfEY4EQ2", 3), Token));
        await manager.CloseAsync();
        Assert.Equal(1, chrome.Closes); Assert.Equal(1, edge.Closes);
        Assert.Equal(1, chrome.Disposes); Assert.Equal(1, edge.Disposes);
    }

    [Fact]
    public async Task MissingInstallations_DoNotLaunch_AndManualFailureNeverFallsBack()
    {
        var catalog = new Catalog { Installed = [] }; var settings = new AppSettings(); var calls = 0;
        using var manager = new BrowserSessionManager(settings, catalog, _ => { calls++; return new Browser(); });
        await manager.WarmupAsync(Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync(Url, Token));
        Assert.Contains("Chrome 或 Edge", error.Message); Assert.Equal(0, calls);
        catalog.Installed = [new(BrowserKind.Chrome, "chrome.exe", "155")];
        await manager.OpenAsync(Url, Token);
        settings.BrowserPreference = BrowserPreference.Edge;
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync(Url, Token));
        Assert.Contains("Edge", error.Message); Assert.Equal(1, calls);
        Assert.Equal(BrowserKind.Chrome, manager.ActiveBrowser!.Kind);
        await manager.CloseAsync();
    }

    [Fact]
    public async Task NewOpen_CancelsNonCooperativeOlderResult()
    {
        var release = Signal(); var old = new Browser { OpenWork = _ => release.Task }; var next = new Browser();
        var settings = new AppSettings();
        using var manager = new BrowserSessionManager(settings, new Catalog(), b => b.Kind == BrowserKind.Chrome ? old : next);
        var first = manager.OpenAsync(Url, Token); await old.Opened.Task;
        settings.BrowserPreference = BrowserPreference.Edge;
        var second = manager.OpenAsync(Url, Token);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(next.Page.Id, (await second).Id);
        Assert.Null(await manager.GetPageAsync(old.Page.Id, Token));
        await manager.CloseAsync();
    }

    [Fact]
    public async Task LatePageResponse_IsDiscardedAfterSwitch()
    {
        var release = new TaskCompletionSource<BrowserPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Browser { PageWork = release.Task }; var settings = new AppSettings();
        using var manager = new BrowserSessionManager(settings, new Catalog(), b => b.Kind == BrowserKind.Chrome ? old : new Browser());
        var page = await manager.OpenAsync(Url, Token);
        var pending = manager.GetPageAsync(page.Id, Token);
        settings.BrowserPreference = BrowserPreference.Edge; await manager.OpenAsync(Url, Token);
        release.SetResult(old.Page);
        Assert.Null(await pending);
        await manager.CloseAsync();
    }

    [Fact]
    public async Task Cleanup_OverlapsNewBrowser_ButProfileReuseWaitsForPreviousExit()
    {
        var release = Signal(); var old = new Browser { CloseWork = release.Task }; var edge = new Browser(); var reopened = new Browser();
        var settings = new AppSettings(); var chromeCreations = 0;
        using var manager = new BrowserSessionManager(settings, new Catalog(), b => b.Kind == BrowserKind.Edge ? edge : ++chromeCreations == 1 ? old : reopened);
        await manager.OpenAsync(Url, Token);
        settings.BrowserPreference = BrowserPreference.Edge;
        await manager.OpenAsync(Url, Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        await old.Closing.Task;
        Assert.False(release.Task.IsCompleted);
        settings.BrowserPreference = BrowserPreference.Chrome;
        var pending = manager.OpenAsync(Url, Token);
        Assert.False(pending.IsCompleted); Assert.Equal(1, chromeCreations);
        release.SetResult();
        Assert.Equal(reopened.Page.Id, (await pending).Id);
        await manager.CloseAsync();
        Assert.Equal(1, old.Disposes); Assert.Equal(1, edge.Disposes); Assert.Equal(1, reopened.Disposes);
    }

    [Fact]
    public async Task SwitchingDuringPrewarm_CancelsOldLaunchWithoutWaitingForIt()
    {
        var chrome = new Browser { WarmupWork = token => Task.Delay(Timeout.Infinite, token) };
        var edge = new Browser(); var settings = new AppSettings();
        using var manager = new BrowserSessionManager(settings, new Catalog(), b => b.Kind == BrowserKind.Chrome ? chrome : edge);
        var warming = manager.WarmupAsync(Token); await chrome.Warming.Task;
        settings.BrowserPreference = BrowserPreference.Edge;
        var page = await manager.OpenAsync(Url, Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Equal(edge.Page.Id, page.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        await manager.CloseAsync();
        Assert.Equal(1, chrome.Closes); Assert.Equal(1, chrome.Disposes);
    }

    [Fact]
    public async Task ExitDuringWarmup_CancelsLaunchAndClosesExactlyOnce()
    {
        var browser = new Browser { WarmupWork = token => Task.Delay(Timeout.Infinite, token) };
        using var manager = new BrowserSessionManager(new(), new Catalog(), _ => browser);
        var warming = manager.WarmupAsync(Token); await browser.Warming.Task;
        await manager.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        await manager.CloseAsync();
        Assert.Equal(1, browser.Closes); Assert.Equal(1, browser.Disposes); Assert.Null(manager.ActiveBrowser);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.OpenAsync(Url, Token));
    }

    [Fact]
    public async Task ExitDuringSwitch_CleansActiveAndRetiringSessions()
    {
        var release = Signal(); var old = new Browser { CloseWork = release.Task };
        var edge = new Browser { OpenWork = token => Task.Delay(Timeout.Infinite, token) }; var settings = new AppSettings();
        using var manager = new BrowserSessionManager(settings, new Catalog(), b => b.Kind == BrowserKind.Chrome ? old : edge);
        await manager.OpenAsync(Url, Token);
        settings.BrowserPreference = BrowserPreference.Edge;
        var opening = manager.OpenAsync(Url, Token); await edge.Opened.Task;
        var closing = manager.CloseAsync(); release.SetResult();
        await closing;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(1, old.Closes); Assert.Equal(1, edge.Closes);
        Assert.Equal(1, old.Disposes); Assert.Equal(1, edge.Disposes);
    }

    [Fact]
    public async Task LaunchFailure_DoesNotSelectAnotherBrowser()
    {
        var browser = new Browser { OpenWork = _ => Task.FromException(new IOException("failed")) }; var calls = 0;
        using var manager = new BrowserSessionManager(new(), new Catalog(), _ => { calls++; return browser; });
        await Assert.ThrowsAsync<IOException>(() => manager.OpenAsync(Url, Token));
        await Assert.ThrowsAsync<IOException>(() => manager.OpenAsync(Url, Token));
        Assert.Equal(1, calls); Assert.Equal(BrowserKind.Chrome, manager.ActiveBrowser!.Kind);
        await manager.CloseAsync();
    }
}
