using System.Runtime.InteropServices;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App.Native;

// Keep Chrome playing while the single visible frame is shown or hidden.
public sealed class PipMouseVisibilityService : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly bool _pollAutomatically;
    private readonly Func<PipLayout?>? _getLayout;
    private readonly Action<PipLayout>? _layoutChanged;
    private PipFrameWindow? _frame;
    private nint _window;
    private bool _hiddenByUs, _disposed;
    public int BrowserProcessId { get; private set; }
    public bool IsTemporarilyHidden => _hiddenByUs;
    public nint WindowHandle => _window;
    public PipVisibilityMode Mode { get; private set; } = PipVisibilityMode.Automatic;
    public nint FrameWindowHandle => _frame?.WindowHandle ?? 0;
    public PipPlacement? Placement => _frame?.Placement;
    public PipLayout? Layout => _frame?.Layout;
    public void FlushPlacement() => _frame?.FlushPlacement();

    public PipMouseVisibilityService(bool pollAutomatically = true, Func<PipLayout?>? getLayout = null,
        Action<PipLayout>? layoutChanged = null)
    {
        _pollAutomatically = pollAutomatically;
        _getLayout = getLayout;
        _layoutChanged = layoutChanged;
        _timer.Tick += (_, _) =>
        {
            try { Refresh(); }
            catch (Exception ex) { _timer.Stop(); AppLog.Warn("Pip", "浮窗嵌入失败，停止自动尝试。可点击放回左下角重试。", ex); }
        };
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
            ReleaseFrame();
            _window = PipWindowService.FindPip(BrowserProcessId);
        }
        if (_window == 0) return;
        if (_frame is null)
        {
            _frame = new PipFrameWindow();
            _frame.LayoutChanged += layout => _layoutChanged?.Invoke(layout);
        }
        if (_frame.VideoWindowHandle != _window)
        {
            try { _frame.Attach(_window, _getLayout?.Invoke()); }
            catch { _timer.Stop(); ReleaseFrame(); _window = 0; throw; }
            AppLog.Info("Pip", $"浮窗 0x{_window:X} 已嵌入统一外框。");
        }
        if (!GetWindowRect(_frame.WindowHandle, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;
        var inside = GetCursorPos(out var cursor) &&
                     cursor.X >= bounds.Left - width * 0.2 && cursor.X < bounds.Right + width * 0.2 &&
                     cursor.Y >= bounds.Top - height * 0.2 && cursor.Y < bounds.Bottom + height * 0.2;
        var hide = Mode == PipVisibilityMode.AlwaysHidden || Mode == PipVisibilityMode.Automatic && inside;
        _frame.SetMode(Mode);
        if (hide == _hiddenByUs) return;
        if (_frame.SetTemporarilyHidden(hide))
        {
            _hiddenByUs = hide;
            AppLog.Debug("Pip", hide ? "统一浮窗临时隐藏。" : "统一浮窗恢复显示。");
        }
    }

    private void ReleaseFrame(bool closeVideo = false)
    {
        _frame?.Hide();
        if (closeVideo && PipWindowService.IsPipWindow(_window, BrowserProcessId, includeHidden: true))
            _frame?.CloseVideo();
        _frame?.ReleaseVideo(restoreVideo: !closeVideo);
        _hiddenByUs = false;
    }

    public void Suspend() => StopTracking(closeVideo: false);

    public void CloseCurrent()
    {
        FlushPlacement();
        StopTracking(closeVideo: true);
    }

    private void StopTracking(bool closeVideo)
    {
        _timer.Stop();
        ReleaseFrame(closeVideo);
        if (BrowserProcessId != 0) AppLog.Info("Pip", "停止跟踪浮窗显隐。");
        _window = 0;
        BrowserProcessId = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopTracking(closeVideo: true);
        _disposed = true;
        _frame?.Close();
        _frame = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint cursor);
}
