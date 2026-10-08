using System.Threading;
using System.Windows;

namespace GenshinVideoHelper.App;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, "Local\\GenshinVideoHelper.Desktop", out var created);
        if (!created)
        {
            MessageBox.Show("GenshinVideoHelper 已经在运行，请从系统托盘或任务栏打开现有窗口。", "GenshinVideoHelper");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
