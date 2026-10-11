using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

public sealed class MinimapAnalyzer : IMinimapAnalyzer
{
    private readonly TerrainRegistration _terrain=new();
    private readonly CameraDirection _direction=new();
    private uint _color;
    public CapturePlan VideoPlan => new();
    public CapturePlan GamePlan => new();
    public string LastRegistrationFailure => _terrain.Failure;
    public double LastCorrelation => _terrain.Correlation;
    public int LastInliers => _terrain.Inliers;
    public MinimapAnalyzer() { Cv2.SetNumThreads(1); }
    public void Reset() { _color=0; }
    public void Dispose() { _terrain.Dispose();_direction.Dispose(); }
    public static bool IsBlack(Mat image) => TerrainRegistration.IsBlack(image);
    public static Mat TerrainMask(Mat image) => TerrainRegistration.Mask(image);
    public (double A,double B,double Tx,double Ty,double Correlation,int Inliers)? Register(Mat video,Mat game) => _terrain.Register(video,game);

    public MapAnalysis Analyze(VisionFrame video,VisionFrame game,bool direction)
    {
        using var guidePixels=MinimapImage.ToBgr(video);
        var guideDisk=GuideDiskDetector.Detect(guidePixels,video);
        if(guideDisk is not {} guide)return new("视频：未找到小地图圆盘");
        // The user HUD is fixed. No arrow, circle detection or rim validation runs on this source.
        var user=MinimapGeometry.GameDisk(game.SourceHeight);
        if(user.X+user.Radius>game.SourceWidth||user.Y+user.Radius>game.SourceHeight)
            return new("游戏画面尺寸不足");
        using var userPixels=MinimapImage.ToBgr(game);
        using var v=MinimapImage.Normalize(guidePixels,video,guide);
        using var g=MinimapImage.Normalize(userPixels,game,user);
        var result=Register(v,g);
        if(result is not {} registration)return new($"地图匹配未通过：{LastRegistrationFailure}",
            Correlation:LastCorrelation,Inliers:LastInliers,RetainMarker:_terrain.RetainMarker);
        var(a,b,tx,ty,corr,inliers)=registration;
        var(px,py)=MapProjection.Position(a,b,tx,ty);
        double? angle=direction?_direction.Detect(v):null;
        if(angle.HasValue)angle=MapProjection.Direction(angle.Value,a,b);
        _color=ChooseColor(g,px,py,angle,_color);
        var marker=new MapMarker(user.X+(px-106)*user.Radius/100,user.Y+(py-106)*user.Radius/100,angle,_color,user);
        return new(direction&&!angle.HasValue?"位置已匹配，视线待确认":"正在标记指导位置",marker,corr,inliers);
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
