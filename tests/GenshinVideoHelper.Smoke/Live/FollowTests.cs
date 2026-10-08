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

internal static class FollowTests
{
    public static void FollowUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => FollowUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    public static async Task FollowUiTestAsync(string root)
    {
        await using var session = await ChromeFixture.StartAsync(root, headed: true);
        var settings = new AppSettings { SelectedVideoLibraryId = null, VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=333.788.videopod.episodes&p=3" };
        var window = new GenshinVideoHelper.App.MainWindow(new AppServices(settings, session.ProfileDirectory))
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        using var browser = new ChromeBrowser(session.ProfileDirectory);
        var controller = new VideoController();
        try
        {
            await WaitForAsync(() => window.Services.Preview.Current.Info is { Bvid: "BV1hjgG6jEa6", CurrentPart: 3 }, "Real P3 metadata preloads before follow", window);
            Check(window.Services.Follow.Current.Page is null && ((ComboBox)window.FindName("EpisodeSelector")).Items.Count > 4, "Real metadata preview is independent of Chrome playback");
            Console.WriteLine("Real Bilibili P3 episode list preloaded before Start.");
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Initial P3 automatic playback and PiP", window);
            Check(window.Services.Follow.Current.Video!.Duration is > 603 and < 605, "P3 duration matches episode");
            var info = window.Services.Follow.Current.Info!;
            Check(info.Episodes.Count > 4 && info.CurrentPart == 3, "Real episode list and current P3");
            var originalPage = window.Services.Follow.Current.Page!;
            var pageCount = (await browser.GetPagesAsync()).Count;
            Console.WriteLine($"Auto follow: P3 ready; {info.Episodes.Count} parts; target={originalPage.Id}.");
            await TestAutomaticMouseVisibilityAsync(window);
            TestLibraryPickerFlow(window);
            await WaitForAsync(() => Ready(3), "Library picker confirm/cancel preserves live P3 and PiP", window);
            await WaitForAsync(() => window.Services.Preview.Current.Info?.Bvid == "BV1MXfEY4EQ2", "Library first map metadata preloads while live P3 continues", window);
            Check(window.Services.Follow.Current.Info!.Bvid == "BV1hjgG6jEa6", "Preview preserves real active video episode list");
            Exception? pickerError = null;
            _ = window.Dispatcher.BeginInvoke(async () =>
            {
                var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
                try
                {
                    SendAltNumber(0x32);
                    await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: true }, "Global hotkey pauses while picker is open", window);
                    SendAltNumber(0x32);
                    await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: false }, "Global hotkey resumes while picker is open", window);
                }
                catch (Exception ex) { pickerError = ex; }
                finally { picker.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
            if (pickerError is not null) throw pickerError;
            Check(window.Services.Follow.Current.Page!.Id == originalPage.Id, "Open library picker keeps exact live target");
            Console.WriteLine("Library picker search/confirm/cancel retains live session; Alt+2 remains active while open.");
            CaptureEpisodeMenu(window, root, "ui-live-episode-menu.png");
            var episodeHotkeys = GetField<HotkeyService>(window, "_hotkeys")!;
            Check(episodeHotkeys.IsRegistered(HotkeyAction.PreviousEpisode) && episodeHotkeys.IsRegistered(HotkeyAction.NextEpisode), "Episode hotkeys registered");
            SendAltNumber(0x27);
            await WaitForAsync(() => Ready(4), "P4 automatic playback and PiP", window);
            Check(window.Services.Follow.Current.Page!.Id == originalPage.Id &&
                   (await browser.GetPagesAsync()).Count == pageCount, "Switch reuses tab");
            Check(window.Services.Follow.Current.Video!.Duration is > 962 and < 964, "P4 duration matches episode");
            Console.WriteLine("Alt+Right P3 -> P4: same tab, playback and restored PiP passed.");
            SendAltNumber(0x25);
            await WaitForAsync(() => Ready(3), "Alt+Left restores P3 and PiP", window);
            SendAltNumber(0x27);
            await WaitForAsync(() => Ready(4), "Alt+Right returns to P4", window);
            var one = (Task)Invoke(window, "SwitchEpisodeAsync", 5)!;
            var two = (Task)Invoke(window, "SwitchEpisodeAsync", 6)!;
            var three = (Task)Invoke(window, "SwitchEpisodeAsync", 4)!;
            await Task.WhenAll(one, two, three);
            await WaitForAsync(() => Ready(4), "Rapid switching settles on last P4 intent", window);
            Console.WriteLine("Rapid P5 -> P6 -> P4 cancellation passed.");

            await controller.ExecuteAsync(window.Services.Follow.Current.Page, new("ensurePipClosed"));
            await WaitForAsync(() => window.Services.Follow.Current.Video is { PictureInPicture: false }, "Browser PiP close is observed", window);
            var mouseVisibility = window.Services.Pip.MouseVisibility;
            Check(mouseVisibility.BrowserProcessId == 0 && mouseVisibility.WindowHandle == 0, "Manual close stops automatic mouse/Alt service");
            keybd_event(0xC0, 0, 0, 0);
            try { await Task.Delay(400); }
            finally { keybd_event(0xC0, 0, 2, 0); }
            await Task.Delay(3500);
            Check(window.Services.Follow.Current.Video is { PictureInPicture: false } &&
                   !window.Services.Follow.Session.AutomaticPending, "Manual browser PiP close remains closed");
            await browser.NavigateAsync(window.Services.Follow.Current.Page, new("BV1hjgG6jEa6", 3));
            await WaitForAsync(() => Ready(3), "Browser-side part change synchronizes and resumes follow", window);
            Console.WriteLine("Browser part synchronization and manual-close suppression passed.");

            var page = window.Services.Follow.Current.Page;
            await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "window.__gvhHeldVideos=[...document.querySelectorAll('video')].map(v=>({v,parent:v.parentNode,next:v.nextSibling}));window.__gvhHeldVideos.forEach(x=>x.v.remove());"
            });
            Invoke(window, "FollowRetry_Click", window, new RoutedEventArgs());
            await WaitForAsync(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("等待视频就绪"), "Video loading feedback", window);
            Check(window.Services.Follow.Session.AutomaticPending, "Loading retains automatic intent");
            await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "window.__gvhHeldVideos.forEach(x=>x.parent.insertBefore(x.v,x.next?.parentNode===x.parent?x.next:null));delete window.__gvhHeldVideos;"
            });
            await WaitForAsync(() => Ready(3), "Loading completion resumes automatic follow", window);
            Console.WriteLine("Delayed readiness and retry without blocking the UI passed.");

            var hotkeys = GetField<HotkeyService>(window, "_hotkeys")!;
            Console.WriteLine("Hotkeys: " + ((TextBlock)window.FindName("HotkeyStatus")).Text);
            Check(hotkeys.IsRegistered(HotkeyAction.TogglePlayback) && hotkeys.IsRegistered(HotkeyAction.SeekBackward) &&
                   hotkeys.IsRegistered(HotkeyAction.SeekForward), "All three Alt-number hotkeys registered");
            SendAltNumber(0x32);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: true }, "Alt+2 pauses actual Bilibili video", window);
            await controller.ExecuteAsync(page, new("seek", 200, Absolute: true));
            await WaitForAsync(() => Math.Abs(window.Services.Follow.Current.Video!.CurrentTime - 200) < 0.5, "Seek test setup", window);
            SendAltNumber(0x31);
            await WaitForAsync(() => Math.Abs(window.Services.Follow.Current.Video!.CurrentTime - 195) < 0.5, "Alt+1 rewinds five seconds", window);
            SendAltNumber(0x33);
            await WaitForAsync(() => Math.Abs(window.Services.Follow.Current.Video!.CurrentTime - 200) < 0.5, "Alt+3 advances five seconds", window);
            SendAltNumber(0x32);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: false }, "Alt+2 resumes actual video", window);
            Console.WriteLine("Actual Alt+1 / Alt+2 / Alt+3 global keyboard actions passed.");
            settings.Hotkeys[HotkeyAction.TogglePlayback] = "Ctrl+Alt+F8";
            Invoke(window, "ApplyHotkeys");
            Check(hotkeys.IsRegistered(HotkeyAction.TogglePlayback), "Custom playback binding registers");
            keybd_event(0x11, 0, 0, 0);
            SendAltNumber(0x77);
            keybd_event(0x11, 0, 2, 0);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: true }, "Custom Ctrl+Alt+F8 pauses video", window);
            settings.Hotkeys[HotkeyAction.TogglePlayback] = "Alt+2";
            Invoke(window, "ApplyHotkeys");
            SendAltNumber(0x32);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Paused: false }, "Restore default binding resumes video", window);
            Console.WriteLine("Editable modifier/key binding performs the real video action.");
            Check(hotkeys.IsRegistered(HotkeyAction.TogglePip) && hotkeys.IsRegistered(HotkeyAction.PlacePip) && hotkeys.IsRegistered(HotkeyAction.ToggleMute), "Three Alt-only auxiliary bindings register");
            SendAltNumber(0x23);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Muted: true }, "Alt+End mutes actual video", window);
            SendAltNumber(0x23);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { Muted: false }, "Alt+End restores sound", window);
            SendAltNumber(0x50);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { PictureInPicture: false }, "Alt+P closes actual PiP", window);
            SendAltNumber(0x50);
            await WaitForAsync(() => window.Services.Follow.Current.Video is { PictureInPicture: true } && ((TextBlock)window.FindName("StatusText")).Text == "画中画已置顶在左下角。", "Alt+P opens and positions PiP", window);
            SendAltNumber(0x24);
            await WaitForAsync(() => ((TextBlock)window.FindName("StatusText")).Text == "画中画已放回本工具所在屏幕的左下角。", "Alt+Home places PiP", window);
            Console.WriteLine("Actual Alt+P / Alt+Home / Alt+End PiP toggle, placement and mute passed.");
            Capture(window, root, "ui-live-follow.png", 1020, 730);
            await new CdpClient().SendAsync(session.BrowserSocket, "Target.closeTarget", new { targetId = page.Id });
            await WaitForAsync(() => ((TextBlock)window.FindName("HeaderConnectionState")).Text == "连接已断开", "Closed page no longer displays connected state", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Restart recovers from closed page", window);
            Check(window.Services.Follow.Current.Page!.Id != page.Id, "Restart selects new target");
            Console.WriteLine("Closed-page feedback and restart recovery passed.");
            await session.ShutdownBrowserAsync();
            await WaitForAsync(() => ((TextBlock)window.FindName("HeaderConnectionState")).Text == "连接已断开", "Entire browser close clears connection state", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Start follow relaunches closed Chrome", window);
            Console.WriteLine("Entire Chrome shutdown and relaunch recovery passed.");
            var libraryVideos = (ComboBox)window.FindName("GuideVideoSelector");
            libraryVideos.SelectedIndex = 0;
            Check(window.Services.Follow.Current.Page.Url.Contains("BV1hjgG6jEa6"), "Preset selection keeps current video running until start");
            await WaitForAsync(() => window.Services.Preview.Current.Info?.Bvid == "BV1MXfEY4EQ2", "Preset episodes available before starting selected map", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(1), "Selected built-in map opens and follows", window);
            Check(VideoIdentity.Parse(window.Services.Follow.Current.Page.Url).Bvid == "BV1MXfEY4EQ2" &&
                   window.Services.Follow.Current.Info?.Bvid == "BV1MXfEY4EQ2", "Built-in preset reaches matching real video and episode list");
            Capture(window, root, "ui-live-library-follow.png", 1020, 730);
            Console.WriteLine("Built-in Hanqing map selection, real video metadata, automatic playback and PiP passed.");
        }
        catch
        {
            Console.WriteLine("Follow request: " + JsonSerializer.Serialize(window.Services.Follow.Session.Current?.Identity));
            Console.WriteLine("Video state: " + JsonSerializer.Serialize(window.Services.Follow.Current.Video));
            try
            {
                var diagnosticPage = window.Services.Follow.Current.Page;
                if (diagnosticPage is not null)
                    Console.WriteLine("Media readiness: " + (await new CdpClient().SendAsync(new Uri(diagnosticPage.WebSocketDebuggerUrl), "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({cid:window.__INITIAL_STATE__?.cid,videos:[...document.querySelectorAll('video')].map(v=>({ready:v.readyState,network:v.networkState,duration:v.duration,paused:v.paused,error:v.error?.message,pip:v===document.pictureInPictureElement}))})"
                    })).GetRawText());
            }
            catch (Exception ex) { Console.WriteLine("Media diagnostics unavailable: " + ex.Message); }
            try { Console.WriteLine("Targets: " + (await new CdpClient().SendAsync(await session.GetCurrentBrowserSocketAsync(), "Target.getTargets", new { })).GetRawText()); }
            catch (Exception ex) { Console.WriteLine("Browser diagnostics unavailable: " + ex.Message); }
            throw;
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }

        bool Ready(int part) => window.Services.Follow.Session is { AutomaticPending: false, Current.Identity.Part: var current } &&
            current == part && window.Services.Follow.Current.Video is { Paused: false, PictureInPicture: true } &&
            VideoIdentity.Parse(window.Services.Follow.Current.Video!.Url).Part == part;
    }

    public static async Task TestAutomaticMouseVisibilityAsync(GenshinVideoHelper.App.MainWindow window)
    {
        var visibility = window.Services.Pip.MouseVisibility;
        await WaitForAsync(() => visibility.WindowHandle != 0, "Open PiP automatically starts mouse service", window);
        var pip = visibility.WindowHandle;
        Check(GetCursorPos(out var originalCursor) && GetWindowRect(pip, out _), "Automatic test obtains real cursor and PiP bounds");
        GetWindowRect(pip, out var bounds);
        var originalWindowState = window.WindowState;
        try
        {
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            await WaitForAsync(() => visibility.IsTemporarilyHidden && HasTransparentAppearance(pip), "Automatic timer hides on entering real PiP", window);
            Check(window.Services.Follow.Current.Video is { PictureInPicture: true, Paused: false }, "Temporary hiding retains active playing PiP session");
            keybd_event(0xC0, 0, 0, 0);
            await WaitForAsync(() => !visibility.IsTemporarilyHidden && IsWindowVisible(pip) && !HasTransparentAppearance(pip), "Held tilde restores PiP through actual timer", window);
            keybd_event(0xC0, 0, 2, 0);
            await WaitForAsync(() => visibility.IsTemporarilyHidden && HasTransparentAppearance(pip), "Released tilde hides again through actual timer", window);
            window.WindowState = WindowState.Minimized;
            SetCursorPos(bounds.Right + (bounds.Right - bounds.Left), bounds.Top);
            await WaitForAsync(() => !visibility.IsTemporarilyHidden && IsWindowVisible(pip) && !HasTransparentAppearance(pip), "Mouse avoidance remains active while helper is minimized", window);
            Console.WriteLine("Actual Bilibili PiP automatic mouse/tilde detection and minimized-helper operation passed.");
        }
        finally
        {
            keybd_event(0xC0, 0, 2, 0);
            Invoke(window, "RestoreFromTray");
            window.WindowState = originalWindowState;
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }
    public static async Task WaitForAsync(Func<bool> predicate, string label, GenshinVideoHelper.App.MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(150);
        }
        throw new Exception("FAIL: " + label + "; UI: " + ((TextBlock)window.FindName("StatusText")).Text);
    }

}
