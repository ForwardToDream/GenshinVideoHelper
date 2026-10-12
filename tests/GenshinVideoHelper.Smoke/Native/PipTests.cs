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
        var originalForeground = GetForegroundWindow();
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
            var reservedMode = RegisterHotKey(handle, 0x99AB, 0x4001, 0x34);
            try
            {
                Check(reservedMode, "Reserve the default mode chord for the conflict test");
                var blocked = hotkeys.Enable();
                Check(blocked.Any(label => label.Contains("显隐模式") && label.Contains("Alt+4")) &&
                    !hotkeys.IsRegistered(HotkeyAction.ReversePipVisibility) && hotkeys.RegisteredCount == 8,
                    "A conflicting Alt+4 reports exactly eight available actions");
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
                await using var session = await ChromiumFixture.StartAsync(root, headed: true);
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
                var margin = ScreenLayout.Margin(20, DisplayMetrics.ForWindow(handle).Dpi);
                Check(Math.Abs(bounds.Left - info.Work.Left - margin) <= 3, "PiP left margin");
                Check(Math.Abs(bounds.Bottom - info.Work.Bottom + margin) <= 3, "PiP bottom margin");
                // Match normal use: the owned browser is behind a separate foreground app.
                var browserWindow = await new CdpClient().SendAsync(session.BrowserSocket, "Browser.getWindowForTarget", new { targetId = session.Page.Id });
                await new CdpClient().SendAsync(session.BrowserSocket, "Browser.setWindowBounds", new
                { windowId = browserWindow.GetProperty("windowId").GetInt32(), bounds = new { windowState = "minimized" } });
                using var foregroundHost = await StartForegroundFixtureAsync();
                try
                {
                    Check(SetForegroundWindow(foregroundHost.MainWindowHandle), "Activate the isolated foreground fixture");
                    await TestMouseVisibilityAsync(session, pid, handle, controller, hotkeys, root);
                    await TestPlacementPersistenceAsync(session, pid, handle, controller, root);
                }
                finally
                {
                    foregroundHost.CloseMainWindow();
                    if (!await Task.Run(() => foregroundHost.WaitForExit(3000))) foregroundHost.Kill(entireProcessTree: true);
                }
                state = await controller.ExecuteAsync(session.Page, new("ensurePipClosed"));
                Check(!state.PictureInPicture, "Exit PiP");
                Console.WriteLine("Native PiP entry, topmost, bottom-left positioning and exit passed.");
            }).Task.Unwrap();
            var frame = new DispatcherFrame();
            work.ContinueWith(_ => helper.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            work.GetAwaiter().GetResult();
        }
        finally { hotkeys.Dispose(); helper.Close(); app.Shutdown(); SetForegroundWindow(originalForeground); }
    }

    private static async Task<Process> StartForegroundFixtureAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(PipTests).Assembly.Location);
        start.ArgumentList.Add("--focus-fixture");
        var process = Process.Start(start)!;
        try
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                process.Refresh();
                if (process.MainWindowHandle != 0) return process;
                if (process.HasExited) throw new InvalidOperationException("Foreground fixture exited before creating its window.");
                await Task.Delay(100);
            }
            throw new TimeoutException("Foreground fixture window did not start.");
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
    }

    public static bool HasTransparentAppearance(nint window) =>
        (GetWindowLong(window, -20) & 0x00000020) != 0 &&
        GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && alpha == 0 && (flags & 2) != 0;
    public static async Task TestMouseVisibilityAsync(ChromiumFixture session, int pid, nint helper, VideoController controller, HotkeyService hotkeys, string root)
    {
        Check(GetCursorPos(out var originalCursor), "Read original cursor position");
        using var visibility = new PipMouseVisibilityService(pollAutomatically: false);
        void OnHotkey(HotkeyAction action) { if (action == HotkeyAction.ReversePipVisibility) visibility.CycleMode(); }
        hotkeys.Pressed += OnHotkey;
        Check(hotkeys.IsRegistered(HotkeyAction.ReversePipVisibility), "Alt+4 is registered as the default mode hotkey");
        bool FrameVisible() => visibility.FrameWindowHandle != 0 && IsWindowVisible(visibility.FrameWindowHandle);
        void CheckFrame(string symbol)
        {
            Check(FrameVisible(), "Unified frame is visible");
            Check(GetWindowRect(visibility.FrameWindowHandle, out var frameBounds), "Frame bounds available");
            Check(GetWindowRect(visibility.WindowHandle, out var videoBounds), "Native video bounds available");
            Check(videoBounds.Right < 0 && videoBounds.Bottom < 0 && IsWindowVisible(visibility.WindowHandle),
                "Chrome's live source stays open off-screen");
            Check((GetWindowLong(visibility.FrameWindowHandle, -20) & 0x08000080) == 0x08000080,
                "Frame is a non-activating tool window");
            var bar = Application.Current.Windows.OfType<GenshinVideoHelper.App.PipFrameWindow>().Single(w => w.WindowHandle == visibility.FrameWindowHandle);
            Check(bar.IsVideoEmbedded, "Chrome's GPU surface is embedded in the frame");
            Check(((UIElement)bar.FindName(symbol)).Visibility == Visibility.Visible, "Correct mode icon: " + symbol);
            CaptureEmbeddedFrame(visibility.FrameWindowHandle, root, "pip-frame-" + visibility.Mode + ".png");
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
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));
            visibility.TrackBrowser(pid);
            await ExpectVisibility(true, "Outside shows PiP");
            await PipWindowService.PlaceAsync(pid, helper, 420, 20, 16d / 9);
            GetWindowRect(visibility.FrameWindowHandle, out var placedFrame);
            var placementMonitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(helper, 2), ref placementMonitor);
            var placementMargin = ScreenLayout.Margin(20, DisplayMetrics.ForWindow(helper).Dpi);
            Check(Math.Abs(placedFrame.Left - placementMonitor.Work.Left - placementMargin) <= 1 &&
                Math.Abs(placedFrame.Bottom - placementMonitor.Work.Bottom + placementMargin) <= 1,
                "Bottom-left placement moves the whole embedded frame");
            await Task.Delay(300);
            GetWindowRect(visibility.FrameWindowHandle, out bounds);
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
            centerY = (bounds.Top + bounds.Bottom) / 2;
            var firstPixels = CaptureEmbeddedFrame(visibility.FrameWindowHandle, root, "pip-frame-attached.png");
            await Task.Delay(400);
            var nextPixels = CaptureEmbeddedFrame(visibility.FrameWindowHandle, root, "pip-frame-playing.png");
            Check(firstPixels != nextPixels, "Off-screen Chrome supplies changing video frames, not a frozen thumbnail");
            var foreground = GetForegroundWindow();
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));
            await Task.Delay(700);
            var startTime = (await controller.ExecuteAsync(session.Page, new("status"))).CurrentTime;
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "20-percent margin hides before entering actual window");
            Check(WindowFromPoint(new ScreenPoint { X = (bounds.Left + bounds.Right) / 2, Y = centerY }) != pip && WindowFromPoint(new ScreenPoint { X = (bounds.Left + bounds.Right) / 2, Y = centerY }) != visibility.FrameWindowHandle, "Hidden frame no longer captures mouse hit testing");
            await Task.Delay(700);
            var hiddenState = await controller.ExecuteAsync(session.Page, new("status"));
            Check(hiddenState.PictureInPicture && !hiddenState.Paused && hiddenState.CurrentTime > startTime + 0.2, "Hidden Chrome PiP session stays open and continues playback");
            var pausedHidden = await controller.ExecuteAsync(session.Page, new("toggle"));
            Check(pausedHidden.PictureInPicture && pausedHidden.Paused, "User can pause while PiP is temporarily hidden");
            SetCursorPos(bounds.Right + width, centerY);
            await ExpectVisibility(true, "Leaving hiding region restores paused PiP");
            Check((await controller.ExecuteAsync(session.Page, new("status"))).Paused, "Showing the frame never resumes a user-paused video");
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "Paused PiP continues normal mouse avoidance");
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));

            Check(!FrameVisible(), "Automatic mode hides video and bar together");
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0x34, 0, 0, 0);
            await ExpectVisibility(true, "Press Alt+4 locks video visible inside the mouse region");
            Check(visibility.Mode == PipVisibilityMode.AlwaysVisible, "Automatic -> locked");
            CheckFrame("LockIcon");
            keybd_event(0x34, 0, 2, 0);
            keybd_event(0x12, 0, 2, 0);
            GetWindowRect(visibility.FrameWindowHandle, out var defaultBounds);
            var frameWindow = Application.Current.Windows.OfType<GenshinVideoHelper.App.PipFrameWindow>()
                .Single(w => w.WindowHandle == visibility.FrameWindowHandle);
            var frameScale = GetDpiForWindow(visibility.FrameWindowHandle) / 96d;
            SetWindowPos(visibility.FrameWindowHandle, new nint(-1), defaultBounds.Left, defaultBounds.Top,
                (int)Math.Round(frameWindow.MinWidth * frameScale), (int)Math.Round(frameWindow.MinHeight * frameScale), 0x0010);
            await Task.Delay(200);
            CaptureEmbeddedFrame(visibility.FrameWindowHandle, root, "pip-frame-minimum.png");
            SetWindowPos(visibility.FrameWindowHandle, new nint(-1), defaultBounds.Left, defaultBounds.Top,
                defaultBounds.Right - defaultBounds.Left, defaultBounds.Bottom - defaultBounds.Top, 0x0010);
            await Task.Delay(200);
            await TestEmbeddedDragAsync(visibility, foreground, false);
            await TestEmbeddedDragAsync(visibility, foreground, true);
            GetWindowRect(visibility.FrameWindowHandle, out bounds);
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
            centerY = (bounds.Top + bounds.Bottom) / 2;
            CaptureEmbeddedFrame(visibility.FrameWindowHandle, root, "pip-frame-dragged.png");
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0x34, 0, 0, 0);
            await ExpectVisibility(false, "A fresh key press enters forced hiding");
            for (var repeat = 0; repeat < 4; repeat++) keybd_event(0x34, 0, 0, 0);
            await Task.Delay(150);
            Check(visibility.Mode == PipVisibilityMode.AlwaysHidden, "Holding/repeating the key cannot cycle again");
            keybd_event(0x34, 0, 2, 0);
            keybd_event(0x12, 0, 2, 0);
            await Task.Delay(80);
            await ExpectVisibility(false, "Key release preserves forced hiding");
            Check(visibility.Mode == PipVisibilityMode.AlwaysHidden, "Locked -> hidden");
            Check(!FrameVisible(), "Forced hide hides video and bar together");
            SetCursorPos(bounds.Right + width, centerY);
            await ExpectVisibility(false, "Forced hide ignores pointer leaving the region");
            Check(!FrameVisible(), "Forced hide keeps the bar hidden after the pointer leaves");
            SendAltNumber(0x34);
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
            SendAltNumber(0x34);
            await Task.Delay(80);
            Check(visibility.Mode == PipVisibilityMode.AlwaysVisible, "Disabling hotkeys prevents mode changes");
            Check(hotkeys.Enable().Count == 0, "Restore default bindings");
            visibility.CycleMode();
            visibility.CycleMode();
            SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.25), centerY);
            await ExpectVisibility(true, "Show the whole frame after leaving the area");
            Check(GetForegroundWindow() == foreground, $"Showing the frame does not steal focus ({WindowLabel(foreground)} -> {WindowLabel(GetForegroundWindow())})");
            Check((GetWindowLong(pip, -20) & 0x00080020) == (originalExtendedStyle & 0x00080020), "Hiding never changes Chrome's rendering styles");

            SetCursorPos((bounds.Left + bounds.Right) / 2, centerY);
            await ExpectVisibility(false, "Hide actual window interior");
            var monitor = MonitorFromWindow(helper, 2);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref monitorInfo);
            Check(SetWindowPos(visibility.FrameWindowHandle, new nint(-1), monitorInfo.Work.Left + 100, monitorInfo.Work.Top + 70, width + 80, height + 80, 0x0010), "Move and resize the whole hidden frame");
            await ExpectVisibility(true, "Uses moved bounds instead of former hiding region");
            await Task.Delay(250);
            CheckFrame("MouseIcon");
            GetWindowRect(visibility.FrameWindowHandle, out bounds);
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            await ExpectVisibility(false, "Resized current window region hides");
            visibility.TrackBrowser(pid + 100000);
            await Task.Delay(80);
            Check(IsWindowVisible(pip) && !HasTransparentAppearance(pip) && (GetWindowLong(pip, -16) & 0x40000000) == 0 && IsWindowEnabled(pip) && visibility.WindowHandle == 0, "Changing tracked browser detaches and restores the owned video, ignoring other process IDs");
            visibility.TrackBrowser(pid);
            await ExpectVisibility(false, "Track owned window again");

            var embeddedFrame = Application.Current.Windows.OfType<GenshinVideoHelper.App.PipFrameWindow>()
                .Single(w => w.WindowHandle == visibility.FrameWindowHandle);
            ((Button)embeddedFrame.FindName("CloseVideoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("Unified frame close button invoked.");
            for (var i = 0; i < 40; i++)
            {
                if (!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture) break;
                await Task.Delay(50);
            }
            Check(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture, "Chrome observes native window close");
            SetCursorPos(bounds.Right + width, bounds.Top);
            for (var i = 0; i < 3; i++)
            {
                SendAltNumber(0x34);
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
            for (var i = 0; i < 60 && (await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture; i++)
                await Task.Delay(20);
            Check(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture && !IsWindowVisible(pip),
                "Disposal closes the owned Chrome PiP without restoring an independent visible window");
            Console.WriteLine("Embedded PiP: live GPU surface, thin border, header/video drag, resize, three modes, hotkeys, playback, focus, close and detach passed.");
        }
        finally
        {
            keybd_event(0x34, 0, 2, 0);
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
                if (visibility.WindowHandle != 0 && IsWindowVisible(visibility.WindowHandle) && FrameVisible() == shown && visibility.IsTemporarilyHidden == !shown) return;
                await Task.Delay(20);
            }
            GetWindowRect(visibility.FrameWindowHandle, out var debugBounds);
            GetCursorPos(out var debugCursor);
            throw new InvalidOperationException($"ASSERT: {label}; source={visibility.WindowHandle:X}, frame={visibility.FrameWindowHandle:X}, sourceVisible={IsWindowVisible(visibility.WindowHandle)}, frameVisible={FrameVisible()}, hidden={visibility.IsTemporarilyHidden}, frameBounds={debugBounds.Left},{debugBounds.Top}-{debugBounds.Right},{debugBounds.Bottom}, cursor={debugCursor.X},{debugCursor.Y}");
        }
    }

    private static async Task TestEmbeddedDragAsync(PipMouseVisibilityService visibility, nint foreground, bool videoArea)
    {
        GetWindowRect(visibility.FrameWindowHandle, out var before);
        int x = before.Left + (before.Right - before.Left) / 2;
        int y = videoArea ? (before.Top + before.Bottom) / 2 : before.Top + ScreenLayout.Pixels(12, DisplayMetrics.ForWindow(visibility.FrameWindowHandle).Dpi);
        await DragPointerAsync(x, y, 65, -35);
        GetWindowRect(visibility.FrameWindowHandle, out var after);
        Check(Math.Abs(after.Left - before.Left - 65) <= 2 && Math.Abs(after.Top - before.Top + 35) <= 2,
            "Dragging " + (videoArea ? "video" : "header") + " moves the whole frame");
        Check(GetWindowRect(visibility.WindowHandle, out var source) && source.Right < 0 && source.Bottom < 0,
            "Dragging moves one window while Chrome's source remains off-screen");
        Check(GetForegroundWindow() == foreground, $"Dragging the frame keeps foreground game/app focus ({WindowLabel(foreground)} -> {WindowLabel(GetForegroundWindow())})");
    }

    private static string WindowLabel(nint handle)
    {
        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        GetWindowThreadProcessId(handle, out var pid);
        return $"0x{handle:X}/PID {pid}: {title}";
    }

    private static async Task DragPointerAsync(int x, int y, int dx, int dy)
    {
        await Task.Run(() =>
        {
            SetCursorPos(x, y);
            try
            {
                mouse_event(2, 0, 0, 0, 0);
                Thread.Sleep(100);
                SetCursorPos(x + dx, y + dy);
                Thread.Sleep(100);
            }
            finally { mouse_event(4, 0, 0, 0, 0); }
        });
        await Task.Delay(80);
    }

    private static async Task TestPlacementPersistenceAsync(ChromiumFixture session, int pid, nint helper, VideoController video, string root)
    {
        Check(GetCursorPos(out var originalCursor), "Read cursor before layout tests");
        using var directory = TestArtifacts.CreateTemp(root, "pip-layout");
        var store = new JsonSettingsStore(Path.Combine(directory.Path, "config.json"));
        var settings = store.Load().Settings;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(helper, 2), ref monitor);
        PipPlacement saved;
        PipLayout savedLayout;
        try
        {
            using (var first = new WindowsPipController(settings, store) { HelperWindow = helper })
            {
                var state = await video.ExecuteAsync(session.Page, new("ensurePip"));
                await first.RestoreAsync(pid, state, CancellationToken.None);
                var visibility = first.MouseVisibility;
                var initial = visibility.Placement!;
                var display = DisplayMetrics.ForWindow(helper);
                var margin = ScreenLayout.Margin(settings.PipMargin, display.Dpi);
                var expected = ScreenLayout.DefaultPip(display.WorkArea, display.Dpi, settings.PipWidth, settings.PipMargin, 16d / 9);
                Check(initial == expected, "Shown factory frame has the expected physical size as well as position");
                Console.WriteLine($"Factory frame: {initial.Width}x{initial.Height}; work area {display.WorkArea.Width}x{display.WorkArea.Height}, DPI {display.Dpi}.");
                Check(initial.Left == monitor.Work.Left + margin && initial.Top + initial.Height == monitor.Work.Bottom - margin,
                    "Factory layout uses the current bottom-left defaults");
                Check(store.Load().Settings.PipLayout is null, "Automatic factory placement does not replace a user's layout");
                visibility.CycleMode(); // lock visible while interacting
                var foreground = GetForegroundWindow();
                await TestEmbeddedDragAsync(visibility, foreground, false);
                Check(store.Load().Settings.PipLayout == visibility.Layout, "A completed drag saves its proportional position immediately");
                var beforeResize = visibility.Placement!;
                await DragPointerAsync(beforeResize.Left + beforeResize.Width - 3, beforeResize.Top + beforeResize.Height - 3, 60, 40);
                saved = visibility.Placement!;
                Check(saved.Width == beforeResize.Width + 60 && saved.Height == beforeResize.Height + 40,
                    "A real edge gesture resizes the complete frame");
                savedLayout = visibility.Layout!;
                Check(savedLayout == store.Load().Settings.PipLayout &&
                    ScreenLayout.Restore(savedLayout, display.WorkArea, display.Dpi) == saved,
                    "A completed resize saves proportions that reproduce both dimensions");
                var contents = File.ReadAllText(Path.Combine(directory.Path, "config.json"));
                Check(SetWindowPos(visibility.FrameWindowHandle, new nint(-1), monitor.Work.Left + 100, monitor.Work.Top + 80,
                    saved.Width + 40, saved.Height + 20, 0x0010), "Apply a system/programmatic bounds change");
                await Task.Delay(250);
                Check(contents == File.ReadAllText(Path.Combine(directory.Path, "config.json")) && visibility.Layout == savedLayout,
                    "Programmatic window changes cannot overwrite user proportions");
                await first.RestoreAsync(pid, state, CancellationToken.None);
                Check(visibility.Placement == saved, "Restore recovers the preference after an automatic bounds change");
                visibility.CycleMode();
                await Task.Delay(200);
                Check(contents == File.ReadAllText(Path.Combine(directory.Path, "config.json")), "Hiding cannot overwrite remembered geometry");
                await first.RestoreAsync(pid, state with { VideoWidth = 1280, VideoHeight = 1024 }, CancellationToken.None);
                Check(visibility.Placement == saved, "Automatic following retains custom dimensions even when the video's aspect changes");
                await video.ExecuteAsync(session.Page, new("ensurePipClosed"));
                await first.ObserveAsync(pid, false, CancellationToken.None);
                Check(store.Load().Settings.PipLayout == savedLayout, "Closing native PiP cannot save its off-screen source coordinates");
            }

            var nextStore = new JsonSettingsStore(Path.Combine(directory.Path, "config.json"));
            var nextSettings = nextStore.Load().Settings;
            using var next = new WindowsPipController(nextSettings, nextStore) { HelperWindow = helper };
            var reopened = await video.ExecuteAsync(session.Page, new("ensurePip"));
            await next.RestoreAsync(pid, reopened, CancellationToken.None);
            Check(next.MouseVisibility.Placement == saved, "A new controller restores layout loaded from disk");
            await next.RestoreAsync(pid, reopened, CancellationToken.None);
            Check(next.MouseVisibility.Placement == saved, "Repeated automatic placement never resets custom layout");

            nextSettings.PipLayout = savedLayout with { MonitorDevice = "removed-display" };
            await next.RestoreAsync(pid, reopened, CancellationToken.None);
            var recovered = next.MouseVisibility.Placement!;
            var nearest = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(next.MouseVisibility.FrameWindowHandle, 2), ref nearest);
            Check(recovered.Left >= nearest.Work.Left && recovered.Top >= nearest.Work.Top &&
                recovered.Left + recovered.Width <= nearest.Work.Right && recovered.Top + recovered.Height <= nearest.Work.Bottom,
                "A removed display or off-screen setting restores inside a visible work area");

            await next.PlaceAsync(pid, reopened, CancellationToken.None);
            var reset = next.MouseVisibility.Placement!;
            var resetMargin = ScreenLayout.Margin(nextSettings.PipMargin, DisplayMetrics.ForWindow(helper).Dpi);
            Check(reset.Left == monitor.Work.Left + resetMargin && reset.Top + reset.Height == monitor.Work.Bottom - resetMargin,
                "Explicit bottom-left reset overrides the remembered position");
            Check(nextStore.Load().Settings.PipLayout == next.MouseVisibility.Layout, "Explicit reset is remembered for the next launch");
            Console.WriteLine("PiP layout: factory defaults, real drag/resize save, hide/close isolation, disk reload, automatic restore, display recovery and reset passed.");
        }
        finally { SetCursorPos(originalCursor.X, originalCursor.Y); }
    }

    private static ulong CaptureEmbeddedFrame(nint window, string root, string name)
    {
        GetWindowRect(window, out var bounds);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        bitmap.Save(Path.Combine(root, "artifacts", name), System.Drawing.Imaging.ImageFormat.Png);
        var colors = new HashSet<int>();
        ulong signature = 14695981039346656037;
        for (int y = height / 3; y < height * 3 / 4; y += 8)
            for (int x = width / 5; x < width * 4 / 5; x += 8)
            {
                var color = bitmap.GetPixel(x, y).ToArgb();
                colors.Add(color);
                signature = unchecked((signature ^ (uint)color) * 1099511628211);
            }
        Check(colors.Count > 8, "Embedded native video actually renders; it is not a blank/black surface");
        return signature;
    }
}
