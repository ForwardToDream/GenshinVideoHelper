using System.ComponentModel;
using System.Runtime.InteropServices;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App.Native;

public sealed record DisplayMetrics(DisplayArea Monitor, DisplayArea WorkArea, uint Dpi, string DeviceName)
{
    public double DpiScale => ScreenLayout.DpiScale(Dpi);

    public static DisplayMetrics ForWindow(nint window) => Read(MonitorFromWindow(window, 2), window);

    public static DisplayMetrics ForLayout(PipLayout layout, nint window)
    {
        nint selected = 0;
        if (layout.MonitorDevice is { Length: > 0 } device)
            EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect bounds, nint data) =>
            {
                if (!string.Equals(Read(monitor, window).DeviceName, device, StringComparison.OrdinalIgnoreCase)) return true;
                selected = monitor;
                return false;
            }, 0);
        return Read(selected != 0 ? selected : MonitorFromWindow(window, 2), window);
    }

    private static DisplayMetrics Read(nint monitor, nint window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var dpi = window == 0 ? GetDpiForSystem() : GetDpiForWindow(window);
        return new(info.Monitor.Area, info.Work.Area, Math.Max(96, dpi), info.Device);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly DisplayArea Area => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
