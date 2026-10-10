using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.App;

// A passive companion to Chrome's native PiP. It never activates or intercepts game input.
public partial class PipFrameWindow : Window
{
    public const double HeightDip = 24;
    private readonly nint _handle;
    private (int X, int Y, int Width, int Height)? _placement;
    public nint WindowHandle => _handle;

    public PipFrameWindow()
    {
        InitializeComponent();
        _handle = new WindowInteropHelper(this).EnsureHandle();
        const int exStyle = -20, toolWindow = 0x80, noActivate = 0x08000000, transparent = 0x20;
        SetWindowLong(_handle, exStyle, GetWindowLong(_handle, exStyle) | toolWindow | noActivate | transparent);
    }

    public void Follow(nint pip, int left, int top, int width, PipVisibilityMode mode)
    {
        LockIcon.Visibility = mode == PipVisibilityMode.AlwaysVisible ? Visibility.Visible : Visibility.Collapsed;
        CircleIcon.Visibility = mode == PipVisibilityMode.AlwaysHidden ? Visibility.Visible : Visibility.Collapsed;
        MouseIcon.Visibility = mode == PipVisibilityMode.Automatic ? Visibility.Visible : Visibility.Collapsed;
        Title = mode switch
        {
            PipVisibilityMode.AlwaysVisible => "画中画 · 锁定显示",
            PipVisibilityMode.AlwaysHidden => "画中画 · 强制隐藏",
            _ => "画中画 · 鼠标避让"
        };
        var height = (int)Math.Round(HeightDip * GetDpiForWindow(pip) / 96d);
        var placement = (left, top - height, width, height);
        // Position before showing to avoid a flash at the default coordinates.
        if (_placement != placement || !IsVisible)
        {
            SetWindowPos(_handle, new nint(-1), left, top - height, width, height, 0x0010);
            _placement = placement;
        }
        if (!IsVisible) Show();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
