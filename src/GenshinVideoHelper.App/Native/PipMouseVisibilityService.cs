using System.Runtime.InteropServices;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.App.Native;

// Transparency only: never opens/shows PiP, changes playback, moves the window or takes focus.
public sealed class PipMouseVisibilityService : IDisposable
{
    private const int ExtendedStyle = -20;
    private const int Layered = 0x00080000;
    private const int Transparent = 0x00000020;
    private const uint Alpha = 0x00000002;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly bool _pollAutomatically;
    private PipFrameWindow? _frame;
    private nint _window;
    private int _originalExtendedStyle;
    private uint _originalColorKey, _originalLayeredFlags;
    private byte _originalAlpha;
    private bool _hiddenByUs;
    private bool _disposed;
    public int BrowserProcessId { get; private set; }
    public bool IsTemporarilyHidden => _hiddenByUs;
    public nint WindowHandle => _window;
    public PipVisibilityMode Mode { get; private set; } = PipVisibilityMode.Automatic;
    public nint FrameWindowHandle => _frame?.WindowHandle ?? 0;

    public PipMouseVisibilityService(bool pollAutomatically = true)
    {
        _pollAutomatically = pollAutomatically;
        _timer.Tick += (_, _) => Refresh();
    }

    public bool CycleMode()
    {
        Refresh();
        if (_disposed || _window == 0) return false;
        Mode = Mode switch
        {
            PipVisibilityMode.Automatic => PipVisibilityMode.AlwaysVisible,
            PipVisibilityMode.AlwaysVisible => PipVisibilityMode.AlwaysHidden,
            _ => PipVisibilityMode.Automatic
        };
        AppLog.Info("Pip", $"浮窗显隐模式：{Mode}。");
        Refresh();
        return true;
    }

    public void TrackBrowser(int processId)
    {
        if (_disposed || processId <= 0) return;
        if (BrowserProcessId != processId)
        {
            Suspend();
            BrowserProcessId = processId;
            AppLog.Info("Pip", $"开始跟踪 PID {processId} 的浮窗显隐。");
        }
        if (_pollAutomatically) _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || BrowserProcessId <= 0) return;
        if (!PipWindowService.IsPipWindow(_window, BrowserProcessId))
        {
            // Chrome may keep its native window after PiP closes. Restore opacity only,
            // then discard it; showing that retained window would undo the user's close.
            Restore();
            _window = PipWindowService.FindPip(BrowserProcessId);
        }
        if (_window == 0 || !GetWindowRect(_window, out var bounds))
        {
            _frame?.Hide();
            return;
        }
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) { _frame?.Hide(); return; }
        // Include the new top bar in the pointer-avoidance area; use the native window's DPI.
        var frameHeight = (int)Math.Round(PipFrameWindow.HeightDip * GetDpiForWindow(_window) / 96d);
        var inside = GetCursorPos(out var cursor) &&
                     cursor.X >= bounds.Left - width * 0.2 && cursor.X < bounds.Right + width * 0.2 &&
                     cursor.Y >= bounds.Top - frameHeight - height * 0.2 && cursor.Y < bounds.Bottom + height * 0.2;
        var hide = Mode == PipVisibilityMode.AlwaysHidden || Mode == PipVisibilityMode.Automatic && inside;
        if (hide) { if (!_hiddenByUs) Hide(); }
        else if (_hiddenByUs) Restore();

        if (hide) _frame?.Hide();
        else
        {
            _frame ??= new PipFrameWindow();
            _frame.Follow(_window, bounds.Left, bounds.Top, width, Mode);
        }
    }

    private void Hide()
    {
        _originalExtendedStyle = GetWindowLong(_window, ExtendedStyle);
        if ((_originalExtendedStyle & Layered) != 0 && !GetLayeredWindowAttributes(_window,
                out _originalColorKey, out _originalAlpha, out _originalLayeredFlags)) return;
        if (SetWindowLong(_window, ExtendedStyle, _originalExtendedStyle | Layered | Transparent) == 0) return;
        if (!SetLayeredWindowAttributes(_window, 0, 0, Alpha))
        {
            SetWindowLong(_window, ExtendedStyle, _originalExtendedStyle);
            return;
        }
        _hiddenByUs = true;
        AppLog.Debug("Pip", $"浮窗 0x{_window:X} 临时隐藏。");
    }

    public void Suspend()
    {
        _timer.Stop();
        _frame?.Hide();
        Restore();
        if (BrowserProcessId != 0) AppLog.Info("Pip", "停止跟踪浮窗显隐。");
        _window = 0;
        BrowserProcessId = 0;
    }

    private void Restore()
    {
        if (_hiddenByUs && PipWindowService.IsPipWindow(_window, BrowserProcessId, includeHidden: true))
        {
            if ((_originalExtendedStyle & Layered) != 0)
                SetLayeredWindowAttributes(_window, _originalColorKey, _originalAlpha, _originalLayeredFlags);
            else
                SetLayeredWindowAttributes(_window, 0, 255, Alpha);
            // Preserve any other style changes Chrome made while the window was transparent.
            var current = GetWindowLong(_window, ExtendedStyle);
            SetWindowLong(_window, ExtendedStyle,
                (current & ~(Layered | Transparent)) | (_originalExtendedStyle & (Layered | Transparent)));
        }
        if (_hiddenByUs) AppLog.Debug("Pip", $"浮窗 0x{_window:X} 恢复显示。");
        _hiddenByUs = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Suspend();
        _disposed = true;
        _frame?.Close();
        _frame = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point cursor);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
