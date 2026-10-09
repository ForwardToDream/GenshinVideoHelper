using System.IO;
namespace GenshinVideoHelper.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
            TestArtifacts.SweepStale(root);
            if (args.Contains("--render-ui")) RenderUi(root);
            else if (args.Contains("--browser")) BrowserTestAsync(root, headed: false).GetAwaiter().GetResult();
            else if (args.Contains("--pip")) PipTest(root);
            else if (args.Contains("--bilibili")) BilibiliTestAsync(root).GetAwaiter().GetResult();
            else if (args.Contains("--bilibili-pip")) PipTest(root, example: true);
            else if (args.Contains("--preview")) PreviewUiTest(root);
            else if (args.Contains("--lifecycle")) LifecycleUiTest(root);
            else if (args.Contains("--follow")) FollowUiTest(root);
            else if (args.Contains("--progress")) ProgressTests.Run(root);
            else if (args.Contains("--background")) LatencyTests.Run(root, background: true);
            else if (args.Contains("--latency")) LatencyTests.Run(root);
            else throw new ArgumentException("Specify a desktop smoke mode; run core and infrastructure tests with dotnet test.");
            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
