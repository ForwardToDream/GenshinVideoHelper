using System.Diagnostics;
using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

public sealed class MinimapAnalyzer : IMinimapAnalyzer
{
    private sealed class Source
    {
        public MapDisk? Disk;
        public string? Epoch;
        public int Width,Height;
        public long NextSearch;
        public CapturePlan Plan => new(Disk?.CaptureRegion(Width,Height),Epoch);
        public void Clear() { Disk=null; Epoch=null; NextSearch=0; }
    }
    private readonly Source _video=new(),_game=new();
    private readonly ORB _orb=ORB.Create(700,1.12f,8,12,0,2,ORBScoreType.Harris,19,8);
    private readonly BFMatcher _matcher=new(NormTypes.Hamming);
    private readonly CameraDirection _direction=new();
    private uint _color;
    public CapturePlan VideoPlan=>_video.Plan;
    public CapturePlan GamePlan=>_game.Plan;
    public MinimapAnalyzer() { Cv2.SetNumThreads(1); }
    public void Reset() { _video.Clear(); _game.Clear(); _color=0; }
    public void Dispose() { _orb.Dispose(); _matcher.Dispose(); _direction.Dispose(); }
    private static Mat? Prepare(VisionFrame frame,Source state,int searchMs,out string reason)
    {
        reason="未找到圆盘";
        if(state.Epoch!=frame.Epoch || state.Width!=frame.SourceWidth || state.Height!=frame.SourceHeight)
        { state.Clear(); state.Epoch=frame.Epoch;state.Width=frame.SourceWidth;state.Height=frame.SourceHeight; }
        using var bgr=DiskDetector.ToBgr(frame);
        if(state.Disk is null)
        {
            if(Stopwatch.GetTimestamp()<state.NextSearch) return null;
            state.NextSearch=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*searchMs/1000d);
            // Old local crops cannot search the source; request a cold overview on the following tick.
            if(frame.Region.X!=0 || frame.Region.Y!=0 || frame.Region.Width<frame.SourceWidth/2-1) return null;
            state.Disk=DiskDetector.Detect(bgr,frame);
        }
        if(state.Disk is not {} disk) return null;
        using var rim=DiskDetector.Normalize(bgr,frame,disk,192,80);
        if(!DiskDetector.CheckRim(rim)) { state.Disk=null;reason="圆盘校验未通过";return null; }
        var image=DiskDetector.Normalize(bgr,frame,disk);
        using var hsv=new Mat();Cv2.CvtColor(image,hsv,ColorConversionCodes.BGR2HSV);
        int cyan=0;
        for(int y=86;y<126;y++)for(int x=86;x<126;x++)
        { var v=hsv.At<Vec3b>(y,x);if(v.Item0>=78&&v.Item0<=112&&v.Item1>115&&v.Item2>135)cyan++; }
        if(cyan<3) {image.Dispose();state.Disk=null;reason="未检测到地图中心";return null;}
        if(!Quality(image,out reason)) {image.Dispose();return null;}
        return image;
    }
    public MapAnalysis Analyze(VisionFrame video,VisionFrame game,bool direction,int searchIntervalMs)
    {
        using var v=Prepare(video,_video,searchIntervalMs,out var vr);
        using var g=Prepare(game,_game,searchIntervalMs,out var gr);
        if(v is null || g is null) return new(v is null?$"视频：{vr}":$"游戏：{gr}");
        var result=Register(v,g,validateTexture:false);
        if(result is not {} registration) return new("地图未可靠匹配，暂不标记");
        var (a,b,tx,ty,corr,inliers)=registration;
        var (px,py)=MapProjection.Position(a,b,tx,ty);
        if(DiskDetector.Distance(px,py,106,106)>94) return new("指导位置在可见地图外");
        double? angle=direction?_direction.Detect(v):null;
        if(angle.HasValue)angle=MapProjection.Direction(angle.Value,a,b);
        var disk=_game.Disk!.Value;
        _color=ChooseColor(g,px,py,angle,_color);
        var marker=new MapMarker(disk.X+(px-106)*disk.Radius/100,disk.Y+(py-106)*disk.Radius/100,angle,_color,disk);
        return new(direction&&!angle.HasValue?"位置已匹配，视线待确认":"正在标记指导位置",marker,corr,inliers);
    }
    public static unsafe bool Quality(Mat image,out string reason)
    {
        using var hsv=new Mat();using var gray=new Mat();using var gx=new Mat();using var gy=new Mat();
        Cv2.CvtColor(image,hsv,ColorConversionCodes.BGR2HSV);Cv2.CvtColor(image,gray,ColorConversionCodes.BGR2GRAY);
        Cv2.Sobel(gray,gx,MatType.CV_32F,1,0);Cv2.Sobel(gray,gy,MatType.CV_32F,0,1);
        int count=0,dark=0;var histogram=new int[256];var cells=new int[16];
        for(int y=31;y<212;y++)
        {
            var colors=(Vec3b*)hsv.Ptr(y);var dx=(float*)gx.Ptr(y);var dy=(float*)gy.Ptr(y);
            for(int x=0;x<212;x++)
            {
                double r2=(x-106)*(x-106)+(y-106)*(y-106);if(r2>=88*88||r2<=18*18)continue;
                var c=colors[x];count++;histogram[c.Item2]++;if(c.Item2<55)dark++;
                if(c.Item1<45&&c.Item2>210 || c.Item0>78&&c.Item0<112&&c.Item1>125)continue;
                if(dx[x]*dx[x]+dy[x]*dy[x]>35*35)cells[(y/53)*4+x/53]++;
            }
        }
        int sum=0,p10=-1,p50=-1,p90=-1;
        for(int i=0;i<256;i++){sum+=histogram[i];if(p10<0&&sum>=count*.1)p10=i;if(p50<0&&sum>=count*.5)p50=i;if(p90<0&&sum>=count*.9)p90=i;}
        bool black=dark>count*.8 || p50<60&&p90-p10<12;
        bool textured=cells.Count(c=>c>=45)>=5;
        reason=black?"地图全黑或未探索":!textured?"地图纹理不足":"可匹配";
        return !black&&textured;
    }
    public static unsafe Mat TerrainMask(Mat image)
    {
        using var hsv=new Mat();Cv2.CvtColor(image,hsv,ColorConversionCodes.BGR2HSV);
        using var icons=new Mat(212,212,MatType.CV_8UC1,Scalar.Black);
        var mask=new Mat(212,212,MatType.CV_8UC1,Scalar.Black);
        for(int y=0;y<212;y++)
        {var colors=(Vec3b*)hsv.Ptr(y);var row=(byte*)icons.Ptr(y);for(int x=0;x<212;x++)
            {var c=colors[x];if(c.Item1<45&&c.Item2>210 || c.Item0>78&&c.Item0<115&&c.Item1>150&&c.Item2>165)row[x]=255;}}
        using var kernel=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(3,3));Cv2.Dilate(icons,icons,kernel);
        for(int y=26;y<212;y++){var excluded=(byte*)icons.Ptr(y);var row=(byte*)mask.Ptr(y);for(int x=0;x<212;x++)
            {int r2=(x-106)*(x-106)+(y-106)*(y-106);if(r2<94*94&&r2>16*16&&excluded[x]==0)row[x]=255;}}
        return mask;
    }
    public (double A,double B,double Tx,double Ty,double Correlation,int Inliers)? Register(Mat video,Mat game,bool validateTexture=true)
    {
        if(validateTexture && (!Quality(video,out _)||!Quality(game,out _)))return null;
        using var va=new Mat();using var ga=new Mat();using var vm=TerrainMask(video);using var gm=TerrainMask(game);
        Cv2.CvtColor(video,va,ColorConversionCodes.BGR2GRAY);Cv2.CvtColor(game,ga,ColorConversionCodes.BGR2GRAY);
        using var vd=new Mat();using var gd=new Mat();
        _orb.DetectAndCompute(va,vm,out var vk,vd);_orb.DetectAndCompute(ga,gm,out var gk,gd);
        if(vd.Empty()||gd.Empty()||Math.Min(vd.Rows,gd.Rows)<8)return null;
        var good=_matcher.KnnMatch(vd,gd,2).Where(p=>p.Length==2&&p[0].Distance<.78*p[1].Distance).Select(p=>p[0]).ToArray();
        if(good.Length<8)return null;
        // Require unique destination evidence; repeated features cannot inflate the inlier count.
        good=good.GroupBy(p=>p.TrainIdx).Select(g=>g.MinBy(p=>p.Distance)).ToArray();
        var pa=good.Select(p=>vk[p.QueryIdx].Pt).ToArray();var pb=good.Select(p=>gk[p.TrainIdx].Pt).ToArray();
        using var pointsA=InputArray.Create(pa);using var pointsB=InputArray.Create(pb);using var inlierMask=new Mat();
        using var matrix=Cv2.EstimateAffinePartial2D(pointsA,pointsB,inlierMask,RobustEstimationAlgorithms.RANSAC,2,1000,.99,10);
        if(matrix is null || matrix.Empty())return null;
        double a=matrix.At<double>(0,0),b=matrix.At<double>(1,0),tx=matrix.At<double>(0,2),ty=matrix.At<double>(1,2);
        if(!new[]{a,b,tx,ty}.All(double.IsFinite))return null;
        double scale=Math.Sqrt(a*a+b*b);var center=MapProjection.Position(a,b,tx,ty);
        if(scale<.65||scale>1.5||DiskDetector.Distance(center.X,center.Y,106,106)>78)return null;
        var indices=Enumerable.Range(0,pa.Length).Where(i=>inlierMask.At<byte>(i)!=0).ToArray();
        if(indices.Length<8||indices.Length<pa.Length*.5)return null;
        if(Cv2.ContourArea(Cv2.ConvexHull(indices.Select(i=>pa[i]))) / (212d*212)<=.035)return null;
        var errors=indices.Select(i=>DiskDetector.Distance(a*pa[i].X-b*pa[i].Y+tx,b*pa[i].X+a*pa[i].Y+ty,pb[i].X,pb[i].Y)).Order().ToArray();
        if(errors[errors.Length/2]>=1.5)return null;
        using var warped=new Mat();using var wm=new Mat();using var overlap=new Mat();
        Cv2.WarpAffine(va,warped,matrix,new Size(212,212));Cv2.WarpAffine(vm,wm,matrix,new Size(212,212),InterpolationFlags.Nearest);
        Cv2.BitwiseAnd(wm,gm,overlap);int count=Cv2.CountNonZero(overlap);
        if(count<212*212*.10)return null;
        double sx=0,sy=0,sxx=0,syy=0,sxy=0;
        unsafe{for(int y=0;y<212;y++){var xrow=(byte*)warped.Ptr(y);var yrow=(byte*)ga.Ptr(y);var mask=(byte*)overlap.Ptr(y);for(int x=0;x<212;x++)
            {if(mask[x]==0)continue;double xx=xrow[x],yy=yrow[x];sx+=xx;sy+=yy;sxx+=xx*xx;syy+=yy*yy;sxy+=xx*yy;}}}
        double corr=(sxy-sx*sy/count)/Math.Sqrt(Math.Max(1e-9,(sxx-sx*sx/count)*(syy-sy*sy/count)));
        return corr>.62?(a,b,tx,ty,corr,indices.Length):null;
    }
    private static readonly uint[] Colors=[0x00A6FF,0xFF00DC,0x00FFFF,0xFFFF00,0x00FF50,0xAE55FF];
    public static uint ChooseColor(Mat game,double x,double y,double? angle,uint previous)
    {
        var pixels=new List<Vec3b>();
        for(int step=0;step<=6;step++)for(int yy=-5;yy<=5;yy+=2)for(int xx=-5;xx<=5;xx+=2)
        {int px=(int)Math.Round(x+xx+(angle.HasValue?Math.Cos(angle.Value)*step*4:0)),py=(int)Math.Round(y+yy+(angle.HasValue?Math.Sin(angle.Value)*step*4:0));
            if(px>=0&&px<212&&py>=0&&py<212)pixels.Add(game.At<Vec3b>(py,px));}
        double Score(uint c){var distances=pixels.Select(p=>Math.Sqrt(Math.Pow(p.Item0-(c&255),2)+Math.Pow(p.Item1-((c>>8)&255),2)+Math.Pow(p.Item2-(c>>16),2))).Order().ToArray();return distances.Length==0?0:distances[distances.Length/5];}
        uint best=Colors.MaxBy(Score);
        return previous!=0&&Score(previous)>=Score(best)*.85?previous:best;
    }
}
