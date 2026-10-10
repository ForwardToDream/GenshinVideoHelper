using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

/// <summary>White-cone paired boundaries, then robust ray/intersection validation. Never uses arrow heading.</summary>
public sealed class CameraDirection : IDisposable
{
    private readonly Mat _mx = new(360, 19, MatType.CV_32F);
    private readonly Mat _my = new(360, 19, MatType.CV_32F);
    public CameraDirection()
    {
        for(int a=0;a<360;a++) for(int r=0;r<19;r++)
        { _mx.Set(a,r,(float)(106+Math.Cos(a*Math.PI/180)*(22+3*r))); _my.Set(a,r,(float)(106+Math.Sin(a*Math.PI/180)*(22+3*r))); }
    }
    public double? Detect(Mat image)
    {
        using var polar = new Mat(); Cv2.Remap(image,polar,_mx,_my,InterpolationFlags.Linear);
        var entry = new double[360,19]; var exit = new double[360,19];
        for(int a=0;a<360;a++) for(int r=0;r<19;r++)
        {
            var lo=polar.At<Vec3b>((a+357)%360,r); var hi=polar.At<Vec3b>((a+3)%360,r);
            double alpha=Math.Max(.03,1-(137+1.43*(22+3*r))/255);
            entry[a,r]=Vote(lo,hi,alpha); exit[a,r]=Vote(hi,lo,alpha);
        }
        var raw=new double[360]; var scores=new double[360];
        for(int a=0;a<360;a++) for(int r=0;r<19;r++) raw[a]+=Math.Min(entry[a,r],exit[(a+90)%360,r])/19;
        for(int a=0;a<360;a++) scores[a]=(raw[(a+359)%360]+2*raw[a]+raw[(a+1)%360])/4;
        int peak=Enumerable.Range(0,360).MaxBy(a=>scores[a]);
        var rival=Enumerable.Range(0,360).Where(a=>MapProjection.AngularDistance(a,peak)>15).Max(a=>scores[a]);
        if(scores[peak]<.12 || scores[peak]-rival<.025) return null;
        var first=Fit(entry,peak); var second=Fit(exit,(peak+90)%360);
        if(first is not {} f || second is not {} s || Math.Max(f.Residual,s.Residual)>=1.5) return null;
        double determinant=f.Dx*s.Dy-f.Dy*s.Dx;
        if(Math.Abs(determinant)<.2) return null;
        double t=((s.X-f.X)*s.Dy-(s.Y-f.Y)*s.Dx)/determinant;
        double x=f.X+t*f.Dx,y=f.Y+t*f.Dy;
        if(DiskDetector.Distance(x,y,106,106)>=10) return null;
        double ux=f.X-x,uy=f.Y-y,vx=s.X-x,vy=s.Y-y;
        double ul=Math.Sqrt(ux*ux+uy*uy),vl=Math.Sqrt(vx*vx+vy*vy);
        if(ul<1 || vl<1) return null;
        ux/=ul;uy/=ul;vx/=vl;vy/=vl;
        double opening=Math.Acos(Math.Clamp(ux*vx+uy*vy,-1,1))*180/Math.PI;
        double direction=Math.Atan2(uy+vy,ux+vx);
        if(opening is <=78 or >=102 || MapProjection.AngularDistance(direction*180/Math.PI,(peak+45)%360)>8) return null;
        return direction;
    }
    private static double Vote(Vec3b lo,Vec3b hi,double alpha)
    {
        double bright=Math.Max(lo.Item0,Math.Max(lo.Item1,lo.Item2)), dark=Math.Min(lo.Item0,Math.Min(lo.Item1,lo.Item2));
        if(bright>=233 || bright-dark<=12) return 0;
        double b=(hi.Item0-lo.Item0)/Math.Max(255d-lo.Item0,24),g=(hi.Item1-lo.Item1)/Math.Max(255d-lo.Item1,24),r=(hi.Item2-lo.Item2)/Math.Max(255d-lo.Item2,24);
        return Math.Clamp((b+g+r)/(3*alpha),0,1.5)*Math.Exp(-1.5*(Math.Max(b,Math.Max(g,r))-Math.Min(b,Math.Min(g,r)))/alpha);
    }
    private static (double Dx,double Dy,double X,double Y,double Residual)? Fit(double[,] signal,int direction)
    {
        var points=new List<Point2f>();
        for(int r=0;r<19;r++)
        {
            double peak=0;
            for(int o=-13;o<=13;o++) peak=Math.Max(peak,signal[(direction+o+360)%360,r]);
            if(peak<=.17) continue;
            double weights=0,angle=0;
            for(int o=-13;o<=13;o++) { double w=Math.Max(0,signal[(direction+o+360)%360,r]-.8*peak); weights+=w; angle+=w*o; }
            double theta=(direction+angle/Math.Max(weights,1e-6))*Math.PI/180;
            points.Add(new((float)(106+Math.Cos(theta)*(22+3*r)),(float)(106+Math.Sin(theta)*(22+3*r))));
        }
        if(points.Count<8) return null;
        var line=Cv2.FitLine(points,DistanceTypes.Huber,0,.01,.01);
        var residual=points.Select(p=>Math.Abs((p.X-line.X1)*line.Vy-(p.Y-line.Y1)*line.Vx)).Order().ToArray();
        return (line.Vx,line.Vy,line.X1,line.Y1,residual[residual.Length/2]);
    }
    public void Dispose() { _mx.Dispose(); _my.Dispose(); }
}
