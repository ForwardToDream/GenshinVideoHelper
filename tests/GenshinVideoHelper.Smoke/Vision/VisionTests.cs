using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Vision;
using OpenCvSharp;
namespace GenshinVideoHelper.Smoke;
internal static class VisionTests
{
    private static double Median(List<double> values){values.Sort();return values.Count==0?0:values[values.Count/2];}
    public static void Run(string root)
    {
        var app=CreateTestApplication();Exception? failure=null;
        var fill=new SolidColorBrush(Color.FromRgb(40,130,70));
        var fixture=new System.Windows.Window{Title="GVH capture fixture",Width=960,Height=600,WindowStyle=WindowStyle.None,Background=fill,Topmost=false};
        var animation=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};int tick=0;
        animation.Tick+=(_,_)=>fill.Color=Color.FromRgb((byte)(40+(++tick%5)),130,70);
        fixture.Loaded+=async(_,_)=>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fixture.Activate();animation.Start();var hwnd=new WindowInteropHelper(fixture).Handle;
                Check(GameWindowCapture.GetForegroundWindow()==hwnd,"Desktop capture fixture requires foreground access");
                await CaptureWindowAsync(hwnd);
                await BrowserAsync(root);
            }
            catch(Exception ex){failure=ex;}
            finally{animation.Stop();fixture.Close();app.Shutdown();}
        };
        fixture.Show();app.Run();if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public static void RunBrowser(string root) => BrowserAsync(root).GetAwaiter().GetResult();
    private static async Task CaptureWindowAsync(nint hwnd)
    {
        var timings=new List<double>();VisionUpdate? marker=null;
        await Task.Run(async()=>
        {
            using var capture=new GameWindowCapture();CapturePlan plan=new();int count=0;
            var until=Stopwatch.StartNew();
            while(until.Elapsed<TimeSpan.FromSeconds(5)&&count<12)
            {
                var start=Stopwatch.GetTimestamp();using var frame=await capture.CaptureAsync(hwnd,plan,CancellationToken.None);
                if(frame is not null)
                {
                    Check(frame.Pixels[30*frame.Stride+30*4+1]>=120&&frame.Pixels[30*frame.Stride+30*4+1]<=140,"WGC captures selected client pixels");
                    Check(Stopwatch.GetElapsedTime(frame.CapturedAt).TotalMilliseconds<250,"WGC timestamps share monotonic clock");
                    if(count>0){Check(frame.Region==MinimapGeometry.GameCaptureRegion(frame.SourceWidth,frame.SourceHeight),"GPU copies only fixed user HUD ROI");timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
                    plan=new(new(10,10,120,120),frame.Epoch);count++;
                    marker=new(1,"test",new(80,80,0,0xff00ff,new(80,80,70)),hwnd,frame.CapturedAt);
                }
                await Task.Delay(50);
            }
            Console.WriteLine($"WGC received {count} frames; fixture foreground={capture.IsForeground(hwnd)}; foreground HWND={GameWindowCapture.GetForegroundWindow()}; fixture HWND={hwnd}");
            Check(count>=10,"WGC yields consecutive fresh frames");
            // Reproduce the old one-second failure backoff without consuming the two-buffer pool.
            await Task.Delay(1100);
            using var afterIdle=await capture.CaptureAsync(hwnd,plan,CancellationToken.None);
            Check(afterIdle is not null && Stopwatch.GetElapsedTime(afterIdle.CapturedAt).TotalMilliseconds<250,
                "WGC discards queued old frames and recovers after a long sampling gap");
        });
        using var overlay=new MinimapOverlay();
        var time=Stopwatch.GetTimestamp();overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp()},5);
        Check(GameWindowCapture.GetForegroundWindow()==hwnd,"Overlay does not steal focus");
        Console.WriteLine($"WGC ROI median {Median(timings):F2} ms; first overlay update {Stopwatch.GetElapsedTime(time).TotalMilliseconds:F2} ms");
        var redraw=new List<double>();
        for(int i=0;i<30;i++){var start=Stopwatch.GetTimestamp();overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp(),Marker=marker!.Marker! with{X=80+i*.1}},5);redraw.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
        Console.WriteLine($"Overlay redraw median {Median(redraw):F3} ms");
        overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp()},5);
        overlay.Update(new(1,"transient",RetainMarker:true),5);
        Check(overlay.IsVisible,"One transient miss does not hide a current marker");
        await Task.Delay(160);
        overlay.Update(new(1,"transient",CapturedAt:Stopwatch.GetTimestamp(),RetainMarker:true),5);
        await Task.Delay(160);
        Check(!overlay.IsVisible,"Repeated transient misses never renew the old marker timestamp");
        overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp()},5);
        Check(overlay.IsVisible,"Fresh reliable frame restores marker");
        overlay.Update(new(1,"black map"),5);
        Check(!overlay.IsVisible,"Hard rejection hides the marker immediately");
    }
    private static async Task BrowserAsync(string root)
    {
        using var temp=TestArtifacts.CreateTemp(root,"vision-media");
        var folder=Path.Combine(temp.Path,"video","BV1hjgG6jEa6");Directory.CreateDirectory(folder);
        using var portProbe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);portProbe.Start();
        int port=((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        using var server=new System.Net.HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        byte[] media=await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","test-media","sample.mp4"));
        int serverStopping=0;
        var serve=Task.Run(async()=>{try{while(server.IsListening){var request=await server.GetContextAsync();bool mp4=request.Request.RawUrl!.Contains(".mp4");byte[] body=mp4?media:System.Text.Encoding.UTF8.GetBytes("<video muted autoplay style='width:320px' src='/sample.mp4'></video>");request.Response.ContentType=mp4?"video/mp4":"text/html";request.Response.ContentLength64=body.Length;await request.Response.OutputStream.WriteAsync(body);request.Response.Close();}}catch(Exception)when(Volatile.Read(ref serverStopping)!=0||!server.IsListening){}});
        await using var browser=await ChromiumFixture.StartAsync(root,headed:true);
        using var client=new CdpClient(reuseConnections:true);
        var endpoint=new Uri(browser.Page.WebSocketDebuggerUrl);
        await client.SendAsync(endpoint,"Page.navigate",new{url=$"http://127.0.0.1:{port}/video/BV1hjgG6jEa6/"});
        using var video=new VideoController();
        for(int i=0;i<30;i++){try{await video.ExecuteAsync(browser.Page,new("ensurePlay"));await video.ExecuteAsync(browser.Page,new("ensurePip"));break;}catch(InvalidOperationException){await Task.Delay(100);}}
        var source=video.FrameSource;var identity=new VideoIdentity("BV1hjgG6jEa6",1);
        await Task.Delay(200);
        int invalidations=0,activities=0;
        video.VideoFrameInvalidated+=_=>Interlocked.Increment(ref invalidations);
        video.MediaActivity+=_=>Interlocked.Increment(ref activities);
        using var cold=await source.CaptureAsync(browser.Page,identity,new(),CancellationToken.None);
        Check(cold is not null&&cold.SourceWidth==640&&cold.Region.Width==213&&cold.Width==213&&cold.Height==180,"Video samples intrinsic size, not CSS size");
        using var local=await source.CaptureAsync(browser.Page,identity,new(new(10,20,120,120),cold!.Epoch),CancellationToken.None);
        Check(local is not null&&local.Width==213&&local.Region.X==0,"Guide always uses native one-sixth search ROI");
        await video.ExecuteAsync(browser.Page,new("toggle"));await Task.Delay(150);
        await video.ExecuteAsync(browser.Page,new("rate",1.5));await Task.Delay(100);
        Check(Volatile.Read(ref activities)>=2 && Volatile.Read(ref invalidations)==0,
            "Pause and speed events update playback without resetting minimap recognition");
        await client.SendAsync(endpoint,"Runtime.evaluate",new{expression="const ad=document.createElement('video');document.body.append(ad);ad.dispatchEvent(new Event('loadedmetadata',{bubbles:true}));"});
        await Task.Delay(100);
        Check(Volatile.Read(ref invalidations)==0,"Unrelated video events do not reset the followed map");
        using(var paused=await source.CaptureAsync(browser.Page,identity,new(new(10,20,120,120),cold.Epoch),CancellationToken.None))
            Check(paused is not null&&paused.Epoch==cold.Epoch&&paused.Region.X==0&&paused.Width==213,"Pause and rate preserve native guide search pixels");
        await video.ExecuteAsync(browser.Page,new("seek",3,true));await Task.Delay(150);
        Check(Volatile.Read(ref invalidations)>0,"Seek immediately invalidates the minimap source");
        using var seek=await source.CaptureAsync(browser.Page,identity,new(new(10,20,120,120),cold.Epoch),CancellationToken.None);
        Check(seek is not null&&seek.Epoch!=cold.Epoch&&seek.Region.X==0,"Seek invalidates crop/epoch");
        using var stale=await source.CaptureAsync(browser.Page,identity with{Part=2},new(),CancellationToken.None);
        Check(stale is null,"Wrong episode rejects the frame");
        await video.ExecuteAsync(browser.Page,new("ensurePipClosed"));
        using var closed=await source.CaptureAsync(browser.Page,identity,new(),CancellationToken.None);
        Check(closed is null,"Manually closed PiP stops map capture");
        Interlocked.Exchange(ref serverStopping,1);server.Stop();await serve;
        Console.WriteLine("Video capture: native ROI, pause/rate without reset, unrelated media, seek invalidation, wrong episode and PiP closure passed.");
    }
}
