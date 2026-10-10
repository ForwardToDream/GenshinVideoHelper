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

internal static class PipTests
{
    public static void PipTest(string root, bool example = false)
    {
        var app = new Application();
        var helper = new Window { Width = 100, Height = 100, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        helper.Show();
        var handle = new WindowInteropHelper(helper).Handle;
        var hotkeys = new HotkeyService(handle);
        try
        {
            var blocker = new Window { Width = 50, Height = 50, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            blocker.Show();
            var blockerHandle = new WindowInteropHelper(blocker).Handle;
            var reserved = RegisterHotKey(blockerHandle, 0x99AA, 0x4001, 0x31);
            try
            {
                if (reserved)
                {
                    var blocked = hotkeys.Enable();
                    Check(blocked.Any(label => label.Contains("Alt+1")) && !hotkeys.IsRegistered(HotkeyAction.SeekBackward) && hotkeys.RegisteredCount > 0,
                        "An occupied Alt+1 reports conflict while other hotkeys remain available");
                    Console.WriteLine("Partial hotkey conflict handling passed.");
                }
                else Console.WriteLine("Alt+1 is already occupied; reservation test skipped.");
            }
            finally
            {
                if (reserved) UnregisterHotKey(blockerHandle, 0x99AA);
                blocker.Close();
                hotkeys.Disable();
            }
            var reservedMode = RegisterHotKey(handle, 0x99AB, 0x4000, 0xC0);
            try
            {
                Check(reservedMode, "Reserve the single mode key for the conflict test");
                var blocked = hotkeys.Enable();
                Check(blocked.Any(label => label.Contains("显隐模式") && label.Contains("~")) &&
                    !hotkeys.IsRegistered(HotkeyAction.ReversePipVisibility) && hotkeys.RegisteredCount == 8,
                    "A conflicting tilde reports exactly eight available actions");
            }
            finally
            {
                hotkeys.Disable();
                if (reservedMode) UnregisterHotKey(handle, 0x99AB);
            }
            var conflicts = hotkeys.Enable();
            Check(hotkeys.RegisteredCount > 0, "At least one hotkey registered");
            if (conflicts.Count > 0) Console.WriteLine("Unavailable hotkeys: " + string.Join("; ", conflicts));
            hotkeys.Disable();
            hotkeys.Enable();
            Console.WriteLine("Hotkey registration, release and re-registration passed.");
            // The native PiP companion is a WPF window; keep its lifetime on the UI dispatcher.
            var work = helper.Dispatcher.InvokeAsync(async () =>
            {
                if (example)
                {
                    await BilibiliTestAsync(root, headed: true, helperWindow: handle);
                    return;
                }
                await using var session = await ChromeFixture.StartAsync(root, headed: true);
                var controller = new VideoController();
                VideoState? state = null;
                for (var i = 0; i < 80; i++)
                {
                    try { state = await controller.ExecuteAsync(session.Page, new("ensurePip")); break; }
                    catch (VideoNotReadyException) { await Task.Delay(200); }
                }
                if (state is null)
                    Console.WriteLine("Local media readiness: " + (await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({url:location.href,ready:document.readyState,text:document.body?.innerText,videos:[...document.querySelectorAll('video')].map(v=>({src:v.currentSrc,ready:v.readyState,network:v.networkState,error:v.error?.message}))})"
                    })).GetRawText());
                Check(state?.PictureInPicture == true, "Native Chrome PiP entered after media readiness");
                state = await controller.ExecuteAsync(session.Page, new("ensurePip"));
                Check(state.PictureInPicture, "Repeated ensure PiP does not close window");
                var reply = await new CdpClient().SendAsync(session.BrowserSocket, "SystemInfo.getProcessInfo", new { });
                var process = reply.GetProperty("processInfo").EnumerateArray()
                    .First(item => item.GetProperty("type").GetString() == "browser");
                var pid = (int)process.GetProperty("id").GetDouble();
                await PipWindowService.PlaceAsync(pid, handle, 420, 20, 16d / 9);
                nint pip = 0;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner != pid || !IsWindowVisible(window) || (GetWindowLong(window, -20) & 8) == 0) return true;
                    var title = new StringBuilder(512);
                    GetWindowText(window, title, title.Capacity);
                    Console.WriteLine($"Owned topmost window: {title}");
                    pip = window;
                    return false;
                }, 0);
                Check(pip != 0, "Owned topmost PiP window");
                GetWindowRect(pip, out var bounds);
                var monitor = MonitorFromWindow(handle, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                GetMonitorInfo(monitor, ref info);
                var margin = (int)Math.Round(20 * GetDpiForWindow(handle) / 96d);
                Check(Math.Abs(bounds.Left - info.Work.Left - margin) <= 3, "PiP left margin");
                Check(Math.Abs(bounds.Bottom - info.Work.Bottom + margin) <= 3, "PiP bottom margin");
                await TestMouseVisibilityAsync(session, pid, handle, controller, hotkeys, root);
                state = await controller.ExecuteAsync(session.Page, new("pip"));
                Check(!state.PictureInPicture, "Exit PiP");
                Console.WriteLine("Native PiP entry, topmost, bottom-left positioning and exit passed.");
            }).Task.Unwrap();
            var frame = new DispatcherFrame();
            work.ContinueWith(_ => helper.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            work.GetAwaiter().GetResult();
        }
        finally { hotkeys.Dispose(); helper.Close(); app.Shutdown(); }
    }

    public static bool HasTransparentAppearance(nint window) =>
        (GetWindowLong(window, -20) & 0x00000020) != 0 &&
        GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && alpha == 0 && (flags & 2) != 0;
    public static async Task TestMouseVisibilityAsync(ChromeFixture session, int pid, nint helper, VideoController controller, HotkeyService hotkeys, string root)
    {
        Check(GetCursorPos(out var originalCursor), "Read original cursor position");
        using var visibility = new PipMouseVisibilityService(pollAutomatically: false);
        void OnHotkey(HotkeyAction action) { if (action == HotkeyAction.ReversePipVisibility) visibility.CycleMode(); }
        hotkeys.Pressed += OnHotkey;
        Check(hotkeys.IsRegistered(HotkeyAction.ReversePipVisibility), "Unmodified tilde is registered as a system hotkey");
        bool FrameVisible() => visibility.FrameWindowHandle != 0 && IsWindowVisible(visibility.FrameWindowHandle);
        void CheckFrame(string symbol)
        {
            Check(FrameVisible(), "Mode bar is visible");
            Check(GetWindowRect(visibility.FrameWindowHandle, out var frameBounds) && GetWindowRect(visibility.WindowHandle, out var videoBounds) &&
                Math.Abs(frameBounds.Left - videoBounds.Left) <= 1 && Math.Abs(frameBounds.Right - videoBounds.Right) <= 1 &&
                Math.Abs(frameBounds.Bottom - videoBounds.Top) <= 1, "Bar follows the video's full width immediately above it");
            Check(WindowFromPoint(new ScreenPoint { X = (frameBounds.Left + frameBounds.Right) / 2, Y = (frameBounds.Top + frameBounds.Bottom) / 2 }) != visibility.FrameWindowHandle, "Bar does not intercept mouse hit testing");
            Check((GetWindowLong(visibility.FrameWindowHandle, -20) & 0x080000A0) == 0x080000A0,
                "Bar is a non-activating, mouse-transparent tool window");
            var bar = Application.Current.Windows.OfType<GenshinVideoHelper.App.PipFrameWindow>().Single(w => w.WindowHandle == visibility.FrameWindowHandle);
            Check(((UIElement)bar.FindName(symbol)).Visibility == Visibility.Visible, "Correct mode icon: " + symbol);
            Capture(bar, root, "pip-mode-" + visibility.Mode + ".png", (int)bar.ActualWidth, 24);
        }
        try
        {
            var pip = PipWindowService.FindPip(pid);
            Check(pip != 0 && GetWindowRect(pip, out _), "Discover only owned PiP window");
            var originalExtendedStyle = GetWindowLong(pip, -20);
            GetWindowRect(pip, out var bounds);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var centerY = (bounds.Top + bounds.Bottom) / 2;
            Check(SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.25), centerY), "Move pointer outside expanded area");
            visibility.TrackBrowser(pid);
            await ExpectVisibility(true, "Outside shows PiP");
            var foreground = GetForegroundWindow();
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));
            await Task.Delay(700);
            var startTime = (await controller.ExecuteAsync(session.Page, new("status"))).CurrentTime;
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "20-percent margin hides before entering actual window");
            Check(WindowFromPoint(new ScreenPoint { X = (bounds.Left + bounds.Right) / 2, Y = centerY }) != pip, "Hidden window no longer captures mouse hit testing");
            await Task.Delay(700);
            var hiddenState = await controller.ExecuteAsync(session.Page, new("status"));
            Check(hiddenState.PictureInPicture && !hiddenState.Paused && hiddenState.CurrentTime > startTime + 0.2, "Hidden Chrome PiP session stays open and continues playback");
            var pausedHidden = await controller.ExecuteAsync(session.Page, new("toggle"));
            Check(pausedHidden.PictureInPicture && pausedHidden.Paused, "User can pause while PiP is temporarily hidden");
            SetCursorPos(bounds.Right + width, centerY);
            await ExpectVisibility(true, "Leaving hiding region restores paused PiP");
            Check((await controller.ExecuteAsync(session.Page, new("status"))).Paused, "Restoring opacity never resumes a user-paused video");
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "Paused PiP continues normal mouse avoidance");
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));

            Check(!FrameVisible(), "Automatic mode hides video and bar together");
            keybd_event(0xC0, 0, 0, 0);
            await ExpectVisibility(true, "Press tilde locks video visible inside the mouse region");
            Check(visibility.Mode == PipVisibilityMode.AlwaysVisible, "Automatic -> locked");
            CheckFrame("LockIcon");
            for (var repeat = 0; repeat < 4; repeat++) keybd_event(0xC0, 0, 0, 0);
            await Task.Delay(150);
            Check(visibility.Mode == PipVisibilityMode.AlwaysVisible, "Holding/repeating the key cannot cycle again");
            keybd_event(0xC0, 0, 2, 0);
            await Task.Delay(80);
            await ExpectVisibility(true, "Key release preserves locked mode");

            keybd_event(0xC0, 0, 0, 0);
            keybd_event(0xC0, 0, 2, 0);
            await ExpectVisibility(false, "Second press forces hidden video");
            Check(visibility.Mode == PipVisibilityMode.AlwaysHidden, "Locked -> hidden");
            Check(!FrameVisible(), "Forced hide hides video and bar together");
            SetCursorPos(bounds.Right + width, centerY);
            await ExpectVisibility(false, "Forced hide ignores pointer leaving the region");
            Check(!FrameVisible(), "Forced hide keeps the bar hidden after the pointer leaves");
            keybd_event(0xC0, 0, 0, 0);
            keybd_event(0xC0, 0, 2, 0);
            await ExpectVisibility(true, "Third press returns to mouse avoidance");
            Check(visibility.Mode == PipVisibilityMode.Automatic, "Hidden -> automatic");
            CheckFrame("MouseIcon");
            GetWindowRect(visibility.FrameWindowHandle, out var barBounds);
            SetCursorPos((barBounds.Left + barBounds.Right) / 2, (barBounds.Top + barBounds.Bottom) / 2);
            await ExpectVisibility(false, "Approaching the bar also hides both windows");
            Check(!FrameVisible(), "Bar itself participates in automatic avoidance");

            var bindings = HotkeyBindings.Defaults();
            bindings[HotkeyAction.ReversePipVisibility] = "Ctrl+F8";
            Check(hotkeys.Enable(bindings).Count == 0, "Register editable mode chord");
            keybd_event(0x77, 0, 0, 0);
            keybd_event(0x77, 0, 2, 0);
            await Task.Delay(80);
            Check(visibility.Mode == PipVisibilityMode.Automatic, "Chord without its modifier does nothing");
            keybd_event(0x11, 0, 0, 0);
            keybd_event(0x77, 0, 0, 0);
            keybd_event(0x77, 0, 2, 0);
            keybd_event(0x11, 0, 2, 0);
            await ExpectVisibility(true, "Custom chord cycles once and persists after release");
            hotkeys.Disable();
            keybd_event(0xC0, 0, 0, 0);
            keybd_event(0xC0, 0, 2, 0);
            await Task.Delay(80);
            Check(visibility.Mode == PipVisibilityMode.AlwaysVisible, "Disabling hotkeys prevents mode changes");
            Check(hotkeys.Enable().Count == 0, "Restore default bindings");
            visibility.CycleMode();
            visibility.CycleMode();
            SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.25), centerY);
            await ExpectVisibility(true, "Show both windows after leaving the area");            Check(GetForegroundWindow() == foreground, "Restoring native PiP does not steal focus");
            Check((GetWindowLong(pip, -20) & 0x00080020) == (originalExtendedStyle & 0x00080020), "Restore original layering and hit-test styles");

            SetCursorPos((bounds.Left + bounds.Right) / 2, centerY);
            await ExpectVisibility(false, "Hide actual window interior");
            var monitor = MonitorFromWindow(helper, 2);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref monitorInfo);
            Check(SetWindowPos(pip, new nint(-1), monitorInfo.Work.Left + 100, monitorInfo.Work.Top + 70, width + 80, height + 45, 0x0010), "Move and resize hidden window without showing");
            await ExpectVisibility(true, "Uses moved bounds instead of former hiding region");
            CheckFrame("MouseIcon");
            GetWindowRect(pip, out bounds);
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            await ExpectVisibility(false, "Resized current window region hides");
            visibility.TrackBrowser(pid + 100000);
            await Task.Delay(80);
            Check(IsWindowVisible(pip) && !HasTransparentAppearance(pip) && visibility.WindowHandle == 0, "Changing tracked browser restores prior window and ignores other process IDs");
            visibility.TrackBrowser(pid);
            await ExpectVisibility(false, "Track owned window again");

            Check(PostMessage(pip, 0x0010, 0, 0), "Close owned native PiP using its actual close message");
            for (var i = 0; i < 40; i++)
            {
                if (!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture) break;
                await Task.Delay(50);
            }
            Check(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture, "Chrome observes native window close");
            SetCursorPos(bounds.Right + width, bounds.Top);
            for (var i = 0; i < 6; i++)
            {
                keybd_event(0xC0, 0, i % 2 == 0 ? 0u : 2u, 0);
                visibility.Refresh();
                await Task.Delay(40);
            }
            Check(PipWindowService.FindPip(pid) == 0 && visibility.WindowHandle == 0 && !FrameVisible(), "Closing PiP removes the bar; cycling never reopens either window");
            visibility.Suspend();
            Check(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture && visibility.BrowserProcessId == 0 && visibility.WindowHandle == 0, "Manual PiP close stops hiding; mouse/reversal key never reopen it");
            await controller.ExecuteAsync(session.Page, new("ensurePip"));
            await PipWindowService.PlaceAsync(pid, helper, 420, 20, 16d / 9);
            pip = PipWindowService.FindPip(pid, includeHidden: true);
            GetWindowRect(pip, out bounds);
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            visibility.TrackBrowser(pid);
            await ExpectVisibility(false, "Reopened PiP resumes mouse avoidance");
            visibility.Dispose();
            Check(visibility.FrameWindowHandle == 0, "Disposal destroys the bar");
            for (var i = 0; i < 60 && !IsWindowVisible(pip); i++) await Task.Delay(20);
            Check(IsWindowVisible(pip) && !HasTransparentAppearance(pip) && (await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture, "Stopping helper restores only its temporarily hidden window");
            Console.WriteLine("PiP modes: three-state cycling, no-repeat, editable chord, both-window mouse avoidance, bar icons/placement, playback, focus, move/resize, manual close and disposal passed.");
        }
        finally
        {
            keybd_event(0xC0, 0, 2, 0);
            keybd_event(0x12, 0, 2, 0);
            keybd_event(0x11, 0, 2, 0);
            keybd_event(0x77, 0, 2, 0);
            hotkeys.Pressed -= OnHotkey;
            visibility.Dispose();
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }

        async Task ExpectVisibility(bool shown, string label)
        {
            for (var i = 0; i < 100; i++)
            {
                visibility.Refresh();
                if (visibility.WindowHandle != 0 && IsWindowVisible(visibility.WindowHandle) && HasTransparentAppearance(visibility.WindowHandle) == !shown && visibility.IsTemporarilyHidden == !shown) return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("ASSERT: " + label);
        }
    }

}
