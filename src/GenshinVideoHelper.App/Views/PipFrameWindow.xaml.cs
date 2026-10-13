using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using static GenshinVideoHelper.App.Native.PipVideoSurface;

namespace GenshinVideoHelper.App;

// Chrome supplies the live surface; one application window owns all visible UI and movement.
public partial class PipFrameWindow : Window
{
    public const double ReferenceBarHeight = ScreenLayout.PipBarHeight;
    public const double ReferenceBorder = ScreenLayout.PipBorder;
    private readonly nint _handle;
    private readonly WindowDisplayLayout _displayLayout;
    private PipVideoSurface? _video;
    private (int Width, int Height, NativeBounds Slot)? _emptyRegion;
    private bool _applyingRegion;
    private bool _hidden;
    private bool _applyingPlacement;
    private bool _userChanging;
    private PipPlacement? _observedPlacement;
    private PipLayout? _layout, _pendingLayout;
    private readonly DispatcherTimer _placementTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(150) };
    private PipVisibilityMode? _mode;
    public nint WindowHandle => _handle;
    public nint VideoWindowHandle => _video?.VideoWindow ?? 0;
    public bool IsVideoEmbedded => _video?.IsAttached == true;
    public PipLayout? Layout => _layout;
    public event Action<PipLayout>? LayoutChanged;
    public event Action? CloseRequested;
    public PipPlacement? Placement
    {
        get
        {
            if (!GetWindowRect(_handle, out var bounds)) return null;
            return new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }
    }

    public PipFrameWindow()
    {
        InitializeComponent();
        SetVisibilityShortcut(HotkeyBindings.Defaults()[HotkeyAction.ReversePipVisibility]);
        _displayLayout = new(this, resizeWindow: false);
        _handle = new WindowInteropHelper(this).EnsureHandle();
        _displayLayout.DisplayChanged += DisplayChanged;
        SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x80 | 0x08000000);
        HwndSource.FromHwnd(_handle)!.AddHook(WindowMessages);
        _placementTimer.Tick += (_, _) => FlushPlacement();
        VideoSlot.SizeChanged += (_, _) => UpdateVideo();
    }

    public void Attach(nint pip, PipLayout? layout = null)
    {
        if (VideoWindowHandle == pip) return;
        ReleaseVideo();
        if (!GetWindowRect(pip, out var bounds)) throw new InvalidOperationException("画中画窗口已关闭。");
        if (_layout is null)
        {
            var display = DisplayMetrics.ForWindow(_handle);
            var border = ScreenLayout.Pixels(ReferenceBorder, display.Dpi);
            var bar = ScreenLayout.Pixels(ReferenceBarHeight, display.Dpi);
            ApplyBounds(bounds.Left - border, bounds.Top - bar - border,
                bounds.Right - bounds.Left + border * 2, bounds.Bottom - bounds.Top + bar + border * 2);
        }
        try
        {
            _applyingPlacement = true;
            _video = new PipVideoSurface(pip, _handle);
            PipWindowService.RegisterFrame(pip, this);
            if (layout is { IsValid: true }) RestoreLayout(layout);
            _hidden = false;
            Show();
            UpdateLayout();
            UpdateVideo();
        }
        catch { ReleaseVideo(); throw; }
        finally { _applyingPlacement = false; _observedPlacement = Placement; _layout ??= CaptureLayout(); }
    }

    public void Place(PipPlacement placement)
    {
        ApplyBounds(placement.Left, placement.Top, placement.Width, placement.Height);
        _layout = CaptureLayout();
    }

    public void RestoreLayout(PipLayout layout)
    {
        if (!layout.IsValid) return;
        _layout = layout;
        RestoreOnDisplay(layout, DisplayMetrics.ForLayout(layout, _handle));
    }

    private void RestoreOnDisplay(PipLayout layout, DisplayMetrics display)
    {
        var restored = ScreenLayout.Restore(layout, display.WorkArea, display.Dpi);
        ApplyBounds(restored.Left, restored.Top, restored.Width, restored.Height);
        // Moving the HWND lets WPF adopt the target monitor's actual DPI before final sizing.
        var actual = DisplayMetrics.ForWindow(_handle);
        if (actual == display) return;
        restored = ScreenLayout.Restore(layout, actual.WorkArea, actual.Dpi);
        ApplyBounds(restored.Left, restored.Top, restored.Width, restored.Height);
    }

    private void DisplayChanged(DisplayMetrics previous, DisplayMetrics current)
    {
        if (_applyingPlacement || _layout is not { } layout) return;
        if (_userChanging && previous.DeviceName != current.DeviceName && GetWindowRect(_handle, out var bounds))
        {
            var resized = ScreenLayout.Restore(layout, current.WorkArea, current.Dpi);
            // A drag onto another display keeps the pointer's new position, while resizing proportionally.
            resized = resized with { Left = Math.Clamp(bounds.Left, current.WorkArea.Left, current.WorkArea.Right - resized.Width),
                Top = Math.Clamp(bounds.Top, current.WorkArea.Top, current.WorkArea.Bottom - resized.Height) };
            ApplyBounds(resized.Left, resized.Top, resized.Width, resized.Height);
        }
        else RestoreOnDisplay(layout, current);
    }

    private void ApplyBounds(int left, int top, int width, int height)
    {
        FlushPlacement();
        var wasApplying = _applyingPlacement;
        _applyingPlacement = true;
        try
        {
            if (!SetWindowPos(_handle, new nint(-1), left, top, width, height, 0x0010))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _displayLayout.Refresh();
            var dpiScale = DisplayMetrics.ForWindow(_handle).DpiScale;
            Width = width / dpiScale;
            Height = height / dpiScale;
            UpdateLayout();
            UpdateVideo();
        }
        finally { _applyingPlacement = wasApplying; _observedPlacement = Placement; }
    }

    private void ObservePlacement()
    {
        if (!_userChanging || _applyingPlacement || _displayLayout.IsApplying ||
            Placement is not { IsValid: true } placement || placement == _observedPlacement) return;
        _observedPlacement = placement;
        _layout = _pendingLayout = CaptureLayout();
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    public void FlushPlacement()
    {
        _placementTimer.Stop();
        var layout = _pendingLayout;
        _pendingLayout = null;
        if (layout is not null) LayoutChanged?.Invoke(layout);
    }

    private PipLayout? CaptureLayout()
    {
        if (Placement is not { } placement) return null;
        var display = DisplayMetrics.ForWindow(_handle);
        return ScreenLayout.Remember(placement, display.WorkArea, display.Dpi, display.DeviceName);
    }

    public void SetVisibilityShortcut(string keys) => VisibilityShortcutText.Text = keys;

    public void SetMode(PipVisibilityMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        LockIcon.Visibility = mode == PipVisibilityMode.AlwaysVisible ? Visibility.Visible : Visibility.Collapsed;
        CircleIcon.Visibility = mode == PipVisibilityMode.AlwaysHidden ? Visibility.Visible : Visibility.Collapsed;
        MouseIcon.Visibility = mode == PipVisibilityMode.Automatic ? Visibility.Visible : Visibility.Collapsed;
        Title = mode switch
        {
            PipVisibilityMode.AlwaysVisible => "画中画 · 锁定显示",
            PipVisibilityMode.AlwaysHidden => "画中画 · 强制隐藏",
            _ => "画中画 · 鼠标避让"
        };
    }

    public bool SetTemporarilyHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden) { UpdateVideo(); Hide(); }
        else { Show(); UpdateLayout(); UpdateVideo(); }
        return true;
    }

    private void UpdateVideo()
    {
        if (_applyingRegion || PresentationSource.FromVisual(VideoSlot) is null || !GetWindowRect(_handle, out var frame)) return;
        var first = VideoSlot.PointToScreen(new Point());
        var last = VideoSlot.PointToScreen(new Point(VideoSlot.ActualWidth, VideoSlot.ActualHeight));
        var destination = new NativeBounds { Left = (int)Math.Round(first.X - frame.Left), Top = (int)Math.Round(first.Y - frame.Top),
            Right = (int)Math.Round(last.X - frame.Left), Bottom = (int)Math.Round(last.Y - frame.Top) };
        if (destination.Right <= destination.Left || destination.Bottom <= destination.Top) return;
        if (_video is null)
        {
            SetEmptyRegion(frame.Right - frame.Left, frame.Bottom - frame.Top, destination);
            return;
        }
        if (_emptyRegion is not null)
        {
            _applyingRegion = true;
            try
            {
                if (SetWindowRgn(_handle, 0, true) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _emptyRegion = null;
            }
            finally { _applyingRegion = false; }
        }
        var restore = new NativeBounds { Left = frame.Left + destination.Left, Top = frame.Top + destination.Top,
            Right = frame.Left + destination.Right, Bottom = frame.Top + destination.Bottom };
        _video.Update(destination, restore, !_hidden);
    }

    // A native hole is transparent and passes input through, without a layered WPF window or frame copies.
    private void SetEmptyRegion(int width, int height, NativeBounds slot)
    {
        var shape = (width, height, slot);
        if (_emptyRegion.Equals(shape)) return;
        var outer = CreateRectRgn(0, 0, width, height);
        var inner = CreateRectRgn(slot.Left, slot.Top, slot.Right, slot.Bottom);
        _applyingRegion = true;
        try
        {
            if (outer == 0 || inner == 0 || CombineRgn(outer, outer, inner, 4) == 0 || SetWindowRgn(_handle, outer, true) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            outer = 0; // Windows owns the region after SetWindowRgn succeeds.
            _emptyRegion = shape;
        }
        finally
        {
            _applyingRegion = false;
            if (inner != 0) DeleteObject(inner);
            if (outer != 0) DeleteObject(outer);
        }
    }

    public void ReleaseVideo(bool restoreVideo = true)
    {
        FlushPlacement();
        if (_video is null) return;
        PipWindowService.UnregisterFrame(_video.VideoWindow);
        _video.Release(restoreVideo);
        _video = null;
        UpdateVideo();
    }

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        if (GetCursorPos(out var cursor))
            SendMessage(_handle, 0x00A1, 2, (nint)((cursor.Y << 16) | (cursor.X & 0xffff)));
    }
    private void CloseVideo_Click(object sender, RoutedEventArgs e)
    {
        if (CloseRequested is { } close) close();
        else { CloseVideo(); Hide(); }
    }
    public void CloseVideo()
    {
        if (VideoWindowHandle != 0) PostMessage(VideoWindowHandle, 0x0010, 0, 0);
    }
    private nint WindowMessages(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x0231) _userChanging = true;
        if (message == 0x0047 && _displayLayout.Display == DisplayMetrics.ForWindow(_handle))
        { UpdateVideo(); ObservePlacement(); }
        if (message == 0x0232)
        {
            ObservePlacement();
            _userChanging = false;
            FlushPlacement();
        }
        return 0;
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseVideo();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
