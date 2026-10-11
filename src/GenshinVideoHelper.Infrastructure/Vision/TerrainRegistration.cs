using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

/// <summary>Only terrain ORB descriptors participate in the guide-to-user similarity transform.</summary>
public sealed class TerrainRegistration : IDisposable
{
    private readonly ORB _orb = ORB.Create(700,1.12f,8,12,0,2,ORBScoreType.Harris,19,8);
    private readonly BFMatcher _matcher = new(NormTypes.Hamming);
    private readonly CLAHE _contrast = Cv2.CreateCLAHE(3,new Size(4,4));
    private Mat? _guideImage;
    private Features? _guideFeatures;
    public string Failure { get; private set; } = "";
    public double Correlation { get; private set; }
    public int Inliers { get; private set; }
    public bool RetainMarker { get; private set; }

    public void ResetGuide() { _guideImage=null;_guideFeatures?.Dispose();_guideFeatures=null; }
    public void Dispose() { ResetGuide();_orb.Dispose();_matcher.Dispose();_contrast.Dispose(); }

    public static unsafe Mat Mask(Mat image)
    {
        using var hsv=new Mat();using var icons=new Mat();using var white=new Mat();
        Cv2.CvtColor(image,hsv,ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv,new Scalar(80,130,200),new Scalar(104,255,255),icons);
        Cv2.InRange(hsv,new Scalar(0,0,211),new Scalar(180,44,255),white);
        Cv2.BitwiseOr(icons,white,icons);
        using var kernel=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(5,5));
        Cv2.Dilate(icons,icons,kernel);
        var mask=new Mat(212,212,MatType.CV_8UC1,Scalar.Black);
        for(int y=26;y<212;y++)
        {
            var excluded=(byte*)icons.Ptr(y);var row=(byte*)mask.Ptr(y);
            for(int x=0;x<212;x++)
            {
                int r2=(x-106)*(x-106)+(y-106)*(y-106);
                if(r2<94*94&&r2>18*18&&excluded[x]==0)row[x]=255;
            }
        }
        return mask;
    }

    private static unsafe bool Black(Mat gray,Mat mask)
    {
        Span<int> histogram=stackalloc int[256];histogram.Clear();int count=0;
        for(int y=0;y<gray.Height;y++)
        {
            var row=(byte*)gray.Ptr(y);var yes=(byte*)mask.Ptr(y);
            for(int x=0;x<gray.Width;x++)if(yes[x]!=0){histogram[row[x]]++;count++;}
        }
        if(count==0)return true;
        int sum=0;
        for(int i=0;i<35;i++)sum+=histogram[i];
        return sum>count/2;
    }
    public static bool IsBlack(Mat image)
    {
        using var mask=Mask(image);using var gray=new Mat();
        Cv2.CvtColor(image,gray,ColorConversionCodes.BGR2GRAY);
        return Black(gray,mask);
    }

    private sealed class Features : IDisposable
    {
        public Mat Gray { get; } = new();
        public Mat Mask { get; }
        public Mat Descriptors { get; } = new();
        public KeyPoint[] Points { get; set; } = [];
        public bool Black { get; set; }
        public Features(Mat image) { Mask=TerrainRegistration.Mask(image);Cv2.CvtColor(image,Gray,ColorConversionCodes.BGR2GRAY); }
        public void Dispose() { Gray.Dispose();Mask.Dispose();Descriptors.Dispose(); }
    }
    private Features Prepare(Mat image)
    {
        var data=new Features(image);
        try
        {
            data.Black=Black(data.Gray,data.Mask);
            if(!data.Black)
            {
                using var contrasted=new Mat();_contrast.Apply(data.Gray,contrasted);
                _orb.DetectAndCompute(contrasted,data.Mask,out var points,data.Descriptors);
                data.Points=points;
            }
            return data;
        }
        catch { data.Dispose();throw; }
    }

    public (double A,double B,double Tx,double Ty,double Correlation,int Inliers)? Register(Mat guide,Mat user,bool cacheGuide=false)
    {
        if(guide.Size()!=new Size(212,212)||user.Size()!=new Size(212,212))throw new ArgumentException("地图必须归一化到212像素");
        Failure="";Correlation=0;Inliers=0;RetainMarker=false;
        (double,double,double,double,double,int)? Reject(string reason,bool retain=true)
        {Failure=reason;RetainMarker=retain;return null;}
        using var transient=cacheGuide?null:Prepare(guide);
        if(cacheGuide && !ReferenceEquals(guide,_guideImage))
        { ResetGuide();_guideFeatures=Prepare(guide);_guideImage=guide; }
        var source=transient??_guideFeatures!;
        using var target=Prepare(user);
        if(source.Black||target.Black)return Reject(source.Black?"视频小地图全黑或未探索":"游戏小地图全黑或未探索",false);
        if(source.Descriptors.Empty()||target.Descriptors.Empty()||Math.Min(source.Points.Length,target.Points.Length)<8)
            return Reject("地形特征不足");
        var good=_matcher.KnnMatch(source.Descriptors,target.Descriptors,2)
            .Where(pair=>pair.Length==2&&pair[0].Distance<.78*pair[1].Distance).Select(pair=>pair[0])
            .GroupBy(match=>match.TrainIdx).Select(group=>group.MinBy(match=>match.Distance)).ToArray();
        if(good.Length<8)return Reject("地形特征对应不足");
        var pa=good.Select(match=>source.Points[match.QueryIdx].Pt).ToArray();
        var pb=good.Select(match=>target.Points[match.TrainIdx].Pt).ToArray();
        using var inputA=InputArray.Create(pa);using var inputB=InputArray.Create(pb);using var mask=new Mat();
        using var matrix=Cv2.EstimateAffinePartial2D(inputA,inputB,mask,RobustEstimationAlgorithms.RANSAC,2,1000,.99,10);
        if(matrix is null||matrix.Empty())return Reject("无法估计地形变换");
        double a=matrix.At<double>(0,0),b=matrix.At<double>(1,0),tx=matrix.At<double>(0,2),ty=matrix.At<double>(1,2);
        if(!double.IsFinite(a)||!double.IsFinite(b)||!double.IsFinite(tx)||!double.IsFinite(ty))return Reject("地形变换无效",false);
        var indices=Enumerable.Range(0,pa.Length).Where(i=>mask.At<byte>(i)!=0).ToArray();
        Inliers=indices.Length;
        if(Inliers<8||Inliers<pa.Length*.25)return Reject("一致地形特征不足");
        var residual=indices.Select(i=>MinimapImage.Distance(a*pa[i].X-b*pa[i].Y+tx,b*pa[i].X+a*pa[i].Y+ty,pb[i].X,pb[i].Y)).Order().ToArray();
        double median=residual.Length%2==1?residual[residual.Length/2]:(residual[residual.Length/2-1]+residual[residual.Length/2])/2;
        if(median>=1.5)return Reject("地形对齐误差过大");
        if(Math.Min(Cv2.ContourArea(Cv2.ConvexHull(indices.Select(i=>pa[i]))),Cv2.ContourArea(Cv2.ConvexHull(indices.Select(i=>pb[i]))))<500)
            return Reject("地形特征过于集中");
        var point=GenshinVideoHelper.Core.Vision.MapProjection.Position(a,b,tx,ty);
        double scale=Math.Sqrt(a*a+b*b);
        if(scale<.65||scale>1.5||MinimapImage.Distance(point.X,point.Y,106,106)>94)return Reject("指导位置在可见地图外",false);
        using var warpedMask=new Mat();using var overlap=new Mat();
        Cv2.WarpAffine(source.Mask,warpedMask,matrix,new Size(212,212),InterpolationFlags.Nearest);
        Cv2.BitwiseAnd(warpedMask,target.Mask,overlap);
        if(Cv2.CountNonZero(overlap)<1800)return Reject("共同地形范围不足",false);
        using var highA=HighPass(source.Gray);using var highB=HighPass(target.Gray);using var warped=new Mat();
        Cv2.WarpAffine(highA,warped,matrix,new Size(212,212));
        Correlation=Correlate(warped,highB,overlap,new Rect(0,0,212,212)).Correlation;
        if(Correlation<.50)return Reject("地形相关性不足");
        var local=new List<double>();
        for(int y=0;y<212;y+=26)for(int x=0;x<212;x+=26)
        {
            var evidence=Correlate(warped,highB,overlap,new Rect(x,y,Math.Min(26,212-x),Math.Min(26,212-y)));
            if(evidence.Count>=100&&evidence.Variance>=9)local.Add(evidence.Correlation);
        }
        local.Sort();
        if(local.Count<6)return Reject("共同地形证据不足");
        double localMedian=local.Count%2==1?local[local.Count/2]:(local[local.Count/2-1]+local[local.Count/2])/2;
        if(localMedian<.50)return Reject("地形分区不一致");
        return(a,b,tx,ty,Correlation,Inliers);
    }

    private static Mat HighPass(Mat gray)
    {
        var result=new Mat();gray.ConvertTo(result,MatType.CV_32F);
        using var background=new Mat();Cv2.GaussianBlur(result,background,new Size(0,0),3);
        Cv2.Subtract(result,background,result);return result;
    }
    private static unsafe (int Count,double Variance,double Correlation) Correlate(Mat a,Mat b,Mat mask,Rect bounds)
    {
        int count=0;double sx=0,sy=0,sxx=0,syy=0,sxy=0;
        for(int y=bounds.Y;y<bounds.Bottom;y++)
        {
            var ar=(float*)a.Ptr(y);var br=(float*)b.Ptr(y);var mr=(byte*)mask.Ptr(y);
            for(int x=bounds.X;x<bounds.Right;x++)if(mr[x]!=0)
            {double xx=ar[x],yy=br[x];count++;sx+=xx;sy+=yy;sxx+=xx*xx;syy+=yy*yy;sxy+=xx*yy;}
        }
        if(count==0)return(0,0,0);
        double vx=Math.Max(0,sxx-sx*sx/count),vy=Math.Max(0,syy-sy*sy/count);
        return(count,Math.Min(vx,vy)/count,(sxy-sx*sy/count)/(Math.Sqrt(vx*vy)+1e-9));
    }
}
