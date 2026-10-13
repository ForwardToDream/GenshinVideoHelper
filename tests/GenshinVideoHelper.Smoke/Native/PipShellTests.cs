using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class PipShellTests
{
    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string root)
    {
        using var directory = TestArtifacts.CreateTemp(root, "pip-shell");
        var store = new JsonSettingsStore(Path.Combine(directory.Path, "config.json"));
        var settings = store.Load().Settings;
        settings.IsFirstLaunch = false; settings.HotkeysEnabled = false; settings.Vision.Enabled = false;
        MainWindow Create(AppSettings options) => new(new AppServices(options, Path.Combine(directory.Path, "Chrome"), store,
            episodeFactory: PreviewService)) { ShowActivated = false, ShowInTaskbar = false };
        var window = Create(settings);
        var visibility = window.Services.Pip.MouseVisibility;
        PipPlacement saved;
        try
        {
            var foreground = GetForegroundWindow();
            window.Show();
            await Settle();
            Check(visibility.IsOpen && visibility.FrameWindowHandle != 0 && visibility.WindowHandle == 0 &&
                visibility.BrowserProcessId == 0 && window.Services.Follow.Current.Page is null, "Startup opens an independent frame without a browser video");
            Check((string)((Button)window.FindName("PipButton")).Content == "关闭画中画", "Main button reflects the frame state");
            Check(visibility.CycleMode() && visibility.Mode == PipVisibilityMode.AlwaysVisible, "Empty frame supports mode hotkey action");
            Check(GetForegroundWindow() == foreground, "Startup shell does not steal focus");
            CheckHole(visibility.FrameWindowHandle);
            var display = DisplayMetrics.ForWindow(new WindowInteropHelper(window).Handle);
            var initial = ScreenLayout.DefaultPip(display.WorkArea, display.Dpi, settings.PipWidth, settings.PipMargin, 16d / 9);
            Check(visibility.Placement == initial, "Startup shell uses the same resolution/DPI-aware defaults as video");
            Capture(window, root, "ui-pip-shell-startup.png", 1020, 730);

            var frameWindow = Application.Current.Windows.OfType<PipFrameWindow>().Single();
            SendMessage(visibility.FrameWindowHandle, 0x0231, 0, 0);
            Check(SetWindowPos(visibility.FrameWindowHandle, new nint(-1), initial.Left + 60, initial.Top - 40,
                initial.Width + 40, initial.Height + 20, 0x0010), "Move and resize only the owned empty frame");
            SendMessage(visibility.FrameWindowHandle, 0x0232, 0, 0);
            await Settle();
            CheckHole(visibility.FrameWindowHandle);
            saved = visibility.Placement!;
            Check(store.Load().Settings.PipLayout == visibility.Layout && settings.PipLayout is not null,
                "Moving/resizing without video persists the same layout");
            ((Button)frameWindow.FindName("CloseVideoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Check(!visibility.IsOpen && !frameWindow.IsVisible && !visibility.CycleMode(), "Header closes an empty frame and mode cycling cannot reopen it");
            await window.Services.Pip.ObserveAsync(0, false, CancellationToken.None);
            visibility.Refresh();
            Check(!frameWindow.IsVisible, "Polling never reopens a user-closed frame");
            await (Task)Invoke(window, "TogglePipAsync")!;
            Check(visibility.IsOpen && visibility.Placement == saved, "Main toggle reopens the saved empty shell without following");
            CheckHole(visibility.FrameWindowHandle);
            visibility.Suspend();
            Check(visibility.IsOpen && visibility.Placement == saved, "Missing media detaches video without closing or moving the shell");
            await (Task)Invoke(window, "PlacePipAsync")!;
            Check(visibility.Placement == ScreenLayout.DefaultPip(display.WorkArea, display.Dpi, settings.PipWidth, settings.PipMargin,
                saved.Width > 0 ? visibility.Layout!.AspectRatio : 16d / 9), "Empty frame can be reset without a follow session");
            saved = visibility.Placement!;
            CheckHole(visibility.FrameWindowHandle);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
        Check(visibility.FrameWindowHandle == 0, "Closing the assistant destroys its empty frame");
        var restored = Create(store.Load().Settings);
        try
        {
            restored.Show(); await Settle();
            Check(restored.Services.Pip.MouseVisibility.IsOpen && restored.Services.Pip.MouseVisibility.Placement == saved,
                "A new application window opens the shell at its persisted layout");
            CheckHole(restored.Services.Pip.MouseVisibility.FrameWindowHandle);
        }
        finally { restored.Close(); await restored.ShutdownCompletion; }
        Console.WriteLine("PiP shell: automatic startup, native transparent hole, input pass-through, no activation, mode, close/reopen, reset, layout memory and disposal passed.");
    }

    private static async Task Settle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void CheckHole(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Check(GetWindowRect(window, out var bounds) && GetWindowRgn(window, region) == 3, "Empty shell has a native ring-shaped region");
            var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
            Check(!PtInRegion(region, width / 2, height / 2) && PtInRegion(region, width / 2, 4), "Interior is excluded while the header remains drawn");
            Check(WindowFromPoint(new ScreenPoint { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 }) != window,
                "Empty interior passes hit testing to the underlying window");
            Check((GetWindowLong(window, -20) & 0x80000) == 0, "Empty shell stays a normal GPU-composited window");
        }
        finally { DeleteObject(region); }
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
