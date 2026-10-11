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
    public const double HeightDip = 24;
    public const double BorderDip = 1;
    private readonly nint _handle;
    private PipVideoSurface? _video;
    private bool _hidden;
    private bool _applyingPlacement;
    private PipPlacement? _observedPlacement, _pendingPlacement;
    private readonly DispatcherTimer _placementTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(150) };
    private PipVisibilityMode? _mode;
    public nint WindowHandle => _handle;
    public nint VideoWindowHandle => _video?.VideoWindow ?? 0;
    public bool IsVideoEmbedded => _video?.IsAttached == true;
    public event Action<PipPlacement>? PlacementChanged;
    public PipPlacement? Placement => _video is not null && GetWindowRect(_handle, out var bounds)
        ? new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) : null;

    public PipFrameWindow()
    {
        InitializeComponent();
        _handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x80 | 0x08000000);
        HwndSource.FromHwnd(_handle)!.AddHook(WindowMessages);
        _placementTimer.Tick += (_, _) => FlushPlacement();
    }

    public void Attach(nint pip, PipPlacement? placement = null)
    {
        if (VideoWindowHandle == pip) return;
        ReleaseVideo();
        if (!GetWindowRect(pip, out var bounds)) throw new InvalidOperationException("画中画窗口已关闭。");
        var scale = GetDpiForWindow(_handle) / 96d;
        var border = (int)Math.Round(BorderDip * scale);
        var bar = (int)Math.Round(HeightDip * scale);
        Width = (bounds.Right - bounds.Left) / scale + BorderDip * 2;
        Height = (bounds.Bottom - bounds.Top) / scale + HeightDip + BorderDip * 2;
        ApplyBounds(bounds.Left - border, bounds.Top - bar - border,
            bounds.Right - bounds.Left + border * 2, bounds.Bottom - bounds.Top + bar + border * 2);
        try
        {
            _applyingPlacement = true;
            _video = new PipVideoSurface(pip, _handle);
            PipWindowService.RegisterFrame(pip, this);
            if (placement is { IsValid: true }) RestorePlacement(placement);
            _hidden = false;
            Show();
            UpdateLayout();
            UpdateVideo();
        }
        catch { ReleaseVideo(); Hide(); throw; }
        finally { _applyingPlacement = false; _observedPlacement = Placement; }
    }

    public void Place(int left, int bottom, int videoWidth, int videoHeight)
    {
        var scale = GetDpiForWindow(_handle) / 96d;
        var border = (int)Math.Round(BorderDip * scale);
        var bar = (int)Math.Round(HeightDip * scale);
        var width = videoWidth + border * 2;
        var height = videoHeight + bar + border * 2;
        ApplyBounds(left, bottom - height, width, height);
    }

    public void RestorePlacement(PipPlacement placement)
    {
        if (!placement.IsValid) return;
        var bounds = new NativeBounds { Left = placement.Left, Top = placement.Top,
            Right = placement.Left + placement.Width, Bottom = placement.Top + placement.Height };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref bounds, 2), ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var work = info.Work;
        var scale = Math.Max(96, GetDpiForWindow(_handle)) / 96d;
        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;
        var width = Math.Clamp(placement.Width, Math.Min(workWidth, (int)Math.Ceiling(MinWidth * scale)), workWidth);
        var height = Math.Clamp(placement.Height, Math.Min(workHeight, (int)Math.Ceiling(MinHeight * scale)), workHeight);
        ApplyBounds(Math.Clamp(placement.Left, work.Left, work.Right - width),
            Math.Clamp(placement.Top, work.Top, work.Bottom - height), width, height);
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
            UpdateLayout();
            UpdateVideo();
        }
        finally { _applyingPlacement = wasApplying; _observedPlacement = Placement; }
    }

    private void ObservePlacement()
    {
        if (_applyingPlacement || Placement is not { IsValid: true } placement || placement == _observedPlacement) return;
        _observedPlacement = _pendingPlacement = placement;
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    public void FlushPlacement()
    {
        _placementTimer.Stop();
        var placement = _pendingPlacement;
        _pendingPlacement = null;
        if (placement is not null) PlacementChanged?.Invoke(placement);
    }

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
        if (_video is null || !GetClientRect(_handle, out var client) || !GetWindowRect(_handle, out var frame)) return;
        var scale = GetDpiForWindow(_handle) / 96d;
        var border = (int)Math.Round(BorderDip * scale);
        var bar = (int)Math.Round(HeightDip * scale);
        var destination = new NativeBounds { Left = border, Top = border + bar, Right = client.Right - border, Bottom = client.Bottom - border };
        if (destination.Right <= destination.Left || destination.Bottom <= destination.Top) return;
        var restore = new NativeBounds { Left = frame.Left + destination.Left, Top = frame.Top + destination.Top,
            Right = frame.Left + destination.Right, Bottom = frame.Top + destination.Bottom };
        _video.Update(destination, restore, !_hidden);
    }

    public void ReleaseVideo(bool restoreVideo = true)
    {
        FlushPlacement();
        if (_video is null) return;
        PipWindowService.UnregisterFrame(_video.VideoWindow);
        _video.Release(restoreVideo);
        _video = null;
    }

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        if (GetCursorPos(out var cursor))
            SendMessage(_handle, 0x00A1, 2, (nint)((cursor.Y << 16) | (cursor.X & 0xffff)));
    }
    private void CloseVideo_Click(object sender, RoutedEventArgs e) => CloseVideo();
    public void CloseVideo()
    {
        if (VideoWindowHandle != 0) PostMessage(VideoWindowHandle, 0x0010, 0, 0);
    }
    private nint WindowMessages(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x0047 || message == 0x02E0) { UpdateVideo(); ObservePlacement(); } // position/size or monitor DPI
        if (message == 0x0232) FlushPlacement(); // a drag/resize gesture has ended
        return 0;
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseVideo();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeBounds Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeBounds bounds, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
