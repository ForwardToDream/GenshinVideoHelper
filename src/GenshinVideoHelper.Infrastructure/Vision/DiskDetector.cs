using GenshinVideoHelper.Core.Vision;
using OpenCvSharp;

namespace GenshinVideoHelper.Infrastructure.Vision;

/// <summary>Upper-left search with compact cyan anchors and robust radial edge fitting.</summary>
public static class DiskDetector
{
    private static readonly double[] Cos = Enumerable.Range(0, 120).Select(i => Math.Cos(i * Math.PI / 60)).ToArray();
    private static readonly double[] Sin = Enumerable.Range(0, 120).Select(i => Math.Sin(i * Math.PI / 60)).ToArray();
    private sealed record Candidate(MapDisk Disk, double Score);
    public static Mat ToBgr(VisionFrame frame)
    {
        using var input = Mat.FromPixelData(frame.Height, frame.Width, frame.Channels == 4 ? MatType.CV_8UC4 : MatType.CV_8UC3, frame.Pixels);
        var result = new Mat();
        if (frame.Channels == 4) Cv2.CvtColor(input, result, ColorConversionCodes.BGRA2BGR); else input.CopyTo(result);
        return result;
    }
    public static Mat Normalize(Mat source, VisionFrame frame, MapDisk disk, int size = 212, double radius = 100)
    {
        var sx = (double)frame.Region.Width / frame.Width; var sy = (double)frame.Region.Height / frame.Height;
        double factor = radius / disk.Radius;
        using var transform = Mat.FromArray(new double[,] { { factor * sx, 0, size / 2d - (disk.X - frame.Region.X) * factor },
            { 0, factor * sy, size / 2d - (disk.Y - frame.Region.Y) * factor } });
        var output = new Mat(); Cv2.WarpAffine(source, output, transform, new Size(size, size)); return output;
    }
    public static MapDisk? Detect(Mat source, VisionFrame frame)
    {
        // Incoming cold frames already describe the upper-left quarter. Never crop that quarter a second time.
        int scopeW = Math.Min(source.Width, (int)Math.Floor((frame.SourceWidth / 2d - frame.Region.X) * frame.Width / frame.Region.Width));
        int scopeH = Math.Min(source.Height, (int)Math.Floor((frame.SourceHeight / 2d - frame.Region.Y) * frame.Height / frame.Region.Height));
        if (scopeW < 40 || scopeH < 40) return null;
        using var scope = new Mat(source, new Rect(0, 0, scopeW, scopeH));
        var scale = Math.Min(1, 480d / scopeW);
        using var small = new Mat(); Cv2.Resize(scope, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var hsv = new Mat(); using var mask = new Mat(); using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(78, 125, 145), new Scalar(112, 255, 255), mask);
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        using var gray = new Mat(); using var gx = new Mat(); using var gy = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY); Cv2.GaussianBlur(gray, gray, new Size(3, 3), .65);
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0); Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1);
        var candidates = new List<Candidate>();
        var anchors = Enumerable.Range(1, n - 1).OrderByDescending(i => stats.At<int>(i, 4));
        int searched = 0;
        foreach (int i in anchors)
        {
            int bw = stats.At<int>(i, 2), bh = stats.At<int>(i, 3), area = stats.At<int>(i, 4);
            double compact = (double)area / (bw * bh);
            if (area < 4 || area > 6400 || Math.Min(bw, bh) < 2 || Math.Max(bw, bh) >= 150 ||
                (double)Math.Max(bw, bh) / Math.Min(bw, bh) >= 2.3 || compact <= .32 || compact >= .86) continue;
            using var localLabels = new Mat(labels, new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), bw, bh));
            using var local = new Mat(); Cv2.InRange(localLabels, new Scalar(i), new Scalar(i), local);
            Cv2.FindContours(local, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).FirstOrDefault();
            if (contour is null) continue;
            int vertices = Cv2.ApproxPolyDP(contour, .045 * Cv2.ArcLength(contour, true), true).Length;
            var hull = Cv2.ContourArea(Cv2.ConvexHull(contour));
            if (vertices < 3 || vertices > 8 || hull > 0 && Cv2.ContourArea(contour) / hull < .65) continue;
            Radial(small, gx, gy, centroids.At<double>(i, 0), centroids.At<double>(i, 1), area, Math.Max(bw, bh), candidates);
            if (++searched >= 36) break;
        }
        var ranked = candidates.Select(c => c with { Score = c.Score - (candidates.Any(o =>
            o.Disk.Radius > c.Disk.Radius * 1.06 && o.Disk.Radius < c.Disk.Radius * 1.3 &&
            Distance(o.Disk.X, o.Disk.Y, c.Disk.X, c.Disk.Y) < c.Disk.Radius * .08 && o.Score > c.Score - .18) ? .2 : 0) }).OrderByDescending(c => c.Score);
        var best = ranked.FirstOrDefault();
        if (best is null) return null;
        var sx = (double)frame.Region.Width / frame.Width / scale;
        var sy = (double)frame.Region.Height / frame.Height / scale;
        return new(frame.Region.X + best.Disk.X * sx, frame.Region.Y + best.Disk.Y * sy, best.Disk.Radius * (sx + sy) / 2);
    }
    private static void Radial(Mat bgr, Mat gx, Mat gy, double x, double y, int area, int glyph, List<Candidate> output)
    {
        double limit = Math.Min(Math.Min(gx.Width, gx.Height) * .49, Math.Min(Math.Min(x, y), Math.Min(gx.Width - 1 - x, gx.Height - 1 - y)) + 12);
        int first = Math.Max(12, glyph * 3), end = (int)Math.Ceiling(Math.Min(limit, glyph * 11)), count = end - first;
        if (count < 12) return;
        var weights = new double[120, count]; var energy = new float[count];
        for (int a = 0; a < 120; a++) for (int j = 0; j < count; j++)
        {
            var xx = x + Cos[a] * (j + first); var yy = y + Sin[a] * (j + first);
            var dx = SampleFloat(gx, xx, yy); var dy = SampleFloat(gy, xx, yy);
            double radial = Math.Abs(dx * Cos[a] + dy * Sin[a]);
            var weight = radial / Math.Max(1, Math.Sqrt(dx * dx + dy * dy)) > .8 ? Math.Min(90, radial) : 0;
            weights[a, j] = weight; if (weight > 12) energy[j] += 1f / 120;
        }
        using (var row = Mat.FromPixelData(1, count, MatType.CV_32F, energy)) Cv2.GaussianBlur(row, row, new Size(7, 1), 0);
        var peaks = new List<int>();
        foreach (int j in Enumerable.Range(0, count).OrderByDescending(j => energy[j]))
        {
            if (energy[j] < .12) break;
            if (peaks.Any(p => Math.Abs(j - p) < 8)) continue;
            peaks.Add(j); if (peaks.Count == 10) break;
        }
        foreach (int peak in peaks)
        {
            double radius = first + peak, band = Math.Max(4, radius * .07);
            var points = new List<Point2d>();
            for (int a = 0; a < 120; a++)
            {
                double score = double.NegativeInfinity; int selected = peak;
                for (int j = Math.Max(0, (int)Math.Floor(peak - band) + 1); j <= Math.Min(count - 1, (int)Math.Ceiling(peak + band) - 1); j++)
                {
                    double value = weights[a, j] - Math.Abs(j - peak) * 2;
                    if (value > score) { score = value; selected = j; }
                }
                if (weights[a, selected] > 12) points.Add(new(x + Cos[a] * (first + selected), y + Sin[a] * (first + selected)));
            }
            var fitted = Fit(points);
            if (fitted is not { } fit) continue;
            var d = fit.Disk; var offset = Distance(x, y, d.X, d.Y); double ratio = area / (d.Radius * d.Radius);
            if (d.Radius < Math.Max(13, Math.Min(gx.Width, gx.Height) * .1) || fit.Support < .6 ||
                offset > d.Radius * .17 || fit.Residual > 1.3 || ratio <= .0015 || ratio >= .09) continue;
            int changed = 0; double step = Math.Max(4, d.Radius * .065);
            for (int a = 0; a < 120; a++)
            {
                var inner = SampleColor(bgr, d.X + Cos[a] * (d.Radius - step), d.Y + Sin[a] * (d.Radius - step));
                var outer = SampleColor(bgr, d.X + Cos[a] * (d.Radius + step), d.Y + Sin[a] * (d.Radius + step));
                if ((Math.Abs(inner.Item0 - outer.Item0) + Math.Abs(inner.Item1 - outer.Item1) + Math.Abs(inner.Item2 - outer.Item2)) / 3 > 18) changed++;
            }
            double discontinuity = changed / 120d;
            if (fit.Support < .7 && discontinuity < .35) continue;
            output.Add(new(d, fit.Support + .15 * discontinuity - .1 * offset / d.Radius - .1 * fit.Residual));
        }
    }
    private static (MapDisk Disk, double Support, double Residual)? Fit(List<Point2d> points)
    {
        var kept = points; MapDisk disk = default; double[] errors = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            if (kept.Count < 18) return null;
            using var a = new Mat(kept.Count, 3, MatType.CV_64F); using var b = new Mat(kept.Count, 1, MatType.CV_64F); using var solution = new Mat();
            for (int j = 0; j < kept.Count; j++) { var p = kept[j]; a.Set(j, 0, 2 * p.X); a.Set(j, 1, 2 * p.Y); a.Set(j, 2, 1d); b.Set(j, 0, p.X * p.X + p.Y * p.Y); }
            if (!Cv2.Solve(a, b, solution, DecompTypes.SVD)) return null;
            var x = solution.At<double>(0); var y = solution.At<double>(1); var r2 = solution.At<double>(2) + x * x + y * y;
            if (r2 <= 0 || !double.IsFinite(r2)) return null;
            disk = new(x, y, Math.Sqrt(r2));
            kept = points.Where(p => Math.Abs(Distance(p.X, p.Y, x, y) - disk.Radius) < Math.Max(1.4, disk.Radius * .025)).ToList();
            errors = kept.Select(p => Math.Abs(Distance(p.X, p.Y, x, y) - disk.Radius)).Order().ToArray();
        }
        return errors.Length < 18 ? null : (disk, kept.Count / 120d, errors[errors.Length / 2]);
    }
    public static double Distance(double x, double y, double a, double b) => Math.Sqrt((x-a)*(x-a)+(y-b)*(y-b));
    public static unsafe double SampleFloat(Mat mat, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix + 1 >= mat.Width || iy + 1 >= mat.Height) return 0;
        var row = (float*)mat.Ptr(iy); var next = (float*)mat.Ptr(iy + 1); double fx = x - ix, fy = y - iy;
        return (row[ix] * (1-fx) + row[ix+1]*fx)*(1-fy)+(next[ix]*(1-fx)+next[ix+1]*fx)*fy;
    }
    public static Vec3d SampleColor(Mat mat, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix + 1 >= mat.Width || iy + 1 >= mat.Height) return default;
        var a = mat.At<Vec3b>(iy, ix); var b = mat.At<Vec3b>(iy, ix+1); var c = mat.At<Vec3b>(iy+1, ix); var d = mat.At<Vec3b>(iy+1, ix+1);
        var fx = x - ix; var fy = y - iy;
        return new((a.Item0*(1-fx)+b.Item0*fx)*(1-fy)+(c.Item0*(1-fx)+d.Item0*fx)*fy,
            (a.Item1*(1-fx)+b.Item1*fx)*(1-fy)+(c.Item1*(1-fx)+d.Item1*fx)*fy,
            (a.Item2*(1-fx)+b.Item2*fx)*(1-fy)+(c.Item2*(1-fx)+d.Item2*fx)*fy);
    }
    public static bool CheckRim(Mat normalized)
    {
        using var gray = new Mat(); Cv2.CvtColor(normalized, gray, ColorConversionCodes.BGR2GRAY);
        using var f = new Mat(); gray.ConvertTo(f, MatType.CV_32F);
        var peaks = new int[48]; var strength = new double[48]; var counts = new int[12];
        for (int a = 0; a < 48; a++)
        {
            var theta = a * Math.PI / 24; double previous = 0;
            for (int j = 0; j < 13; j++)
            {
                var r = 72 + j * 1.2; var value = SampleFloat(f, 96 + Math.Cos(theta)*r, 96 + Math.Sin(theta)*r);
                if (j > 0 && Math.Abs(value-previous) > strength[a]) { strength[a] = Math.Abs(value-previous); peaks[a] = j-1; }
                previous = value;
            }
            if (strength[a] > 5) counts[peaks[a]]++;
        }
        int mode = Enumerable.Range(0,12).MaxBy(j => counts[j]+(j>0?counts[j-1]:0)+(j<11?counts[j+1]:0));
        return Enumerable.Range(0,48).Count(a => strength[a]>5 && Math.Abs(peaks[a]-mode)<=1) >= 20;
    }
}
