using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App.Native;

public static class PipWindowService
{
    private static readonly Dictionary<nint, PipFrameWindow> Frames = [];
    internal static void RegisterFrame(nint pip, PipFrameWindow frame) { lock (Frames) Frames[pip] = frame; }
    internal static void UnregisterFrame(nint pip) { lock (Frames) Frames.Remove(pip); }
    private static PipFrameWindow? FrameFor(nint pip) { lock (Frames) return Frames.GetValueOrDefault(pip); }

    public static async Task PlaceAsync(int browserProcessId, nint helperWindow, int referenceWidth,
        int referenceMargin, double aspectRatio, CancellationToken token = default, PipLayout? layout = null)
    {
        var pip = await WaitForPipAsync(browserProcessId, token);

        var display = DisplayMetrics.ForWindow(helperWindow);
        var bounds = ScreenLayout.DefaultPip(display.WorkArea, display.Dpi, referenceWidth, referenceMargin, aspectRatio);
        if (FrameFor(pip) is { } frame)
        {
            if (layout is { IsValid: true }) frame.RestoreLayout(layout);
            else frame.Place(bounds);
            var placedDisplay = DisplayMetrics.ForWindow(frame.WindowHandle);
            if (frame.Placement is { } actual)
                AppLog.Info("Pip", $"外框 {actual.Left},{actual.Top} {actual.Width}×{actual.Height}，" +
                    $"{(layout is { IsValid: true } ? "保存" : "默认")}布局，工作区 {placedDisplay.WorkArea.Width}×{placedDisplay.WorkArea.Height}，DPI {placedDisplay.Dpi}。");
            return;
        }
        var width = bounds.Width - ScreenLayout.Pixels(1, display.Dpi) * 2;
        var height = bounds.Height - ScreenLayout.Pixels(24, display.Dpi) - ScreenLayout.Pixels(1, display.Dpi) * 2;
        var bottom = bounds.Top + bounds.Height;
        if (!SetWindowPos(pip, new nint(-1), bounds.Left, bottom - height,
                width, height, 0x0010)) // topmost, no activation; never reopen a closed native window
        {
            var error = Marshal.GetLastWin32Error();
            AppLog.Warn("Pip", $"放置浮窗失败，错误码 {error}。");
            throw new Win32Exception(error);
        }
        AppLog.Info("Pip", $"浮窗 0x{pip:X} 已放置：{bounds.Left},{bottom - height} {width}×{height}，DPI {display.Dpi}。");
    }

    internal static async Task<nint> WaitForPipAsync(int browserProcessId, CancellationToken token)
    {
        nint pip = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            pip = FindPip(browserProcessId);
            if (pip != 0) break;
            await Task.Delay(100, token);
        }
        if (pip == 0) AppLog.Warn("Pip", $"2 秒内未找到 PID {browserProcessId} 的画中画窗口。");
        if (pip == 0)
            throw new InvalidOperationException("画中画已开启，但未找到浮窗。可手动拖到左下角，或点击“放回左下角”重试。");

        return pip;
    }

    public static nint FindPip(int browserProcessId, bool includeHidden = false)
    {
        nint[] embedded;
        lock (Frames) embedded = Frames.Keys.ToArray();
        foreach (var window in embedded)
            if (IsPipWindow(window, browserProcessId, includeHidden)) return window;
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsPipWindow(window, browserProcessId, includeHidden)) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    public static bool IsPipWindow(nint window, int browserProcessId, bool includeHidden = false)
    {
        if (window == 0 || !IsWindow(window) || (!includeHidden && !IsWindowVisible(window))) return false;
        GetWindowThreadProcessId(window, out var process);
        if (process != browserProcessId) return false;
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() != "Chrome_WidgetWin_1" ||
            ((GetWindowLong(window, -20) & 0x00000008) == 0 && FrameFor(window) is null)) return false;
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        var caption = title.ToString();
        return caption.Contains("画中画", StringComparison.Ordinal) ||
               caption.Contains("子母畫面", StringComparison.Ordinal) ||
               caption.Contains("Picture in picture", StringComparison.OrdinalIgnoreCase) ||
               caption.Contains("Picture-in-Picture", StringComparison.OrdinalIgnoreCase);
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
