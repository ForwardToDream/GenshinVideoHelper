using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GenshinVideoHelper.Core.Vision;
using GenshinVideoHelper.Infrastructure.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Smoke;

internal static class VisionAlgorithmTests
{
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    public static void Run(string root)
    {
        string exp=Path.Combine(root,"experiments","minimap-disk-v2");
        using var analyzer=new MinimapAnalyzer();
        using var cases=Read(Path.Combine(exp,"data/user/cases.json"));
        using var userReference=Read(Path.Combine(exp,"results/user/detections.json"));
        using var guideIndex=Read(Path.Combine(exp,"data/guide/frames.json"));
        using var guideReference=Read(Path.Combine(exp,"results/guide/detections.json"));
        var userRows=userReference.RootElement.EnumerateArray().ToDictionary(r=>r.GetProperty("id").GetString()!);
        var guideRows=guideReference.RootElement.EnumerateArray().ToDictionary(r=>r.GetProperty("frame").GetInt32());
        int circleCases=0,found=0;double centerError=0,radiusError=0;
        foreach(var entry in cases.RootElement.EnumerateArray())
        {
            using var image=Cv2.ImRead(Path.Combine(exp,entry.GetProperty("file").GetString()!));
            using var frame=Frame(image,3840,2160);
            Compare(GuideDiskDetector.Detect(image,frame),userRows[entry.GetProperty("id").GetString()!]);
        }
        var guideSamples=guideIndex.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        foreach(var entry in guideSamples)
        {
            using var image=Cv2.ImRead(Path.Combine(exp,entry.GetProperty("file").GetString()!));
            using var frame=Frame(image,1920,1080);
            Compare(GuideDiskDetector.Detect(image,frame),guideRows[entry.GetProperty("frame").GetInt32()]);
        }
        void Compare(MapDisk? actual,JsonElement reference)
        {
            circleCases++;
            Check((actual is not null)==reference.GetProperty("found").GetBoolean(),$"Arrow-first detection parity: case {circleCases}");
            if(actual is not {} disk)return;
            found++;var expected=reference.GetProperty("disk");
            double ce=MinimapImage.Distance(disk.X,disk.Y,expected.GetProperty("x").GetDouble(),expected.GetProperty("y").GetDouble());
            double re=Math.Abs(disk.Radius-expected.GetProperty("radius").GetDouble());
            centerError=Math.Max(centerError,ce);radiusError=Math.Max(radiusError,re);
            Check(ce<.1&&re<=2.1,$"Arrow-first geometry parity: case {circleCases}, centre {ce}, radius {re}");
        }
        using var sequence=Read(Path.Combine(exp,"data/user/sequence.json"));
        var samples=sequence.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        int userPairs=0,userAccepted=0;var timings=new List<double>();
        foreach(int offset in new[]{5,20,50,100,180})for(int i=0;i<samples.Length-offset;i+=5)
        {
            using var a=Cv2.ImRead(Path.Combine(exp,samples[i].GetProperty("file").GetString()!));
            using var b=Cv2.ImRead(Path.Combine(exp,samples[i+offset].GetProperty("file").GetString()!));
            using var guide=Frame(a,3840,2160);
            var region=MinimapGeometry.GameCaptureRegion(3840,2160);
            using var crop=new Mat(b,new Rect(region.X,region.Y,region.Width,region.Height));
            using var user=Frame(crop,3840,2160,region);
            long started=Stopwatch.GetTimestamp();var result=analyzer.Analyze(guide,user,false);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);userPairs++;
            if(result.Marker is {} marker)
            {userAccepted++;Check(marker.Disk==MinimapGeometry.GameDisk(2160),"User disk is fixed physical pixels");}
        }
        int guidePairs=0,guideAccepted=0;var selected=guideSamples.Where((_,i)=>i%10==0).ToArray();
        var first=guideRows[0].GetProperty("disk");var fixedGuide=new MapDisk(first.GetProperty("x").GetDouble(),first.GetProperty("y").GetDouble(),first.GetProperty("radius").GetDouble());
        foreach(int offset in new[]{1,5,10})for(int i=0;i<selected.Length-offset;i++)
        {
            using var a=Cv2.ImRead(Path.Combine(exp,selected[i].GetProperty("file").GetString()!));
            using var b=Cv2.ImRead(Path.Combine(exp,selected[i+offset].GetProperty("file").GetString()!));
            using var af=Frame(a,1920,1080);using var bf=Frame(b,1920,1080);
            var disk=GuideDiskDetector.Detect(a,af);Check(disk is not null,"Guide sample disk available");
            using var guide=MinimapImage.Normalize(a,af,disk!.Value);using var user=MinimapImage.Normalize(b,bf,fixedGuide);
            guidePairs++;if(analyzer.Register(guide,user) is not null)guideAccepted++;
        }
        int controls=0,accepted=0,negativeMarks=0;double maxPointError=0;
        foreach(int index in new[]{0,30,90,180})
        {
            using var image=Cv2.ImRead(Path.Combine(exp,samples[index].GetProperty("file").GetString()!));
            using var frame=Frame(image,3840,2160);var disk=GuideDiskDetector.Detect(image,frame)!.Value;
            using var guide=MinimapImage.Normalize(image,frame,disk);
            foreach(var (dx,dy,angle,scale) in new(double,double,double,double)[]{(8,0,0,1),(-12,8,0,1),(25,-18,0,1),(45,30,0,1),(-40,-35,0,1),(15,-10,15,.8),(-18,20,-20,1.2),(0,0,90,1)})
            {
                using var matrix=Cv2.GetRotationMatrix2D(new Point2f(106,106),angle,scale);
                matrix.Set(0,2,matrix.At<double>(0,2)+dx);matrix.Set(1,2,matrix.At<double>(1,2)+dy);
                using var target=new Mat();Cv2.WarpAffine(guide,target,matrix,new Size(212,212),borderMode:BorderTypes.Reflect);
                Cv2.Circle(target,new(106,106),18,new Scalar(80,110,130),-1);
                var result=analyzer.Register(guide,target);controls++;Check(result.HasValue,"Known transform must register");accepted++;
                var measured=result??throw new InvalidOperationException("Expected known transform");
                var p=MapProjection.Position(measured.A,measured.B,measured.Tx,measured.Ty);
                double error=MinimapImage.Distance(p.X,p.Y,106+dx,106+dy);maxPointError=Math.Max(error,maxPointError);
                Check(error<=2,"Known transform position error <= 2 normalized pixels");
            }
            using var black=new Mat(212,212,MatType.CV_8UC3,Scalar.Black);
            using var flat=new Mat(212,212,MatType.CV_8UC3,new Scalar(80,110,130));
            if(analyzer.Register(guide,black) is not null)negativeMarks++;
            if(analyzer.Register(guide,flat) is not null)negativeMarks++;
            var random=new Random(index+12);using var noise=new Mat(212,212,MatType.CV_8UC3);
            for(int y=0;y<212;y++)for(int x=0;x<212;x++)noise.Set(y,x,new Vec3b((byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256)));
            if(analyzer.Register(guide,noise) is not null)negativeMarks++;
            using var shuffled=new Mat(212,212,MatType.CV_8UC3);
            var order=Enumerable.Range(0,16).OrderBy(_=>random.Next()).ToArray();
            for(int j=0;j<16;j++)
            {using var src=new Mat(guide,new Rect(order[j]%4*53,order[j]/4*53,53,53));using var dst=new Mat(shuffled,new Rect(j%4*53,j/4*53,53,53));src.CopyTo(dst);}
            if(analyzer.Register(guide,shuffled) is not null)negativeMarks++;
        }
        timings.Sort();
        var summary=new{circleCases,found,maxCircleCenterError=centerError,maxCircleRadiusError=radiusError,
            userPairs,userAccepted,guidePairs,guideAccepted,controls,accepted,maxPointError,negativeMarks,
            analysisMedianMs=timings[timings.Count/2],analysisP95Ms=timings[(int)(timings.Count*.95)]};
        Console.WriteLine(JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
        Check(userAccepted>=120&&guideAccepted>=150,"Real dual-frame coverage");Check(negativeMarks==0,"No negative markers");
        using var temp=TestArtifacts.CreateTemp(root,"vision-algorithms");
        File.WriteAllText(Path.Combine(temp.Path,"summary.json"),JsonSerializer.Serialize(summary));
    }
    private static VisionFrame Frame(Mat image,int width,int height,FrameRegion? region=null)
    {
        using var copy=image.Clone();var area=region??new FrameRegion(0,0,image.Width,image.Height);
        var frame=new VisionFrame(image.Width,image.Height,3,width,height,area,"offline");
        Marshal.Copy(copy.Data,frame.Pixels,0,image.Width*image.Height*3);return frame;
    }
}
