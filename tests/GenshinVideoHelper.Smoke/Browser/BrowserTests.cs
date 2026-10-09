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
        await using var session = await ChromeFixture.StartAsync(root, headed);
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
        Console.WriteLine("Actual Chrome/CDP: media selection, play/pause, seek boundaries, rate, mute and missing video passed.");
        await session.CloseTabAsync();
        var closedReported = false;
        try { await controller.ExecuteAsync(session.Page, new("status")); }
        catch (IOException) { closedReported = true; }
        Check(closedReported, "Report closed tab");
        Console.WriteLine("Closed-tab recovery error passed.");
    }

    public static async Task BilibiliTestAsync(string root, bool headed = false, nint helperWindow = default)
    {
        await using var session = await ChromeFixture.StartAsync(root, headed);
        using var browser = new ChromeBrowser(session.ProfileDirectory);
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
