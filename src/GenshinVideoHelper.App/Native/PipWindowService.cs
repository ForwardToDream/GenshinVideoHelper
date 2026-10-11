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

    public static async Task PlaceAsync(int browserProcessId, nint helperWindow, int widthDip,
        int marginDip, double aspectRatio, CancellationToken token = default, PipPlacement? placement = null)
    {
        var pip = await WaitForPipAsync(browserProcessId, token);

        var monitor = MonitorFromWindow(helperWindow, 2); // tool's monitor, nearest fallback
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var scale = GetDpiForWindow(helperWindow) / 96d;
        var work = info.Work;
        var margin = (int)Math.Round(marginDip * scale);
        var width = Math.Clamp((int)Math.Round(widthDip * scale), 200, Math.Max(200, (work.Right - work.Left) / 2));
        var ratio = double.IsFinite(aspectRatio) && aspectRatio is > 0.2 and < 5 ? aspectRatio : 16d / 9;
        var height = Math.Min((int)Math.Round(width / ratio), (work.Bottom - work.Top) / 2);
        if (FrameFor(pip) is { } frame)
        {
            if (placement is { IsValid: true }) frame.RestorePlacement(placement);
            else frame.Place(work.Left + margin, work.Bottom - margin, width, height);
            return;
        }
        if (!SetWindowPos(pip, new nint(-1), work.Left + margin, work.Bottom - margin - height,
                width, height, 0x0010)) // topmost, no activation; never reopen a closed native window
        {
            var error = Marshal.GetLastWin32Error();
            AppLog.Warn("Pip", $"放置浮窗失败，错误码 {error}。");
            throw new Win32Exception(error);
        }
        AppLog.Info("Pip", $"浮窗 0x{pip:X} 已放置：{work.Left + margin},{work.Bottom - margin - height} {width}×{height}，缩放 {scale:0.##}。");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
