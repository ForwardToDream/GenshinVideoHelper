using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

/// <summary>Accepted arrow_first: cyan triangle centre, then bounded circular radial search.</summary>
public static class GuideDiskDetector
{
    private const double EdgeTolerance = .92;
    private const double AreaToRadiusSquared = .022;
    private static readonly float[] Cos = Enumerable.Range(0,120).Select(i => (float)Math.Cos(i*(float)(2*Math.PI/120))).ToArray();
    private static readonly float[] Sin = Enumerable.Range(0,120).Select(i => (float)Math.Sin(i*(float)(2*Math.PI/120))).ToArray();
    private readonly record struct Arrow(double X, double Y, double Area, double Width, double Height);

    public static MapDisk? Detect(Mat source, VisionFrame frame)
    {
        if (frame.Region.Width <= 0 || frame.Region.Height <= 0) return null;
        double sx = (double)frame.Region.Width / frame.Width, sy = (double)frame.Region.Height / frame.Height;
        if (Math.Abs(sx-sy) > Math.Max(sx,sy)*.01) return null;
        var bounds = MinimapGeometry.GuideSearchRegion(frame.SourceWidth, frame.SourceHeight);
        int width = Math.Min(source.Width, (int)Math.Floor((bounds.Width-frame.Region.X)/sx));
        int height = Math.Min(source.Height, (int)Math.Floor((bounds.Height-frame.Region.Y)/sy));
        if (frame.Region.X < 0 || frame.Region.Y < 0 || width < 40 || height < 40) return null;
        using var scope = new Mat(source, new Rect(0,0,width,height));
        double scale = Math.Min(1,320d/width);
        using var small = new Mat();
        if (scale < 1) Cv2.Resize(scope,small,new Size(),scale,scale,InterpolationFlags.Area);
        else scope.CopyTo(small);
        using var gray = new Mat(); using var gx = new Mat(); using var gy = new Mat();
        Cv2.CvtColor(small,gray,ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray,gray,new Size(3,3),.7);
        Cv2.Sobel(gray,gx,MatType.CV_32F,1,0); Cv2.Sobel(gray,gy,MatType.CV_32F,0,1);
        double minimum = MinimapGeometry.GameDisk(frame.SourceHeight).Radius/sx*EdgeTolerance;
        MapDisk? best = null; double bestScore = double.NegativeInfinity;
        foreach (var coarse in Candidates(small,minimum*scale,false))
        {
            var native = NativeCandidate(scope,coarse,scale,minimum);
            if (native is not {} arrow) continue;
            var scaled = new Arrow(arrow.X*scale,arrow.Y*scale,arrow.Area*scale*scale,arrow.Width*scale,arrow.Height*scale);
            var circle = FixedCircle(scaled,gx,gy,minimum*scale);
            if (circle is not {} found || found.Score <= bestScore) continue;
            bestScore = found.Score;
            best = new(frame.Region.X+arrow.X*sx,frame.Region.Y+arrow.Y*sy,found.Radius/scale*sx);
        }
        return best;
    }

