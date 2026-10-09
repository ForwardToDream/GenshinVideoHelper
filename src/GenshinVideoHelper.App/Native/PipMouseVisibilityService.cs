using System.Runtime.InteropServices;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Settings;
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
    private HotkeyGesture? _reversalGesture = new HotkeyGesture(0, 0xC0, "~");
    private nint _window;
    private int _originalExtendedStyle;
    private uint _originalColorKey, _originalLayeredFlags;
    private byte _originalAlpha;
    private bool _hiddenByUs;
    private bool _disposed;
    public int BrowserProcessId { get; private set; }
    public bool IsTemporarilyHidden => _hiddenByUs;
    public nint WindowHandle => _window;

    public PipMouseVisibilityService(bool pollAutomatically = true)
    {
        _pollAutomatically = pollAutomatically;
        _timer.Tick += (_, _) => Refresh();
    }

    public void SetReversalBinding(HotkeyGesture? gesture)
    {
        _reversalGesture = gesture;
        Refresh();
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
        if (_window == 0 || !GetWindowRect(_window, out var bounds) || !GetCursorPos(out var cursor)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;
        // Cursor and window bounds use the same screen-coordinate API and current DPI context.
        var inside = cursor.X >= bounds.Left - width * 0.2 && cursor.X < bounds.Right + width * 0.2 &&
                     cursor.Y >= bounds.Top - height * 0.2 && cursor.Y < bounds.Bottom + height * 0.2;
        var reversalHeld = _reversalGesture is { } gesture && IsHeld(gesture);
        if (inside != reversalHeld)
        {
            if (!_hiddenByUs) Hide();
        }
        else if (_hiddenByUs) Restore();
    }

    private static bool IsHeld(HotkeyGesture gesture) =>
        IsDown((int)gesture.Key) &&
        ((gesture.Modifiers & 1) == 0 || IsDown(0x12)) &&
        ((gesture.Modifiers & 2) == 0 || IsDown(0x11)) &&
        ((gesture.Modifiers & 4) == 0 || IsDown(0x10)) &&
        ((gesture.Modifiers & 8) == 0 || IsDown(0x5B) || IsDown(0x5C));

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
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
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point cursor);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}