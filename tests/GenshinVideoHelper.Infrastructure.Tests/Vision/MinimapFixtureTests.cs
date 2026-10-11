using System.Runtime.InteropServices;
using System.Text.Json;
using GenshinVideoHelper.Core.Vision;
using GenshinVideoHelper.Infrastructure.Vision;
using OpenCvSharp;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Vision;

public sealed partial class MinimapFixtureTests(ITestOutputHelper output)
{
    private static string BasicRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Minimap", "basic");
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static void Check(bool condition, string message) => Assert.True(condition, message);

    [Fact]
    public void BasicDiskSamplesAlwaysMatchFrozenGeometry()
    {
        using var manifest = Read(Path.Combine(BasicRoot, "manifest.json"));
        var samples = manifest.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        Check(samples.Length == 8, "The versioned corpus must contain eight required images.");
        foreach (var sample in samples)
        {
            using var image = LoadBasic(sample);
            using var frame = SampleFrame(image, sample);
            var disk = GuideDiskDetector.Detect(image, frame);
            var expected = sample.GetProperty("expected_disk");
            Check(disk.HasValue == (expected.ValueKind != JsonValueKind.Null), $"Detection: {sample.GetProperty("id")}");
            if (disk is not { } actual) continue;
            var reference = Disk(expected);
            Check(MinimapImage.Distance(actual.X, actual.Y, reference.X, reference.Y) < .1, "Frozen disk centre");
            Check(Math.Abs(actual.Radius - reference.Radius) <= 2.1, "Frozen disk radius");
        }
        output.WriteLine("Basic corpus: all eight circle/absence references passed.");
    }

    [Fact]
    public void BasicTerrainSamplesRecoverPositionAndRejectInvalidGuides()
    {
        using var manifest = Read(Path.Combine(BasicRoot, "manifest.json"));
        var samples = manifest.RootElement.GetProperty("samples").EnumerateArray()
            .ToDictionary(sample => sample.GetProperty("id").GetString()!);
        using var analyzer = new MinimapAnalyzer();
        foreach (var pair in manifest.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var guideSample = samples[pair.GetProperty("guide").GetString()!];
            var userSample = samples[pair.GetProperty("user").GetString()!];
            using var guide = LoadBasic(guideSample);
            using var user = LoadBasic(userSample);
            using var guideFrame = SampleFrame(guide, guideSample);
            var expected = pair.GetProperty("expected_normalized_point");
            double x, y;
            if (pair.GetProperty("fixed_user_hud").GetBoolean())
            {
                using var userFrame = GameFrame(user, userSample);
                var result = analyzer.Analyze(guideFrame, userFrame, false);
                Check(result.Marker is not null, result.Status);
                var marker = result.Marker!;
                Check(marker.Disk == MinimapGeometry.GameDisk(userFrame.SourceHeight), "Fixed user disk and source coordinates");
                x = 106 + (marker.X - marker.Disk.X) * 100 / marker.Disk.Radius;
                y = 106 + (marker.Y - marker.Disk.Y) * 100 / marker.Disk.Radius;
            }
            else
            {
                using var userFrame = SampleFrame(user, userSample);
                var disk = GuideDiskDetector.Detect(guide, guideFrame)!.Value;
                using var guideMap = MinimapImage.Normalize(guide, guideFrame, disk);
                using var userMap = MinimapImage.Normalize(user, userFrame, Disk(guideSample.GetProperty("expected_disk")));
                var result = analyzer.Register(guideMap, userMap);
                Check(result.HasValue, analyzer.LastRegistrationFailure);
                var match = result!.Value;
                (x, y) = MapProjection.Position(match.A, match.B, match.Tx, match.Ty);
            }
            Check(MinimapImage.Distance(x, y, expected[0].GetDouble(), expected[1].GetDouble()) <= 2,
                $"Real-pair position: {pair.GetProperty("id")}");
        }

        using var gamePixels = LoadBasic(samples["user-start"]);
        using var gameFrame = GameFrame(gamePixels, samples["user-start"]);
        foreach (var sample in samples.Values.Where(sample => sample.GetProperty("expected_disk").ValueKind == JsonValueKind.Null))
        {
            using var guidePixels = LoadBasic(sample);
            using var guideFrame = SampleFrame(guidePixels, sample);
            var result = analyzer.Analyze(guideFrame, gameFrame, false);
            Check(result.Marker is null && !result.RetainMarker, "Invalid guide never creates or retains a marker");
        }
        using var unrelated = LoadBasic(samples["guide-start"]);
        using var unrelatedFrame = SampleFrame(unrelated, samples["guide-start"]);
        Check(analyzer.Analyze(unrelatedFrame, gameFrame, false).Marker is null, "Unrelated real terrain must not mark");

        foreach (var id in new[] { "user-start", "user-three-seconds" })
        {
            var sample = samples[id];
            using var pixels = LoadBasic(sample);
            using var frame = SampleFrame(pixels, sample);
            using var map = MinimapImage.Normalize(pixels, frame, Disk(sample.GetProperty("expected_disk")));
            var result = CheckTerrainControls(analyzer, map, id == "user-start" ? 0 : 30);
            Check(result.Controls == 8 && result.NegativeMarks == 0, "Known transformations and false-marker controls");
        }
        output.WriteLine("Basic corpus: real pairs, physical coordinates, 16 known transforms and negative controls passed.");
    }

    private static Mat LoadBasic(JsonElement sample) => LoadImage(Path.Combine(BasicRoot, sample.GetProperty("file").GetString()!));
    private static Mat LoadImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required vision fixture is missing.", path);
        var image = Cv2.ImRead(path);
        if (!image.Empty()) return image;
        image.Dispose();
        throw new InvalidDataException($"Cannot decode vision fixture: {path}");
    }
    private static MapDisk Disk(JsonElement disk) => new(disk.GetProperty("x").GetDouble(), disk.GetProperty("y").GetDouble(), disk.GetProperty("radius").GetDouble());
    private static VisionFrame SampleFrame(Mat image, JsonElement sample)
    {
        var size = sample.GetProperty("source_size");
        return Frame(image, size[0].GetInt32(), size[1].GetInt32());
    }
    private static VisionFrame GameFrame(Mat image, JsonElement sample)
    {
        var size = sample.GetProperty("source_size");
        var region = MinimapGeometry.GameCaptureRegion(size[0].GetInt32(), size[1].GetInt32());
        using var crop = new Mat(image, new Rect(region.X, region.Y, region.Width, region.Height));
        return Frame(crop, size[0].GetInt32(), size[1].GetInt32(), region);
    }
    private static VisionFrame Frame(Mat image, int width, int height, FrameRegion? region = null)
    {
        using var copy = image.Clone();
        var frame = new VisionFrame(image.Width, image.Height, 3, width, height,
            region ?? new FrameRegion(0, 0, image.Width, image.Height), "offline");
        Marshal.Copy(copy.Data, frame.Pixels, 0, image.Width * image.Height * 3);
        return frame;
    }
    private static string FullRoot()
    {
        var besideOutput = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Minimap", "full");
        if (Directory.Exists(besideOutput)) return besideOutput;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "Minimap");
            if (File.Exists(Path.Combine(candidate, "basic", "manifest.json"))) return Path.Combine(candidate, "full");
        }
        return besideOutput;
    }
}
