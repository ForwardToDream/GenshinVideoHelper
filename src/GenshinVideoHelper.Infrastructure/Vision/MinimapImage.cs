using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

public static class MinimapImage
{
    public const int Size = 212;
    public const double Center = 106;
    public const double Radius = 100;

    public static Mat ToBgr(VisionFrame frame)
    {
        using var input = Mat.FromPixelData(frame.Height, frame.Width,
            frame.Channels == 4 ? MatType.CV_8UC4 : MatType.CV_8UC3, frame.Pixels);
        var result = new Mat();
        if (frame.Channels == 4) Cv2.CvtColor(input, result, ColorConversionCodes.BGRA2BGR);
        else input.CopyTo(result);
        return result;
    }

    // Disk and Region are source physical pixels; remap addresses the cropped buffer.
    public static unsafe Mat Normalize(Mat source, VisionFrame frame, MapDisk disk)
    {
        double sx = (double)frame.Region.Width / frame.Width, sy = (double)frame.Region.Height / frame.Height;
        double cx = (disk.X - frame.Region.X) / sx, cy = (disk.Y - frame.Region.Y) / sy;
        double rx = disk.Radius / sx, ry = disk.Radius / sy;
        int left = Math.Max(0, (int)(cx - rx * 1.12)), top = Math.Max(0, (int)(cy - ry * 1.12));
        int right = Math.Min(source.Width, (int)(cx + rx * 1.12) + 2);
        int bottom = Math.Min(source.Height, (int)(cy + ry * 1.12) + 2);
        if (right <= left || bottom <= top) return new(Size, Size, MatType.CV_8UC3, Scalar.Black);
        using var patch = new Mat(source, new Rect(left, top, right - left, bottom - top));
        using var smooth = new Mat();
        double scale = Math.Max(rx, ry) / Radius;
        if (scale > 1.1) Cv2.GaussianBlur(patch, smooth, new Size(0, 0), Math.Max(.35, (scale - 1) * .6));
        using var mapX = new Mat(Size, Size, MatType.CV_32F);
        using var mapY = new Mat(Size, Size, MatType.CV_32F);
        for (int y = 0; y < Size; y++)
        {
            var xx = (float*)mapX.Ptr(y); var yy = (float*)mapY.Ptr(y);
            for (int x = 0; x < Size; x++)
            {
                xx[x] = (float)((x - Center) * rx / Radius + cx - left);
                yy[x] = (float)((y - Center) * ry / Radius + cy - top);
            }
        }
        var output = new Mat();
        Cv2.Remap(scale > 1.1 ? smooth : patch, output, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Constant);
        return output;
    }

    public static double Distance(double x, double y, double a, double b) => Math.Sqrt((x-a)*(x-a)+(y-b)*(y-b));
}
