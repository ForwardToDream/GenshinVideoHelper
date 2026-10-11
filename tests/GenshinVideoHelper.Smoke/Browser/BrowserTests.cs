using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class BrowserTests
{
    public static async Task BrowserTestAsync(string root, bool headed)
    {
        await using var session = await ChromiumFixture.StartAsync(root, headed);
        using var controller = new VideoController();
        using (var shared = new CdpClient(reuseConnections: true))
        {
            var endpoint = new Uri(session.Page.WebSocketDebuggerUrl);
            var delayed = shared.SendAsync(endpoint, "Runtime.evaluate", new { expression = "new Promise(r=>setTimeout(()=>r(17),200))", awaitPromise = true, returnByValue = true });
            var immediate = shared.SendAsync(endpoint, "Runtime.evaluate", new { expression = "29", returnByValue = true });
            Check((await immediate).GetProperty("result").GetProperty("value").GetInt32() == 29 &&
                  (await delayed).GetProperty("result").GetProperty("value").GetInt32() == 17, "Concurrent CDP replies retain request identity");
            var connection = shared.ConnectionId(endpoint);
            using var cancel = new CancellationTokenSource(40);
            var canceled = false;
            try { await shared.SendAsync(endpoint, "Runtime.evaluate", new { expression = "new Promise(r=>setTimeout(()=>r(99),200))", awaitPromise = true }, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            var next = await shared.SendAsync(endpoint, "Runtime.evaluate", new { expression = "31", returnByValue = true });
            Check(canceled && next.GetProperty("result").GetProperty("value").GetInt32() == 31 && shared.ConnectionId(endpoint) == connection,
                "Canceling one CDP request preserves the connection and other replies");
        }
        var mediaEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.MediaActivity += target => { if (target == session.Page.Id) mediaEvent.TrySetResult(); };
        VideoState? state = null;
        for (var i = 0; i < 40; i++)
        {
            try { state = await controller.ExecuteAsync(session.Page, new("status")); break; }
            catch (InvalidOperationException) { await Task.Delay(200); }
        }
        await mediaEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(state is { Paused: true, Duration: > 19 }, "Metadata and finite duration");
        Check(state!.VideoWidth == 640 && state.VideoHeight == 360, "Select main video instead of small secondary video");
        state = await controller.ExecuteAsync(session.Page, new("toggle"));
        Check(!state.Paused, "Play");
        state = await controller.ExecuteAsync(session.Page, new("toggle"));
        Check(state.Paused, "Pause");
        state = await controller.ExecuteAsync(session.Page, new("ensurePlay"));
        Check(!state.Paused, "Ensure play starts playback");
        state = await controller.ExecuteAsync(session.Page, new("ensurePlay"));
        Check(!state.Paused, "Repeated ensure play does not pause");
        await controller.ExecuteAsync(session.Page, new("toggle"));
        state = await controller.ExecuteAsync(session.Page, new("seek", 10, Absolute: true));
        Check(Math.Abs(state.CurrentTime - 10) < 0.2, "Seek absolute");
        state = await controller.ExecuteAsync(session.Page, new("seek", -5));
        Check(Math.Abs(state.CurrentTime - 5) < 0.2, "Seek backward five seconds");
        state = await controller.ExecuteAsync(session.Page, new("seek", -100));
        Check(state.CurrentTime < 0.2, "Clamp seek at start");
        state = await controller.ExecuteAsync(session.Page, new("seek", 100));
        Check(state.CurrentTime <= state.Duration && state.CurrentTime > 19, "Clamp seek at end");
        state = await controller.ExecuteAsync(session.Page, new("rate", 1.5));
        Check(Math.Abs(state.PlaybackRate - 1.5) < 0.01, "Playback rate");
        state = await controller.ExecuteAsync(session.Page, new("mute"));
        Check(!state.Muted, "Mute toggle");
        await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate",
            new { expression = "document.querySelectorAll('video').forEach(v => v.remove())" });
        var missingVideoReported = false;
        try { await controller.ExecuteAsync(session.Page, new("toggle")); }
        catch (InvalidOperationException ex) { missingVideoReported = ex.Message.Contains("视频尚未加载"); }
        Check(missingVideoReported, "Report missing video");
        using var apiFallback = new BilibiliEpisodeService(new FixtureHandler("{\"code\":0,\"data\":" + EpisodeFixture + "}"));
        var fallbackInfo = await apiFallback.ReadAsync(session.Page with { Url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3" });
        Check(fallbackInfo.Episodes.Count == 4, "Missing page metadata automatically falls back to API");
        await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate", new
        { expression = "window.__INITIAL_STATE__={videoData:" + EpisodeFixture + "}" });
        using var episodeReader = new BilibiliEpisodeService(new FixtureHandler("{\"code\":-404}"));
        var info = await episodeReader.ReadAsync(session.Page with { Url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3" });
        Check(info.Episodes.Count == 4 && info.CurrentPart == 3, "Page episode metadata works before any video is present and does not require API");
        Console.WriteLine($"Actual {SmokeBrowser.Kind}/CDP: media selection, play/pause, seek boundaries, rate, mute and missing video passed.");
        await session.CloseTabAsync();
        var closedReported = false;
        try { await controller.ExecuteAsync(session.Page, new("status")); }
        catch (IOException) { closedReported = true; }
        Check(closedReported, "Report closed tab");
        Console.WriteLine("Closed-tab recovery error passed.");
        await TestAccountProbeAsync(root);
        await TestSingleVideoPageAsync(root);
    }

    private static async Task TestSingleVideoPageAsync(string root)
    {
        await using var fixture = await ChromiumFixture.StartAsync(root, headed: false);
        using var cdp = new CdpClient(reuseConnections: true);
        var browser = SmokeBrowser.Create(fixture.ProfileDirectory);
        using var manager = new BrowserSessionManager(new AppSettings { BrowserPreference = SmokeBrowser.Preference },
            new BrowserCatalog(), _ => browser);
        Exception? interceptionError = null;
        var document = Convert.ToBase64String(Encoding.UTF8.GetBytes("<!doctype html><title>Single video fixture</title>"));
        cdp.EventReceived += (endpoint, name, data) =>
        {
            if (name == "Fetch.requestPaused") _ = FulfillAsync(endpoint, data.GetProperty("requestId").GetString()!);
        };
        async Task FulfillAsync(Uri endpoint, string id)
        {
            try
            {
                await cdp.SendAsync(endpoint, "Fetch.fulfillRequest", new
                {
                    requestId = id, responseCode = 200,
                    responseHeaders = new[] { new { name = "Content-Type", value = "text/html" } }, body = document
                });
            }
            catch (Exception ex) { interceptionError = ex; }
        }
        // Pause new pages before they make network requests; all navigation uses local responses.
        async Task PauseNewPagesAsync() => _ = await cdp.SendAsync(fixture.BrowserSocket, "Target.setAutoAttach", new
            { autoAttach = true, waitForDebuggerOnStart = true, flatten = true });
        await PauseNewPagesAsync();
        async Task AttachAsync(BrowserPage page, string url)
        {
            var endpoint = new Uri(page.WebSocketDebuggerUrl);
            await cdp.SendAsync(endpoint, "Fetch.enable", new { patterns = new[] { new { urlPattern = "*", resourceType = "Document" } } });
            await cdp.SendAsync(fixture.BrowserSocket, "Target.setAutoAttach", new
                { autoAttach = false, waitForDebuggerOnStart = false, flatten = true });
            await cdp.SendAsync(endpoint, "Runtime.runIfWaitingForDebugger", new { });
            await cdp.SendAsync(endpoint, "Page.navigate", new { url });
        }
        async Task ReadyAsync(string id, VideoIdentity identity)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (interceptionError is not null) throw interceptionError;
                var page = await browser.GetPageAsync(id);
                if (page is not null && VideoIdentity.TryParse(page.Url, out var actual) && actual == identity)
                {
                    var result = await cdp.SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate",
                        new { expression = "document.title === 'Single video fixture'", returnByValue = true });
                    if (result.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True) return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("Single-video fixture did not finish navigation.");
        }
        try
        {
            var firstIdentity = new VideoIdentity("BV1MXfEY4EQ2", 2);
            var first = await manager.OpenAsync(firstIdentity.Url);
            await AttachAsync(first, firstIdentity.Url);
            await ReadyAsync(first.Id, firstIdentity);
            await cdp.SendAsync(new Uri(first.WebSocketDebuggerUrl), "Runtime.evaluate", new { expression = "window.followSentinel = 17" });
            for (var i = 0; i < 3; i++)
                Check((await manager.OpenAsync(firstIdentity.Url)).Id == first.Id, "Repeated follow reuses the video target");
            var sentinel = await cdp.SendAsync(new Uri(first.WebSocketDebuggerUrl), "Runtime.evaluate",
                new { expression = "window.followSentinel", returnByValue = true });
            Check(sentinel.GetProperty("result").GetProperty("value").GetInt32() == 17, "Following the same video does not reload it");
            var selected = new VideoIdentity("BV1hjgG6jEa6", 3);
            var requests = Enumerable.Range(0, 12).Select(i => manager.OpenAsync(i == 11 ? selected.Url : firstIdentity.Url)).ToArray();
            foreach (var request in requests)
            {
                try { Check((await request).Id == first.Id, "Rapid follow requests retain the same video target"); }
                catch (OperationCanceledException) { }
            }
            Check((await requests[^1]).Id == first.Id, "The latest follow request wins");
            await ReadyAsync(first.Id, selected);
            // A previous version could leave extra video windows in this dedicated profile.
            await PauseNewPagesAsync();
            var legacy = await cdp.SendAsync(fixture.BrowserSocket, "Target.createTarget", new { url = firstIdentity.Url });
            var extraId = legacy.GetProperty("targetId").GetString()!;
            var extra = await browser.GetPageAsync(extraId) ?? throw new InvalidOperationException("Extra fixture page was not created.");
            await AttachAsync(extra, firstIdentity.Url);
            await ReadyAsync(extraId, firstIdentity);
            await manager.OpenAsync(selected.Url);
            var targets = await cdp.SendAsync(fixture.BrowserSocket, "Target.getTargets", new { });
            Check(targets.GetProperty("targetInfos").EnumerateArray().Count(t => t.GetProperty("type").GetString() == "page" &&
                VideoIdentity.TryParse(t.GetProperty("url").GetString(), out _)) == 1, "Only one guide video remains, including after old duplicate windows");
            Check(targets.GetProperty("targetInfos").EnumerateArray().Any(t => t.GetProperty("targetId").GetString() == fixture.Page.Id),
                "Unrelated pages are preserved");
            await cdp.SendAsync(fixture.BrowserSocket, "Target.closeTarget", new { targetId = first.Id });
            await PauseNewPagesAsync();
            var reopened = await manager.OpenAsync(selected.Url);
            Check(reopened.Id != first.Id && await manager.GetPageAsync(first.Id) is null, "A manually closed video is replaced without retaining its old session page");
            await AttachAsync(reopened, selected.Url);
            await ReadyAsync(reopened.Id, selected);
            Console.WriteLine($"{SmokeBrowser.Kind}: repeated follow, rapid requests, video replacement, old duplicate cleanup and closed-page recovery passed.");
        }
        finally { await manager.CloseAsync(); }
    }

    private static async Task TestAccountProbeAsync(string root)
    {
        await using var fixture = await ChromiumFixture.StartAsync(root, headed: false);
        using var cdp = new CdpClient(reuseConnections: true);
        using var controller = new VideoController();
        var endpoint = new Uri(fixture.Page.WebSocketDebuggerUrl);
        var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cdp.EventReceived += (sender, name, data) =>
        {
            if (name != "Fetch.requestPaused") return;
            _ = RespondAsync(data.GetProperty("requestId").GetString()!);
        };
        async Task RespondAsync(string id)
        {
            try
            {
                await cdp.SendAsync(endpoint, "Fetch.fulfillRequest", new
                {
                    requestId = id, responseCode = 200,
                    responseHeaders = new[] { new { name = "Content-Type", value = "text/html" } },
                    body = Convert.ToBase64String(Encoding.UTF8.GetBytes("<!doctype html><title>Account fixture</title>"))
                });
                served.TrySetResult();
            }
            catch (Exception ex) { served.TrySetException(ex); }
        }
        await cdp.SendAsync(endpoint, "Fetch.enable", new { patterns = new[] { new { urlPattern = "*", resourceType = "Document" } } });
        const string url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3";
        await cdp.SendAsync(endpoint, "Page.navigate", new { url });
        await served.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var page = fixture.Page with { Url = url };
        for (var i = 0; i < 30; i++)
        {
            var ready = await cdp.SendAsync(endpoint, "Runtime.evaluate", new { expression = "location.href === '" + url + "' && document.readyState !== 'loading'", returnByValue = true });
            if (ready.GetProperty("result").GetProperty("value").GetBoolean()) break;
            await Task.Delay(50);
        }
        await cdp.SendAsync(endpoint, "Fetch.disable", new { });
        async Task Script(string expression) => _ = await cdp.SendAsync(endpoint, "Runtime.evaluate", new { expression, returnByValue = true });
        await Script("window.fetch=async()=>({ok:true,json:async()=>({code:-101,data:{isLogin:false}})})");
        var state = await controller.ReadAccountAsync(page);
        Check(state.LoggedIn == false && state.Url == url && state.VideoHeight == 0, "Anonymous account detection works before video loads");
        await Script("window.fetch=async()=>({ok:true,json:async()=>({code:0,data:{isLogin:true,uname:'not returned'}})})");
        Check((await controller.ReadAccountAsync(page)).LoggedIn == true, "Authenticated account detected in current page session");
        await Script("window.fetch=async()=>({ok:true,json:async()=>({code:-412})})");
        Check((await controller.ReadAccountAsync(page)).LoggedIn is null, "Site failure remains unknown, never anonymous");
        await Script("window.fetch=async()=>{throw new Error('offline')}");
        Check((await controller.ReadAccountAsync(page)).LoggedIn is null, "Network failure does not cause a login prompt");
        await Script("window.player={requestQuality:async q=>{window.qualityRequest=q;throw new Error('Permission denied')}}");
        var rejected = false;
        try { await controller.RequestHighQualityAsync(page); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Quality request rejection is awaited and reported, not claimed successful");
        await Script("window.player={requestQuality:async q=>{window.qualityRequest=q}};");
        await controller.RequestHighQualityAsync(page);
        var requested = await cdp.SendAsync(endpoint, "Runtime.evaluate", new { expression = "window.qualityRequest", returnByValue = true });
        Check(requested.GetProperty("result").GetProperty("value").GetInt32() == 80 && (await controller.ReadAccountAsync(page)).VideoHeight == 0,
            "1080p requested without inventing decoded resolution");
        await Script("window.fetch=(_,options)=>new Promise((resolve,reject)=>options.signal.addEventListener('abort',()=>reject(new Error('aborted'))))");
        var started = Stopwatch.GetTimestamp();
        Check((await controller.ReadAccountAsync(page)).LoggedIn is null && Stopwatch.GetElapsedTime(started).TotalSeconds < 3.5, "Account HTTP request has a short bounded timeout");
        Console.WriteLine($"{SmokeBrowser.Kind} account probe: anonymous, logged-in, failures, timeout and denied quality passed.");
    }

    public static async Task BilibiliTestAsync(string root, bool headed = false, nint helperWindow = default)
    {
        await using var session = await ChromiumFixture.StartAsync(root, headed);
        using var browser = SmokeBrowser.Create(session.ProfileDirectory);
        const string example = "https://www.bilibili.com/video/BV1hjgG6jEa6";
        await browser.OpenAsync(example);
        using var controller = new VideoController();
        string? lastError = null;
        BrowserPage? page = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(500);
            page = (await browser.GetPagesAsync()).FirstOrDefault(item => item.Url.Contains("BV1hjgG6jEa6"));
            if (page is null) continue;
            try
            {
                var state = await controller.ExecuteAsync(page, new("status"));
                Console.WriteLine($"Example video: {state.Title}, duration={state.Duration}, size={state.VideoWidth}x{state.VideoHeight}");
                if (state.Paused) state = await controller.ExecuteAsync(page, new("toggle"));
                Check(!state.Paused, "Example plays");
                state = await controller.ExecuteAsync(page, new("toggle"));
                Check(state.Paused, "Example pauses");
                var original = state.CurrentTime;
                state = await controller.ExecuteAsync(page, new("seek", 5));
                Check(state.CurrentTime >= original + 4.5, "Example seeks five seconds");
                await controller.ExecuteAsync(page, new("seek", original, Absolute: true));
                var metadata = await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
                {
                    expression = "JSON.stringify({part:window.__INITIAL_STATE__?.videoData?.pages?.[0],videos:[...document.querySelectorAll('video')].map(v=>({duration:v.duration,ready:v.readyState,width:v.videoWidth,height:v.videoHeight}))})",
                    returnByValue = true
                });
                Console.WriteLine("Page metadata: " + metadata.GetProperty("result").GetProperty("value").GetString());
                if (headed)
                {
                    state = await controller.ExecuteAsync(page, new("pip"));
                    Check(state.PictureInPicture, "Bilibili enters native PiP");
                    await PipWindowService.PlaceAsync(await browser.GetBrowserProcessIdAsync(), helperWindow,
                        420, 20, (double)state.VideoWidth / state.VideoHeight);
                    state = await controller.ExecuteAsync(page, new("toggle"));
                    Check(!state.Paused, "Bilibili PiP plays");
                    state = await controller.ExecuteAsync(page, new("toggle"));
                    Check(state.Paused, "Bilibili PiP pauses");
                    state = await controller.ExecuteAsync(page, new("pip"));
                    Check(!state.PictureInPicture, "Bilibili exits native PiP");
                    Console.WriteLine("Bilibili example native PiP, bottom-left positioning and playback control passed.");
                }
                Console.WriteLine("Bilibili example: browser reconnect, page selection, metadata, play/pause and seek passed.");
                return;
            }
            catch (InvalidOperationException ex) { lastError = ex.Message; }
        }
        if (page is not null)
        {
            var diagnostics = await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "JSON.stringify({title:document.title,url:location.href,videos:document.querySelectorAll('video').length,message:document.body.innerText.slice(0,200)})",
                returnByValue = true
            });
            Console.WriteLine(diagnostics.GetProperty("result").GetProperty("value").GetString());
        }
        throw new InvalidOperationException("Example not verified: " + (lastError ?? "No Bilibili page connected."));
    }

}
