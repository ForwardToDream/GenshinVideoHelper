using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Vision;
using Point=GenshinVideoHelper.App.Native.GameWindowCapture.NativePoint;
namespace GenshinVideoHelper.App.Native;

/// <summary>Retained small native layered surface, with no full-screen WPF render target.</summary>
public sealed class MinimapOverlay : IDisposable
{
    private nint _window;
    private static readonly WindowProc Procedure = WindowMessage;
    private static readonly Lazy<ushort> WindowClass = new(() => { var c = new NativeClass { Procedure = Procedure, Instance = GetModuleHandle(null), ClassName = "GVH.MinimapOverlay" }; var atom = RegisterClass(ref c); if (atom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return atom; });
    private nint _dc,_bitmap,_oldBitmap,_bits,_game;
    private Bitmap? _surface;
    private int _size,_lastMarkerSize,_x,_y;
    private MapMarker? _last;
    private long _capturedAt;
    private bool _visible;
    private readonly DispatcherTimer _watchdog=new(){Interval=TimeSpan.FromMilliseconds(40)};
    public MinimapOverlay(){_watchdog.Tick+=CheckVisibility;}
    public void Update(VisionUpdate update,int markerSize)
    {
        if(update.Marker is not {} marker || GameWindowCapture.GetForegroundWindow()!=update.Window ||
            Stopwatch.GetElapsedTime(update.CapturedAt).TotalMilliseconds>250) {Hide();return;}
        _game=update.Window;_capturedAt=update.CapturedAt;
        var origin=new Point();GameWindowCapture.ClientToScreen(_game,ref origin);
        var scale=marker.Disk.Radius/100;
        int size=Math.Clamp(((int)Math.Ceiling((30+markerSize+5)*scale)+2)*2,32,768);
        EnsureSurface(size);
        int x=origin.X+(int)Math.Round(marker.X)-size/2,y=origin.Y+(int)Math.Round(marker.Y)-size/2;
        if(_last!=marker || _lastMarkerSize!=markerSize)
        {
            using var graphics=Graphics.FromImage(_surface!);
            graphics.CompositingMode=CompositingMode.SourceCopy;graphics.Clear(Color.Transparent);
            graphics.CompositingMode=CompositingMode.SourceOver;graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float center=size/2f, radius=(float)marker.Disk.Radius;
            using var clip=new GraphicsPath();
            clip.AddEllipse((float)(center+marker.Disk.X-marker.X-radius),(float)(center+marker.Disk.Y-marker.Y-radius),radius*2,radius*2);
            graphics.SetClip(clip);
            var color=Color.FromArgb(255,(int)(marker.Color>>16)&255,(int)(marker.Color>>8)&255,(int)marker.Color&255);
            if(marker.DirectionRadians is {} direction)
            {
                float dx=(float)(Math.Cos(direction)*26*scale),dy=(float)(Math.Sin(direction)*26*scale);
                using var outside=new Pen(Color.Black,(float)(7*scale)){StartCap=LineCap.Round,EndCap=LineCap.Round};
                using var inside=new Pen(Color.White,(float)(5*scale)){StartCap=LineCap.Round,EndCap=LineCap.Round};
                using var line=new Pen(color,(float)(3*scale)){StartCap=LineCap.Round,EndCap=LineCap.Round};
                graphics.DrawLine(outside,center,center,center+dx,center+dy);graphics.DrawLine(inside,center,center,center+dx,center+dy);graphics.DrawLine(line,center,center,center+dx,center+dy);
            }
            float r=(float)(markerSize*scale);
            graphics.FillEllipse(Brushes.Black,center-r-2,center-r-2,2*r+4,2*r+4);
            graphics.FillEllipse(Brushes.White,center-r-1,center-r-1,2*r+2,2*r+2);
            using var fill=new SolidBrush(color);graphics.FillEllipse(fill,center-r,center-r,2*r,2*r);
            graphics.Flush(FlushIntention.Sync);
            _last=marker;_lastMarkerSize=markerSize;
            var point=new Point(x,y);var extent=new Point(size,size);var source=new Point();var blend=new Blend{SourceConstantAlpha=255,AlphaFormat=1};
            if(!UpdateLayeredWindow(_window,0,ref point,ref extent,_dc,ref source,0,ref blend,2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        if(!_visible || x!=_x || y!=_y)SetWindowPos(_window,new nint(-1),x,y,size,size,0x0010|0x0040);
        _visible=true;_x=x;_y=y;_watchdog.Start();
    }
    private void CheckVisibility(object? sender,EventArgs args)
    {
        if(!_visible)return;
        if(GameWindowCapture.GetForegroundWindow()!=_game || !GameWindowCapture.IsWindow(_game) ||
            GameWindowCapture.IsIconic(_game) || Stopwatch.GetElapsedTime(_capturedAt).TotalMilliseconds>250){Hide();return;}
        var origin=new Point();GameWindowCapture.ClientToScreen(_game,ref origin);
        if(_last is {} m && (origin.X+(int)Math.Round(m.X)-_size/2!=_x || origin.Y+(int)Math.Round(m.Y)-_size/2!=_y))Hide();
    }
    public void Hide(){if(_window != 0 && _visible)ShowWindow(_window,0);_visible=false;_watchdog.Stop();}
    private void EnsureSurface(int size)
    {
        if(_window == 0)
        {
            _ = WindowClass.Value;
            _window=CreateWindowEx(0x00080000|0x00000020|0x00000080|0x08000000,"GVH.MinimapOverlay","GVH minimap marker",0x80000000,0,0,size,size,0,0,GetModuleHandle(null),0);
            if(_window==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetWindowDisplayAffinity(_window,0x11);_dc=CreateCompatibleDC(0);
        }
        if(size==_size)return;
        FreeSurface();
        var info=new BitmapInfo{Size=40,Width=size,Height=-size,Planes=1,BitCount=32};
        _bitmap=CreateDIBSection(_dc,ref info,0,out _bits,0,0);
        if(_bitmap==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _oldBitmap=SelectObject(_dc,_bitmap);_surface=new Bitmap(size,size,size*4,PixelFormat.Format32bppPArgb,_bits);_size=size;_last=null;
    }
    private static nint WindowMessage(nint hwnd,uint msg,nint w,nint l)
    {if(msg==0x84)return new nint(-1);if(msg==0x21)return new nint(3);return DefWindowProc(hwnd,msg,w,l);}
    private void FreeSurface(){_surface?.Dispose();_surface=null;if(_bitmap!=0){SelectObject(_dc,_oldBitmap);DeleteObject(_bitmap);_bitmap=0;}}
    public void Dispose(){Hide();_watchdog.Tick-=CheckVisibility;FreeSurface();if(_dc!=0){DeleteDC(_dc);_dc=0;}if(_window!=0)DestroyWindow(_window);_window=0;}
    private delegate nint WindowProc(nint hwnd,uint message,nint wparam,nint lparam);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NativeClass{public uint Style;public WindowProc Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background,Menu;public string ClassName;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClass(ref NativeClass cls);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint extended,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint DefWindowProc(nint hwnd,uint msg,nint w,nint l);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;public int XPels,YPels;public uint ColorsUsed,ColorsImportant;}
    [StructLayout(LayoutKind.Sequential,Pack=1)]private struct Blend{public byte Operation,Flags,SourceConstantAlpha,AlphaFormat;}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UpdateLayeredWindow(nint hwnd,nint screen,ref Point position,ref Point size,nint source,ref Point sourcePoint,uint key,ref Blend blend,uint flags);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")]private static extern bool SetWindowDisplayAffinity(nint hwnd,uint affinity);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")]private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
}
