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
    public static void Baseline(string root)
    {
        var output=Path.Combine(root,"artifacts","vision-baseline");
        var inputs=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"inputs.json")));
        using var analyzer=new MinimapAnalyzer();var rows=new List<object>();int inside=0,found=0,accurate=0,outsideOutputs=0,negativeAccepted=0;
        var coldTimes=new List<double>();var hotTimes=new List<double>();
        foreach(var item in inputs.RootElement.EnumerateArray())
        {
            string id=item.GetProperty("id").GetString()!;
            using var full=Cv2.ImRead(item.GetProperty("path").GetString()!);
            using var f=new VisionFrame(full.Width,full.Height,3,full.Width,full.Height,new(0,0,full.Width,full.Height),id);
            Marshal.Copy(full.Data,f.Pixels,0,f.Width*f.Height*3);
            var truth=item.GetProperty("gt");bool positive=truth.ValueKind==JsonValueKind.Array;
            bool inScope=positive&&truth[0].GetDouble()<full.Width/2&&truth[1].GetDouble()<full.Height/2;
            if(inScope)inside++;
            MapDisk? disk=null;var times=new List<double>();
            for(int i=0;i<4;i++){var t=Stopwatch.GetTimestamp();disk=DiskDetector.Detect(full,f);if(i>0)times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);}
            times.Sort();coldTimes.Add(times[1]);double? error=null,radiusError=null;
            if(disk is {} d&&inScope){found++;error=DiskDetector.Distance(d.X,d.Y,truth[0].GetDouble(),truth[1].GetDouble())*100/truth[2].GetDouble();radiusError=Math.Abs(d.Radius-truth[2].GetDouble())*100/truth[2].GetDouble();if(error<3&&radiusError<6)accurate++;}
            if(disk is not null&&positive&&!inScope)outsideOutputs++;
            analyzer.Reset();var result=analyzer.Analyze(f,f,false,1000);
            if(result.Marker is not null&&!positive)negativeAccepted++;
            if(result.Marker is not null)
            {
                var hot=new List<double>();for(int i=0;i<10;i++){var start=Stopwatch.GetTimestamp();result=analyzer.Analyze(f,f,true,1000);hot.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}hot.Sort();hotTimes.Add(hot[5]);
            }
            rows.Add(new{id,inScope,disk,error,radiusError,coldMs=times[1],accepted=result.Marker is not null,status=result.Status});
        }
        var exp=Path.Combine(root,"experiments","minimap-follow");
        using var labels=JsonDocument.Parse(File.ReadAllText(Path.Combine(exp,"data","camera-labels.json")));
        using var frames=JsonDocument.Parse(File.ReadAllText(Path.Combine(exp,"data","frames.json")));
        using var direction=new CameraDirection();var angles=new List<object>();int accepted=0,bad=0;
        foreach(var label in labels.RootElement.GetProperty("labels").EnumerateObject())
        {
            var metadata=frames.RootElement.EnumerateArray().First(f=>f.GetProperty("id").GetString()==label.Name);
            using var native=Cv2.ImRead(Path.Combine(exp,metadata.GetProperty("file").GetString()!));using var image=new Mat();Cv2.Resize(native,image,new OpenCvSharp.Size(212,212));
            var prediction=direction.Detect(image);double? error=prediction is {} a?MapProjection.AngularDistance(a*180/Math.PI,label.Value.GetDouble()):null;
            if(error.HasValue){accepted++;if(error>5)bad++;}angles.Add(new{id=label.Name,degrees=prediction*180/Math.PI,error});
        }
        var summary=new{inside,found,accurate,outsideOutputs,negativeAccepted,coldMedian=Median(coldTimes),hotPairMedian=Median(hotTimes),cameraAccepted=accepted,cameraErrorsOver5=bad};
        File.WriteAllText(Path.Combine(output,"csharp-results.json"),JsonSerializer.Serialize(new{summary,rows,angles},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(summary));
        Check(outsideOutputs==0&&negativeAccepted==0,"Scope and negative gates");Check(accurate>=10,"Circle port retains baseline coverage");Check(bad==0&&accepted>=10,"Camera accepts accurate native directions");
    }
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
                fixture.Activate();animation.Start();var hwnd=new WindowInteropHelper(fixture).Handle;
                await CaptureWindowAsync(hwnd);
                await BrowserAsync(root);
            }
            catch(Exception ex){failure=ex;}
            finally{animation.Stop();fixture.Close();app.Shutdown();}
        };
        fixture.Show();app.Run();if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
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
                    if(count>0){Check(frame.Width==120&&frame.Height==120,"GPU copies only requested ROI");timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
                    plan=new(new(10,10,120,120),frame.Epoch);count++;
                    marker=new(1,"test",new(80,80,0,0xff00ff,new(80,80,70)),hwnd,frame.CapturedAt);
                }
                await Task.Delay(50);
            }
            Check(count>=10,"WGC yields consecutive fresh frames");
        });
        using var overlay=new MinimapOverlay();
        var time=Stopwatch.GetTimestamp();overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp()},5);
        Check(GameWindowCapture.GetForegroundWindow()==hwnd,"Overlay does not steal focus");
        Console.WriteLine($"WGC ROI median {Median(timings):F2} ms; first overlay update {Stopwatch.GetElapsedTime(time).TotalMilliseconds:F2} ms");
        var redraw=new List<double>();
        for(int i=0;i<30;i++){var start=Stopwatch.GetTimestamp();overlay.Update(marker! with{CapturedAt=Stopwatch.GetTimestamp(),Marker=marker!.Marker! with{X=80+i*.1}},5);redraw.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
        Console.WriteLine($"Overlay redraw median {Median(redraw):F3} ms");
        await Task.Delay(100);overlay.Hide();
    }
    private static async Task BrowserAsync(string root)
    {
        using var temp=TestArtifacts.CreateTemp(root,"vision-media");
        var folder=Path.Combine(temp.Path,"video","BV1hjgG6jEa6");Directory.CreateDirectory(folder);
        using var portProbe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);portProbe.Start();
        int port=((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        using var server=new System.Net.HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        byte[] media=await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","test-media","sample.mp4"));
        var serve=Task.Run(async()=>{try{while(server.IsListening){var request=await server.GetContextAsync();bool mp4=request.Request.RawUrl!.Contains(".mp4");byte[] body=mp4?media:System.Text.Encoding.UTF8.GetBytes("<video muted autoplay style='width:320px' src='/sample.mp4'></video>");request.Response.ContentType=mp4?"video/mp4":"text/html";request.Response.ContentLength64=body.Length;await request.Response.OutputStream.WriteAsync(body);request.Response.Close();}}catch(Exception)when(!server.IsListening){}});
        await using var browser=await ChromeFixture.StartAsync(root,headed:true);
        using var client=new CdpClient(reuseConnections:true);
        var endpoint=new Uri(browser.Page.WebSocketDebuggerUrl);
        await client.SendAsync(endpoint,"Page.navigate",new{url=$"http://127.0.0.1:{port}/video/BV1hjgG6jEa6/"});
        using var video=new VideoController();
        for(int i=0;i<30;i++){try{await video.ExecuteAsync(browser.Page,new("ensurePlay"));await video.ExecuteAsync(browser.Page,new("ensurePip"));break;}catch(InvalidOperationException){await Task.Delay(100);}}
        var source=video.FrameSource;var identity=new VideoIdentity("BV1hjgG6jEa6",1);
        using var cold=await source.CaptureAsync(browser.Page,identity,new(),CancellationToken.None);
        Check(cold is not null&&cold.SourceWidth==640&&cold.Region.Width==320,"Video samples intrinsic size, not CSS size");
        using var local=await source.CaptureAsync(browser.Page,identity,new(new(10,20,120,120),cold!.Epoch),CancellationToken.None);
        Check(local is not null&&local.Width==120&&local.Region.X==10,"Video uses cached source ROI");
        await video.ExecuteAsync(browser.Page,new("seek",3,true));await Task.Delay(150);
        using var seek=await source.CaptureAsync(browser.Page,identity,new(new(10,20,120,120),cold.Epoch),CancellationToken.None);
        Check(seek is not null&&seek.Epoch!=cold.Epoch&&seek.Region.X==0,"Seek invalidates crop/epoch");
        using var stale=await source.CaptureAsync(browser.Page,identity with{Part=2},new(),CancellationToken.None);
        Check(stale is null,"Wrong episode rejects the frame");
        await video.ExecuteAsync(browser.Page,new("ensurePipClosed"));
        using var closed=await source.CaptureAsync(browser.Page,identity,new(),CancellationToken.None);
        Check(closed is null,"Manually closed PiP stops map capture");
        server.Stop();await serve;
    }
}
