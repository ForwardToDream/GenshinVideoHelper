using System.Runtime.InteropServices;
using GenshinVideoHelper.Core.Vision;
using GenshinVideoHelper.Infrastructure.Vision;
using OpenCvSharp;
using Xunit;
namespace GenshinVideoHelper.Infrastructure.Tests.Vision;

public sealed class MinimapAlgorithmTests
{
    [Theory][InlineData(0)][InlineData(45)][InlineData(100)][InlineData(180)][InlineData(260)]
    public void GuideArrowLocatesRotatingEnlargedCircularMap(int degrees)
    {
        using var terrain=Terrain(17);using var scene=Scene(terrain,new(235,230,185),true,degrees);
        using var frame=Frame(scene);var disk=GuideDiskDetector.Detect(scene,frame);
        Assert.NotNull(disk);
        Assert.InRange(MinimapImage.Distance(disk.Value.X,disk.Value.Y,235,230),0,8);
        Assert.InRange(disk.Value.Radius,172,197);
    }
    [Theory][InlineData(false,185,0)][InlineData(true,55,0)][InlineData(true,185,40)]
    public void GuideRequiresCorrectArrowAndMinimumSize(bool arrow,double radius,int hueShift)
    {
        using var terrain=Terrain(17);using var scene=Scene(terrain,new(235,230,radius),arrow,0,hueShift);
        using var frame=Frame(scene);Assert.Null(GuideDiskDetector.Detect(scene,frame));
    }
    [Fact] public void GuideSearchIsOnlyLeftThirdTopHalf()
    {
        using var terrain=Terrain(17);
        foreach(var disk in new[]{new MapDisk(860,230,150),new MapDisk(235,760,150)})
        {
            using var scene=Scene(terrain,disk,true,width:1280,height:1080);
            using var frame=Frame(scene);Assert.Null(GuideDiskDetector.Detect(scene,frame));
        }
    }
    [Fact] public void GuideCropOffsetAndUniformDownscalePreserveSourceCoordinates()
    {
        using var terrain=Terrain(17);using var scene=Scene(terrain,new(235,230,185),true);
        using var roi=new Mat(scene,new Rect(20,10,600,520));using var small=new Mat();
        Cv2.Resize(roi,small,new Size(300,260),interpolation:InterpolationFlags.Area);
        using var frame=Frame(small,new(20,10,600,520));var disk=GuideDiskDetector.Detect(small,frame);
        Assert.NotNull(disk);Assert.InRange(MinimapImage.Distance(disk.Value.X,disk.Value.Y,235,230),0,8);
        Assert.InRange(disk.Value.Radius,172,197);
        using var stretched=Frame(small,new(20,10,600,500));Assert.Null(GuideDiskDetector.Detect(small,stretched));
    }
    [Fact] public void FixedUserMapWorksWithoutPlayerArrowAndWithGpuCropOffset()
    {
        using var terrain=Terrain(17);using var guide=Scene(terrain,new(235,230,185),true);
        var fixedDisk=MinimapGeometry.GameDisk(1080);
        using var user=Scene(terrain,fixedDisk,false,width:360,height:300);
        var region=MinimapGeometry.GameCaptureRegion(1920,1080);
        using var userRoi=new Mat(user,new Rect(region.X,region.Y,region.Width,region.Height));
        using var v=Frame(guide);using var g=Frame(userRoi,region);using var analyzer=new MinimapAnalyzer();
        var result=analyzer.Analyze(v,g,false);Assert.True(result.Marker is not null,result.Status);
        Assert.Equal(fixedDisk,result.Marker.Disk);
        Assert.InRange(MinimapImage.Distance(result.Marker.X,result.Marker.Y,168,125),0,6);
        Assert.Null(analyzer.GamePlan.Region);Assert.NotNull(analyzer.VideoPlan.Region);
        var crop=analyzer.VideoPlan.Region.Value;
        Assert.True(crop.Width*crop.Height<guide.Width*guide.Height);
        using var clipped=new Mat(guide,new Rect(crop.X,crop.Y,crop.Width,crop.Height));
        using var next=Frame(clipped,crop);
        var cropped=analyzer.Analyze(next,g,false);Assert.True(cropped.Marker is not null,cropped.Status);
        Assert.InRange(MinimapImage.Distance(cropped.Marker.X,cropped.Marker.Y,result.Marker.X,result.Marker.Y),0,4);
        using var blank=new Mat(crop.Height,crop.Width,MatType.CV_8UC3,Scalar.Black);
        using var lostCrop=Frame(blank,crop);
        Assert.NotNull(analyzer.Analyze(lostCrop,g,false).Marker);
        Assert.Null(analyzer.VideoPlan.Region); // Retry full search without blanking a good match.
    }
    [Fact] public void MissingGuideAndBlackUserClearThenRecover()
    {
        using var terrain=Terrain(17);using var guide=Scene(terrain,new(235,230,185),true);
        using var user=Scene(terrain,MinimapGeometry.GameDisk(1080),false,width:360,height:300);
        using var v=Frame(guide);using var g=Frame(user);using var analyzer=new MinimapAnalyzer();
        Assert.NotNull(analyzer.Analyze(v,g,false).Marker);
        using var noArrow=Scene(terrain,new(235,230,185),false);using var badGuide=Frame(noArrow);
        var missing=analyzer.Analyze(badGuide,g,false);Assert.Null(missing.Marker);Assert.False(missing.RetainMarker);
        using var black=new Mat(300,360,MatType.CV_8UC3,Scalar.Black);using var badUser=Frame(black);
        var result=analyzer.Analyze(v,badUser,false);Assert.Null(result.Marker);Assert.False(result.RetainMarker);Assert.Contains("全黑",result.Status);
        Assert.NotNull(analyzer.Analyze(v,g,false).Marker);
    }
    [Fact] public void KnownLocalTranslationAndRotationRecoverPosition()
    {
        using var image=Terrain(17);using var dest=new Mat();using var analyzer=new MinimapAnalyzer();
        using var transform=Mat.FromArray(new double[,]{{1,0,12},{0,1,-8}});
        Cv2.WarpAffine(image,dest,transform,new Size(212,212));var result=analyzer.Register(image,dest);
        Assert.True(result.HasValue,analyzer.LastRegistrationFailure);
        var point=MapProjection.Position(result.Value.A,result.Value.B,result.Value.Tx,result.Value.Ty);
        Assert.InRange(MinimapImage.Distance(point.X,point.Y,118,98),0,1);
        using var rotation=Cv2.GetRotationMatrix2D(new Point2f(106,106),15,1.05);
        Cv2.WarpAffine(image,dest,rotation,new Size(212,212));result=analyzer.Register(image,dest);
        Assert.True(result.HasValue,analyzer.LastRegistrationFailure);
        Assert.InRange(result.Value.A,1.00,1.03);Assert.InRange(result.Value.B,-.29,-.25);
    }
    [Fact] public void UnrelatedAndFlatTerrainNeverMarks()
    {
        using var a=Terrain(17);using var b=Terrain(951);using var analyzer=new MinimapAnalyzer();
        Assert.Null(analyzer.Register(a,b));
        using var flat=new Mat(212,212,MatType.CV_8UC3,new Scalar(80,110,130));Assert.Null(analyzer.Register(a,flat));
        using var black=new Mat(212,212,MatType.CV_8UC3,Scalar.Black);Assert.Null(analyzer.Register(a,black));
    }
    [Fact] public void TerrainMaskExcludesArrowButKeepsBlueWater()
    {
        using var scene=new Mat(212,212,MatType.CV_8UC3,new Scalar(170,100,60));
        Cv2.FillPoly(scene,[[new Point(150,65),new(165,85),new(150,80),new(135,85)]],new Scalar(255,230,0));
        using var mask=MinimapAnalyzer.TerrainMask(scene);
        Assert.Equal(255,mask.At<byte>(90,145));Assert.Equal(0,mask.At<byte>(75,150));Assert.Equal(0,mask.At<byte>(106,106));
    }

