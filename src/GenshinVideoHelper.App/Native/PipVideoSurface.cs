using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GenshinVideoHelper.App.Native;

// DWM composites Chrome's live GPU surface into our window without CPU frame copies.
// Chrome keeps its top-level HWND: reparenting its DirectComposition target breaks rendering.
internal sealed class PipVideoSurface : IDisposable
{
    private const uint NoActivate = 0x0010, AsyncPosition = 0x4000;
    private nint _thumbnail;
    private readonly bool _wasTopmost;
    private NativeBounds _restoreBounds;
    private (int Width, int Height) _size;
    public nint VideoWindow { get; }
    public bool IsAttached => _thumbnail != 0;

    public PipVideoSurface(nint video, nint destination)
    {
        VideoWindow = video;
        if (!GetWindowRect(video, out _restoreBounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _wasTopmost = (GetWindowLong(video, -20) & 8) != 0;
        _size = (_restoreBounds.Right - _restoreBounds.Left, _restoreBounds.Bottom - _restoreBounds.Top);
        Marshal.ThrowExceptionForHR(DwmRegisterThumbnail(destination, video, out _thumbnail));
        if (!SetWindowPos(video, new nint(-2), -30000, -30000, _size.Width, _size.Height, NoActivate | AsyncPosition))
        {
            var error = Marshal.GetLastWin32Error();
            DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = 0;
            throw new Win32Exception(error);
        }
    }

    public void Update(NativeBounds destination, NativeBounds restoreBounds, bool visible)
    {
        if (_thumbnail == 0) return;
        _restoreBounds = restoreBounds;
        var size = (destination.Right - destination.Left, destination.Bottom - destination.Top);
        if (size != _size && size.Item1 > 1 && size.Item2 > 1)
        {
            _size = size;
            SetWindowPos(VideoWindow, new nint(-2), -30000, -30000, size.Item1, size.Item2, NoActivate | AsyncPosition);
        }
        var properties = new ThumbnailProperties
        {
            Flags = 1 | 4 | 8 | 16, Destination = destination, Opacity = 255,
            Visible = visible, ClientAreaOnly = true
        };
        Marshal.ThrowExceptionForHR(DwmUpdateThumbnailProperties(_thumbnail, ref properties));
    }

    public void Dispose() => Release(restoreVideo: true);

    public void Release(bool restoreVideo)
    {
        if (_thumbnail == 0) return;
        DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
        // No SHOWWINDOW flag: a source Chrome has closed must remain closed.
        if (restoreVideo && IsWindow(VideoWindow))
            SetWindowPos(VideoWindow, _wasTopmost ? new nint(-1) : new nint(-2),
                _restoreBounds.Left, _restoreBounds.Top, _restoreBounds.Right - _restoreBounds.Left,
                _restoreBounds.Bottom - _restoreBounds.Top, NoActivate | AsyncPosition);
    }

    [StructLayout(LayoutKind.Sequential)] internal struct NativeBounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeBounds Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientAreaOnly;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeBounds bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
