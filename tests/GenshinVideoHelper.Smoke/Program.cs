using System.IO;
namespace GenshinVideoHelper.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--focus-fixture"))
            {
                var fixture = new System.Windows.Window { Title = "画中画测试 · 模拟游戏前台", Width = 400, Height = 200,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    Content = new System.Windows.Controls.TextBlock { Text = "正在验证画中画保持前台焦点", Margin = new System.Windows.Thickness(20) } };
                fixture.Loaded += (_, _) => NativeInput.ShowWindow(new System.Windows.Interop.WindowInteropHelper(fixture).Handle, 8);
                new System.Windows.Application().Run(fixture);
                return 0;
            }
            var root = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
            SmokeBrowser.Kind = args.Contains("--edge") ? Core.Models.BrowserKind.Edge : Core.Models.BrowserKind.Chrome;
            TestArtifacts.SweepStale(root);
            if (args.Contains("--display")) DisplayTests.Run(root);
            else if (args.Contains("--browser-switch")) BrowserSwitchTests.Run(root);
            else if (args.Contains("--vision-browser")) VisionTests.RunBrowser(root);
            else if (args.Contains("--vision")) VisionTests.Run(root);
            else if (args.Contains("--login")) LoginTests.Run(root);
            else if (args.Contains("--render-ui")) RenderUi(root);
            else if (args.Contains("--history")) HistoryUiTests.Run(root);
            else if (args.Contains("--library")) LibraryUiTests.Run(root);
            else if (args.Contains("--progress-ui")) ProgressUiTests.RunStandalone(root);
            else if (args.Contains("--browser")) BrowserTestAsync(root, headed: false).GetAwaiter().GetResult();
            else if (args.Contains("--pip")) PipTest(root);
            else if (args.Contains("--pip-shell")) PipShellTests.Run(root);
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
