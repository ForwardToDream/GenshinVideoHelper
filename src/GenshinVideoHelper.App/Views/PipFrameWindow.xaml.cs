using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
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
    private PipVisibilityMode? _mode;
    public nint WindowHandle => _handle;
    public nint VideoWindowHandle => _video?.VideoWindow ?? 0;
    public bool IsVideoEmbedded => _video?.IsAttached == true;

    public PipFrameWindow()
    {
        InitializeComponent();
        _handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x80 | 0x08000000);
        HwndSource.FromHwnd(_handle)!.AddHook(WindowMessages);
    }

    public void Attach(nint pip)
    {
        if (VideoWindowHandle == pip) return;
        ReleaseVideo();
        if (!GetWindowRect(pip, out var bounds)) throw new InvalidOperationException("画中画窗口已关闭。");
        var scale = GetDpiForWindow(_handle) / 96d;
        var border = (int)Math.Round(BorderDip * scale);
        var bar = (int)Math.Round(HeightDip * scale);
        Width = (bounds.Right - bounds.Left) / scale + BorderDip * 2;
        Height = (bounds.Bottom - bounds.Top) / scale + HeightDip + BorderDip * 2;
        SetWindowPos(_handle, new nint(-1), bounds.Left - border, bounds.Top - bar - border,
            bounds.Right - bounds.Left + border * 2, bounds.Bottom - bounds.Top + bar + border * 2, 0x0010);
        try
        {
            _video = new PipVideoSurface(pip, _handle);
            PipWindowService.RegisterFrame(pip, this);
            _hidden = false;
            Show();
            UpdateLayout();
            UpdateVideo();
        }
        catch { ReleaseVideo(); Hide(); throw; }
    }

    public void Place(int left, int bottom, int videoWidth, int videoHeight)
    {
        var scale = GetDpiForWindow(_handle) / 96d;
        var border = (int)Math.Round(BorderDip * scale);
        var bar = (int)Math.Round(HeightDip * scale);
        var width = videoWidth + border * 2;
        var height = videoHeight + bar + border * 2;
        SetWindowPos(_handle, new nint(-1), left, bottom - height, width, height, 0x0010);
        UpdateLayout();
        UpdateVideo();
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
        if (message == 0x0047 || message == 0x02E0) UpdateVideo(); // position/size or monitor DPI
        return 0;
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseVideo();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
