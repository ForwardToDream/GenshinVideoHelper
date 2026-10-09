using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Infrastructure.Diagnostics;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.App;

public partial class App : Application
{
    private Mutex? _instance;
    private FileLogSink? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, "Local\\GenshinVideoHelper.Desktop", out var created);
        if (!created)
        {
            MessageBox.Show("GenshinVideoHelper 已经在运行，请从系统托盘或任务栏打开现有窗口。", "GenshinVideoHelper");
            Shutdown();
            return;
        }
        StartLogging();
        base.OnStartup(e);
    }

    private void StartLogging()
    {
        _log = new FileLogSink(ApplicationPaths.LogDirectory);
        AppLog.Sink = _log;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppLog.Info("App", $"启动 {version}，{Environment.OSVersion}，.NET {Environment.Version}，PID {Environment.ProcessId}。");
        AppLog.Info("App", $"程序目录 {AppContext.BaseDirectory}，配置 {ApplicationPaths.SettingsPath}，日志 {_log.FilePath}。");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("App", "界面线程出现未处理的异常。", e.Exception);
        // Before the window exists there is nothing to keep alive; afterwards an event handler's failure
        // must not take the helper down and strand its Chrome.
        if (MainWindow is MainWindow { IsLoaded: true } window)
        {
            e.Handled = true;
            window.ReportInternalError(e.Exception);
        }
        else _log?.Flush(TimeSpan.FromSeconds(1));
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("App", e.IsTerminating ? "未处理的异常，进程即将终止。" : "未处理的异常。", e.ExceptionObject as Exception);
        _log?.Flush(TimeSpan.FromSeconds(1));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Warn("App", "后台任务的异常未被观察。", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_log is not null)
        {
            AppLog.Info("App", $"退出，代码 {e.ApplicationExitCode}。");
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            AppLog.Sink = null;
            _log.Dispose();
        }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