    [Theory][InlineData(10)][InlineData(100)][InlineData(260)]
    public void WhiteConeNeverUsesPlayerArrowAsCamera(int degrees)
    {
        using var image=Terrain(21);using var cone=image.Clone();
        // Flat tinted ground yields distributed cone edges; add a contradictory blue player heading.
        cone.SetTo(new Scalar(60,110,140));
        for(int y=0;y<212;y++)for(int x=0;x<212;x++)
        {
            var r=MinimapImage.Distance(x,y,106,106);double a=Math.Atan2(y-106,x-106)*180/Math.PI;
            if(MapProjection.AngularDistance(a,degrees)>45||r<18||r>90)continue;
            double alpha=Math.Max(.03,1-(137+1.43*r)/255);var c=cone.At<Vec3b>(y,x);
            cone.Set(y,x,new Vec3b((byte)(c.Item0+(255-c.Item0)*alpha),(byte)(c.Item1+(255-c.Item1)*alpha),(byte)(c.Item2+(255-c.Item2)*alpha)));
        }
        Cv2.FillConvexPoly(cone,[new(106,96),new(99,114),new(115,109)],new Scalar(255,255,0));
        using var detector=new CameraDirection();var result=detector.Detect(cone);
        Assert.NotNull(result);Assert.InRange(MapProjection.AngularDistance(result.Value*180/Math.PI,degrees),0,5);
    }

