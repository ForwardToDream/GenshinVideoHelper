using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GenshinVideoHelper.App.Native;

/// <summary>WPF owns DPI scaling; this adapter keeps windows within the current work area.</summary>
public sealed class WindowDisplayLayout
{
    private readonly Window _window;
    private readonly bool _resizeWindow;
    private readonly double _minimumWidth, _minimumHeight;
    private double _preferredWidth, _preferredHeight;
    private bool _queued, _closed;
    private nint _handle;
    private HwndSource? _source;
    public bool IsApplying { get; private set; }
    public DisplayMetrics? Display { get; private set; }
    public event Action<DisplayMetrics, DisplayMetrics>? DisplayChanged;

    public WindowDisplayLayout(Window window, bool resizeWindow = true)
    {
        _window = window;
        _resizeWindow = resizeWindow;
        _preferredWidth = window.Width;
        _preferredHeight = window.Height;
        _minimumWidth = window.MinWidth;
        _minimumHeight = window.MinHeight;
        window.SourceInitialized += Initialize;
        window.Closed += (_, _) => { _closed = true; _source?.RemoveHook(Messages); };
    }

    private void Initialize(object? sender, EventArgs args)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(Messages);
        Refresh();
    }

    public void Refresh() { if (!_closed && _handle != 0) Apply(DisplayMetrics.ForWindow(_handle)); }

    // Tests can supply synthetic monitor/DPI combinations without changing desktop settings.
    public void Apply(DisplayMetrics display)
    {
        if (_closed || Display == display) return;
        var previous = Display;
        Display = display;
        var wasApplying = IsApplying;
        IsApplying = true;
        try
        {
            var availableWidth = display.WorkArea.Width / display.DpiScale;
            var availableHeight = display.WorkArea.Height / display.DpiScale;
            _window.MinWidth = Math.Min(_minimumWidth, availableWidth);
            _window.MinHeight = Math.Min(_minimumHeight, availableHeight);
            _window.MaxWidth = availableWidth;
            _window.MaxHeight = availableHeight;
            if (_resizeWindow && _window.WindowState == WindowState.Normal)
            {
                _window.Width = Math.Clamp(_preferredWidth, _window.MinWidth, availableWidth);
                _window.Height = Math.Clamp(_preferredHeight, _window.MinHeight, availableHeight);
                if (_handle != 0 && GetWindowRect(_handle, out var bounds))
                {
                    var width = Math.Min(display.WorkArea.Width, (int)Math.Round(_window.Width * display.DpiScale));
                    var height = Math.Min(display.WorkArea.Height, (int)Math.Round(_window.Height * display.DpiScale));
                    if (!SetWindowPos(_handle, 0, Math.Clamp(bounds.Left, display.WorkArea.Left, display.WorkArea.Right - width),
                        Math.Clamp(bounds.Top, display.WorkArea.Top, display.WorkArea.Bottom - height), width, height, 0x0014))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            _window.UpdateLayout();
            if (previous is not null) DisplayChanged?.Invoke(previous, display);
        }
        finally { IsApplying = wasApplying; }
    }

    private nint Messages(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0232 && _resizeWindow && !IsApplying && _window.WindowState == WindowState.Normal)
        {
            _preferredWidth = _window.ActualWidth;
            _preferredHeight = _window.ActualHeight;
        }
        // WPF handles WM_DPICHANGED first. Work-area adjustments never become user preferences.
        if (!_queued && message is 0x02E0 or 0x007E or 0x001A or 0x0047)
        {
            _queued = true;
            _window.Dispatcher.BeginInvoke(() => { _queued = false; Refresh(); }, DispatcherPriority.Loaded);
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
