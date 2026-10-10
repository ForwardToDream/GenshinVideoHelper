using System.Diagnostics;
using System.Runtime.InteropServices;
using GenshinVideoHelper.Core.Vision;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GenshinVideoHelper.App.Native;

/// <summary>WGC captures only the selected HWND; GPU crops precede CPU readback. Worker-thread owned.</summary>
public sealed class GameWindowCapture : IGameFrameSource
{
    private nint _window;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private int _stageWidth,_stageHeight;
    private long _epoch;
    private bool _closed;
    public bool IsForeground(nint window) => window != 0 && GetForegroundWindow()==window && IsWindow(window) && !IsIconic(window);
    public ValueTask<VisionFrame?> CaptureAsync(nint window,CapturePlan plan,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!IsForeground(window))return ValueTask.FromResult<VisionFrame?>(null);
        if(_closed || _window!=window || _pool is null) { Reset(); Start(window); return ValueTask.FromResult<VisionFrame?>(null); }
        Direct3D11CaptureFrame? latest=null;
        try
        {
            // Drain the two-buffer pool to consume the freshest frame; never queue full textures.
            for(int i=0;i<3;i++){var next=_pool.TryGetNextFrame();if(next is null)break;latest?.Dispose();latest=next;}
            if(latest is null)return ValueTask.FromResult<VisionFrame?>(null);
            var size=latest.ContentSize;
            if(size.Width!=_item!.Size.Width || size.Height!=_item.Size.Height || size.Width<=0 || size.Height<=0)
            { Reset();return ValueTask.FromResult<VisionFrame?>(null); }
            if(!GetClientRect(window,out var client))return ValueTask.FromResult<VisionFrame?>(null);
            var origin=new NativePoint();ClientToScreen(window,ref origin);
            if(DwmGetWindowAttribute(window,9,out var bounds,Marshal.SizeOf<NativeRect>())!=0) GetWindowRect(window,out bounds);
            int ox=origin.X-bounds.Left,oy=origin.Y-bounds.Top,w=client.Right,h=client.Bottom;
            if(ox<0||oy<0||w<=0||h<=0||ox+w>size.Width||oy+h>size.Height)return ValueTask.FromResult<VisionFrame?>(null);
            string epoch=$"{_epoch}:{size.Width}:{size.Height}:{ox}:{oy}:{w}:{h}";
            var r=plan.Epoch==epoch && plan.Region is {} cached ? cached : new FrameRegion(0,0,w/2,h/2);
            if(r.X<0||r.Y<0||r.Width<1||r.Height<1||r.X+r.Width>w||r.Y+r.Height>h)return ValueTask.FromResult<VisionFrame?>(null);
            EnsureStaging(r.Width,r.Height);
            var access=latest.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var textureId=typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(access.GetInterface(ref textureId,out var ptr));
            using var source=new ID3D11Texture2D(ptr);
            _context!.CopySubresourceRegion(_staging!,0,0,0,0,source,0,
                new Vortice.Mathematics.Box(ox+r.X,oy+r.Y,0,ox+r.X+r.Width,oy+r.Y+r.Height,1));
            var mapped=_context.Map(_staging!,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
            // WGC SystemRelativeTime is the monotonic QPC time used by Stopwatch.
            long stamp=latest.SystemRelativeTime is {} timestamp ? (long)(timestamp.TotalSeconds*Stopwatch.Frequency) : Stopwatch.GetTimestamp();
            var frame=new VisionFrame(r.Width,r.Height,4,w,h,r,epoch,stamp);
            try
            {
                for(int row=0;row<r.Height;row++)Marshal.Copy(mapped.DataPointer+(nint)(row*mapped.RowPitch),frame.Pixels,row*frame.Stride,frame.Stride);
                return ValueTask.FromResult<VisionFrame?>(frame);
            }
            catch {frame.Dispose();throw;}
            finally {_context.Unmap(_staging!,0);}
        }
        finally {latest?.Dispose();}
    }
    private void Start(nint window)
    {
        if(!GraphicsCaptureSession.IsSupported())throw new NotSupportedException("当前系统不支持 Windows Graphics Capture");
        _window=window;_closed=false;
        D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],out _device,out _context).CheckError();
        using var dxgi=_device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out var abi));
        try {_winrtDevice=MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{Marshal.Release(abi);}
        _item=CreateItem(window);_item.Closed+=ItemClosed;
        _pool=Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,_item.Size);
        _session=_pool.CreateCaptureSession(_item);_session.IsCursorCaptureEnabled=false;
        _session.StartCapture();_epoch++;
    }
    private void ItemClosed(GraphicsCaptureItem sender,object args)=>_closed=true;
    private void EnsureStaging(int width,int height)
    {
        if(_staging is not null&&width==_stageWidth&&height==_stageHeight)return;
        _staging?.Dispose();
        _staging=_device!.CreateTexture2D(new Texture2DDescription
        {Width=(uint)width,Height=(uint)height,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,
            SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Staging,CPUAccessFlags=CpuAccessFlags.Read});
        _stageWidth=width;_stageHeight=height;
    }
    public void Reset()
    {
        if(_item is not null)_item.Closed-=ItemClosed;
        _session?.Dispose();_session=null;_pool?.Dispose();_pool=null;_item=null;
        _staging?.Dispose();_staging=null;(_winrtDevice as IDisposable)?.Dispose();_winrtDevice=null;
        _context?.Dispose();_context=null;_device?.Dispose();_device=null;_window=0;
    }
    public void Dispose()=>Reset();
    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name,name.Length,out var hstring));
        nint factory=0;
        try
        {
            var iid=typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring,ref iid,out factory));
            var interop=(IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            try
            {
                var itemId=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(interop.CreateForWindow(hwnd,ref itemId,out var item));
                try{return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);}finally{Marshal.Release(item);}
            }
            finally{Marshal.ReleaseComObject(interop);}
        }
        finally{if(factory!=0)Marshal.Release(factory);WindowsDeleteString(hstring);}
    }
    [ComImport,Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]int CreateForWindow(nint window,ref Guid iid,out nint result);
        [PreserveSig]int CreateForMonitor(nint monitor,ref Guid iid,out nint result);
    }
    [ComImport,Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess {[PreserveSig]int GetInterface(ref Guid iid,out nint result);}
    [DllImport("d3d11.dll")]private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint result);
    [DllImport("combase.dll",CharSet=CharSet.Unicode)]private static extern int WindowsCreateString(string source,int length,out nint result);
    [DllImport("combase.dll")]private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]private static extern int RoGetActivationFactory(nint name,ref Guid iid,out nint factory);
    [DllImport("user32.dll")]public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]public static extern bool GetClientRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]public static extern bool ClientToScreen(nint hwnd,ref NativePoint point);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out NativeRect rect,int size);
    [StructLayout(LayoutKind.Sequential)]public struct NativeRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]public struct NativePoint {public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
}
