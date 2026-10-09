using System.IO;

namespace GenshinVideoHelper.Smoke;

/// <summary>Throwaway directories live under artifacts/test-run and are removed by the test that made them;
/// whatever a crashed run leaves behind is swept at the next start.</summary>
internal static class TestArtifacts
{
    private static string RunRoot(string root) => Path.GetFullPath(Path.Combine(root, "artifacts", "test-run"));
    public static string PathFor(string root, string name) => Path.Combine(RunRoot(root), name);

    public static TempDirectory CreateTemp(string root, string prefix)
    {
        var path = PathFor(root, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new(RunRoot(root), path);
    }

    public static void SweepStale(string root)
    {
        var runRoot = RunRoot(root);
        if (!Directory.Exists(runRoot)) return;
        foreach (var directory in Directory.GetDirectories(runRoot)) Delete(runRoot, directory, attempts: 1);
        foreach (var file in Directory.GetFiles(runRoot))
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Latency runs are named by timestamp, so ordinal order is chronological.</summary>
    public static void PruneLatency(string root, int keep = 10)
    {
        var latency = Path.GetFullPath(Path.Combine(root, "artifacts", "latency"));
        if (!Directory.Exists(latency)) return;
        foreach (var directory in Directory.GetDirectories(latency).OrderDescending(StringComparer.Ordinal).Skip(keep))
            Delete(latency, directory, attempts: 1);
    }

    // Chrome can hold profile files briefly after its process exits.
    private static void Delete(string parent, string path, int attempts)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        for (var attempt = 1; ; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts) { Console.WriteLine($"Test artifact left for the next sweep: {path} ({ex.Message})"); return; }
                Thread.Sleep(250);
            }
        }
    }

    internal sealed class TempDirectory(string parent, string path) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose() => Delete(parent, Path, attempts: 12);
    }
}