    private static List<Arrow> Candidates(Mat image, double minimum, bool strict)
    {
        using var hsv = new Mat(); using var mask = new Mat(); using var labels = new Mat();
        using var stats = new Mat(); using var centers = new Mat();
        Cv2.CvtColor(image,hsv,ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv,new Scalar(80,130,200),new Scalar(104,255,255),mask);
        int count = Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers,PixelConnectivity.Connectivity8);
        var output = new List<Arrow>();
        for (int i=1;i<count;i++)
        {
            int x=stats.At<int>(i,0),y=stats.At<int>(i,1),w=stats.At<int>(i,2),h=stats.At<int>(i,3),area=stats.At<int>(i,4);
            double fill=(double)area/(w*h),upper=Math.Pow(Math.Max(minimum,Math.Min(image.Width,image.Height)/2d),2)*.07;
            if(strict) upper=Math.Max(upper,image.Width*image.Height*.35);
            if(area<Math.Max(6,minimum*minimum*.012)||area>upper || (double)w/h<=.45 || (double)w/h>=2.2 || fill<=.30 || fill>=.83)continue;
            if (strict)
            {
                using var component = new Mat(h,w,MatType.CV_8UC1,Scalar.Black);
                unsafe
                {
                    for(int yy=0;yy<h;yy++)
                    {var src=(int*)labels.Ptr(y+yy);var dst=(byte*)component.Ptr(yy);for(int xx=0;xx<w;xx++)if(src[x+xx]==i)dst[xx]=255;}
                }
                Cv2.FindContours(component,out Point[][] contours,out _,RetrievalModes.External,ContourApproximationModes.ApproxSimple);
                if(contours.Length==0)continue;
                var contour=contours.MaxBy(c=>Cv2.ContourArea(c))!;
                double hull=Cv2.ContourArea(Cv2.ConvexHull(contour));
                if(hull<=0)continue;
                double solidity=Cv2.ContourArea(contour)/hull;
                if(Cv2.ApproxPolyDP(contour,.09*Cv2.ArcLength(contour,true),true).Length!=3 || solidity<=.65 || solidity>=.96)continue;
            }
            if(Math.Sqrt(area/AreaToRadiusSquared)<minimum*.83)continue;
            output.Add(new(centers.At<double>(i,0),centers.At<double>(i,1),area,w,h));
        }
        return output;
    }

    private static Arrow? NativeCandidate(Mat source,Arrow coarse,double scale,double minimum)
    {
        double cx=coarse.X/scale,cy=coarse.Y/scale,ex=coarse.Width/scale*.7+4/scale,ey=coarse.Height/scale*.7+4/scale;
        int left=Math.Max(0,(int)(cx-ex)),top=Math.Max(0,(int)(cy-ey));
        int right=Math.Min(source.Width,(int)(cx+ex)+1),bottom=Math.Min(source.Height,(int)(cy+ey)+1);
        using var patch=new Mat(source,new Rect(left,top,right-left,bottom-top));
        var candidates=Candidates(patch,minimum,true);
        if(candidates.Count==0)return null;
        var arrow=candidates.MinBy(a=>MinimapImage.Distance(a.X+left,a.Y+top,cx,cy));
        arrow=arrow with {X=arrow.X+left,Y=arrow.Y+top};
        return MinimapImage.Distance(arrow.X,arrow.Y,cx,cy)>Math.Max(coarse.Width,coarse.Height)/scale ? null : arrow;
    }

    private static unsafe (double Radius,double Score)? FixedCircle(Arrow arrow,Mat gx,Mat gy,double minimum)
    {
        double guess=Math.Sqrt(arrow.Area/AreaToRadiusSquared);
        double limit=Math.Min(Math.Min(arrow.X,arrow.Y),Math.Min(gx.Width-arrow.X-1,gx.Height-arrow.Y-1));
        float first=(float)Math.Max(minimum,guess*.83);
        double end=Math.Min(limit,guess*1.28)+.1;
        int count=(int)Math.Ceiling((end-first)/.5);
        if(count<8)return null;
        using var mx=new Mat(120,count,MatType.CV_32F);using var my=new Mat(120,count,MatType.CV_32F);
        for(int a=0;a<120;a++)
        {
            var x=(float*)mx.Ptr(a);var y=(float*)my.Ptr(a);
            for(int j=0;j<count;j++){float r=first+j*.5f;x[j]=(float)(arrow.X+Cos[a]*r);y[j]=(float)(arrow.Y+Sin[a]*r);}
        }
        using var dx=new Mat();using var dy=new Mat();
        Cv2.Remap(gx,dx,mx,my,InterpolationFlags.Linear);Cv2.Remap(gy,dy,mx,my,InterpolationFlags.Linear);
        using var support=new Mat(1,count,MatType.CV_32F,Scalar.Black);
        var hits=new byte[120,count];var totals=new int[count];var values=(float*)support.Ptr();
        for(int a=0;a<120;a++)
        {
            var xx=(float*)dx.Ptr(a);var yy=(float*)dy.Ptr(a);
            for(int j=0;j<count;j++)
            {
                float radial=Math.Abs(xx[j]*Cos[a]+yy[j]*Sin[a]);double magnitude=Math.Sqrt(xx[j]*xx[j]+yy[j]*yy[j]);
                if(radial>10&&radial/Math.Max(magnitude,1)>.65){hits[a,j]=1;totals[j]++;}
            }
        }
        for(int j=0;j<count;j++)values[j]=totals[j]/120f;
        Cv2.GaussianBlur(support,support,new Size(7,1),0);
        int selected=-1;double score=double.NegativeInfinity;
        for(int j=0;j<count;j++)
        {
            int sectors=0;
            for(int s=0;s<8;s++){int n=0;for(int a=s*15;a<(s+1)*15;a++)n+=hits[a,j];if(n/15d>.15)sectors++;}
            if(sectors<6||values[j]<.50)continue;
            float radius=first+j*.5f;
            double candidate=values[j]-.35*Math.Abs(Math.Log(radius/guess));
            if(candidate>score){score=candidate;selected=j;}
        }
        return selected<0 ? null : (first+selected*.5f,score);
    }
}