    private static Mat Scene(Mat terrain,MapDisk disk,bool arrow,int degrees=0,int hueShift=0,int width=640,int height=540)
    {
        double scale=disk.Radius/100;
        using var transform=Mat.FromArray(new double[,]{{scale,0,disk.X-106*scale},{0,scale,disk.Y-106*scale}});
        var output=new Mat(height,width,MatType.CV_8UC3,new Scalar(15,25,20));
        using var warped=new Mat();Cv2.WarpAffine(terrain,warped,transform,new Size(width,height));
        using var mask=new Mat(height,width,MatType.CV_8UC1,Scalar.Black);
        Cv2.Circle(mask,new((int)disk.X,(int)disk.Y),(int)disk.Radius,Scalar.White,-1);warped.CopyTo(output,mask);
        Cv2.Circle(output,new((int)disk.X,(int)disk.Y),(int)disk.Radius,new Scalar(230,230,230),2);
        if(arrow)
        {
            double radians=degrees*Math.PI/180;
            var shape=new[]{new Point(0,-14),new(13,11),new(0,5),new(-13,11)};
            var points=shape.Select(p=>new Point((int)Math.Round(disk.X+(p.X*Math.Cos(radians)-p.Y*Math.Sin(radians))*scale),
                (int)Math.Round(disk.Y+(p.X*Math.Sin(radians)+p.Y*Math.Cos(radians))*scale))).ToArray();
            Cv2.FillPoly(output,[points],hueShift==0?new Scalar(255,230,0):new Scalar(0,200,255));
        }
        return output;
    }
    private static VisionFrame Frame(Mat image,FrameRegion? region=null)
    {
        using var pixels=image.Clone();var area=region??new FrameRegion(0,0,image.Width,image.Height);
        var frame=new VisionFrame(image.Width,image.Height,3,1920,1080,area,"test");
        Marshal.Copy(pixels.Data,frame.Pixels,0,image.Width*image.Height*3);return frame;
    }
    internal static Mat Terrain(int seed)
    {
        var random=new Random(seed);var image=new Mat(212,212,MatType.CV_8UC3,new Scalar(55,105,100));
        for(int i=0;i<180;i++)Cv2.Circle(image,new(random.Next(212),random.Next(212)),random.Next(1,7),new Scalar(random.Next(30,160),random.Next(55,170),random.Next(50,180)),-1);
        for(int i=0;i<35;i++)Cv2.Line(image,new(random.Next(212),random.Next(212)),new(random.Next(212),random.Next(212)),new Scalar(25,65,75),1);
        return image;
    }
}
