using System.Runtime.InteropServices;
using GenshinVideoHelper.Core.Vision;
using GenshinVideoHelper.Infrastructure.Vision;
using OpenCvSharp;
using Xunit;
namespace GenshinVideoHelper.Infrastructure.Tests.Vision;
public sealed class MinimapAlgorithmTests
{
    [Theory][InlineData(0,0,0)][InlineData(20,40,20)][InlineData(150,150,150)][InlineData(255,255,255)]
    public void MissingBlackAndFlatMapsAreRejected(int b,int g,int r)
    {
        using var image=new Mat(212,212,MatType.CV_8UC3,new Scalar(b,g,r));
        Assert.False(MinimapAnalyzer.Quality(image,out _));
        using var direction=new CameraDirection();Assert.Null(direction.Detect(image));
        Cv2.Circle(image,new Point(106,106),98,new Scalar(230,230,230),2);
        Cv2.FillConvexPoly(image,[new(106,100),new(101,112),new(112,110)],new Scalar(220,220,0));
        Assert.False(MinimapAnalyzer.Quality(image,out _));
    }
    [Fact] public void KnownLocalTranslationAndRotationRecoverPosition()
    {
        using var image=Terrain(17);using var dest=new Mat();
        using var transform=Mat.FromArray(new double[,]{{1,0,12},{0,1,-8}});
        Cv2.WarpAffine(image,dest,transform,new Size(212,212));
        using var analyzer=new MinimapAnalyzer();var result=analyzer.Register(image,dest);
        Assert.NotNull(result);var p=MapProjection.Position(result.Value.A,result.Value.B,result.Value.Tx,result.Value.Ty);
        Assert.InRange(p.X,117,119);Assert.InRange(p.Y,97,99);
        using var rotation=Cv2.GetRotationMatrix2D(new Point2f(106,106),15,1.05);
        Cv2.WarpAffine(image,dest,rotation,new Size(212,212));result=analyzer.Register(image,dest);
        Assert.NotNull(result);Assert.InRange(result.Value.A,1.00,1.03);Assert.InRange(result.Value.B,-.29,-.25);
    }
    [Fact] public void DifferentRegionsAreRejected()
    {
        using var a=Terrain(17);using var b=Terrain(951);using var analyzer=new MinimapAnalyzer();
        Assert.Null(analyzer.Register(a,b));
    }
    [Fact] public void DetectorNeverSearchesOutsideUpperLeftQuarter()
    {
        using var full=new Mat(1080,1920,MatType.CV_8UC3,new Scalar(70,90,40));
        using var terrain=Terrain(17);using(var roi=new Mat(full,new Rect(1500,700,212,212)))terrain.CopyTo(roi);
        Cv2.Circle(full,new(1606,806),100,new Scalar(220,220,220),3);
        using var f=new VisionFrame(1920,1080,3,1920,1080,new(0,0,1920,1080),"full");
        Assert.Null(DiskDetector.Detect(full,f));
    }
    [Theory][InlineData(10)][InlineData(100)][InlineData(260)]
    public void WhiteConeNeverUsesPlayerArrowAsCamera(int degrees)
    {
        using var image=Terrain(21);using var cone=image.Clone();
        // Flat tinted ground yields distributed cone edges; add a contradictory blue player heading.
        cone.SetTo(new Scalar(60,110,140));
        for(int y=0;y<212;y++)for(int x=0;x<212;x++)
        {
            var r=DiskDetector.Distance(x,y,106,106);double a=Math.Atan2(y-106,x-106)*180/Math.PI;
            if(MapProjection.AngularDistance(a,degrees)>45||r<18||r>90)continue;
            double alpha=Math.Max(.03,1-(137+1.43*r)/255);var c=cone.At<Vec3b>(y,x);
            cone.Set(y,x,new Vec3b((byte)(c.Item0+(255-c.Item0)*alpha),(byte)(c.Item1+(255-c.Item1)*alpha),(byte)(c.Item2+(255-c.Item2)*alpha)));
        }
        Cv2.FillConvexPoly(cone,[new(106,96),new(99,114),new(115,109)],new Scalar(255,255,0));
        using var detector=new CameraDirection();var result=detector.Detect(cone);
        Assert.NotNull(result);Assert.InRange(MapProjection.AngularDistance(result.Value*180/Math.PI,degrees),0,5);
    }
    internal static Mat Terrain(int seed)
    {
        var random=new Random(seed);var image=new Mat(212,212,MatType.CV_8UC3,new Scalar(55,105,100));
        for(int i=0;i<180;i++)Cv2.Circle(image,new(random.Next(212),random.Next(212)),random.Next(1,7),new Scalar(random.Next(30,160),random.Next(55,170),random.Next(50,180)),-1);
        for(int i=0;i<35;i++)Cv2.Line(image,new(random.Next(212),random.Next(212)),new(random.Next(212),random.Next(212)),new Scalar(25,65,75),1);
        return image;
    }
}
