using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class LifecycleTests
{
    public static void LifecycleUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => LifecycleUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    public static async Task LifecycleUiTestAsync(string root)
    {
        await using var owned = await ChromeFixture.StartAsync(root, headed: true);
        await using var unrelated = await ChromeFixture.StartAsync(root, headed: true);
        using var ownedBrowser = new ChromeBrowser(owned.ProfileDirectory);
        using var unrelatedBrowser = new ChromeBrowser(unrelated.ProfileDirectory);
        // GetBrowserProcessId is normally called after Open; establish only a profile-scoped connection here.
        await ownedBrowser.GetPagesAsync();
        await unrelatedBrowser.GetPagesAsync();
        var ownedPid = await ownedBrowser.GetBrowserProcessIdAsync();
        var unrelatedPid = await unrelatedBrowser.GetBrowserProcessIdAsync();
        using var ownedProcess = Process.GetProcessById(ownedPid);
        using var unrelatedProcess = Process.GetProcessById(unrelatedPid);
        _ = ownedProcess.Handle; _ = unrelatedProcess.Handle;
        var controller = new VideoController();
        for (var i = 0; i < 80; i++)
        {
            try { await controller.ExecuteAsync(owned.Page, new("ensurePip")); break; }
            catch (VideoNotReadyException) { await Task.Delay(200); }
        }
        var pip = PipWindowService.FindPip(ownedPid);
        Check(pip != 0, "Lifecycle starts with real native PiP");
        var window = new GenshinVideoHelper.App.MainWindow(new AppServices(new AppSettings { VideoUrl = "", SelectedVideoLibraryId = null }, owned.ProfileDirectory, episodeFactory: PreviewService))
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var tray = GetField<TrayIconService>(window, "_tray")!;
            Check(window.Icon is not null && tray.IsVisible, "Window/taskbar icon and tray share original icon asset");
            var notify = GetField<System.Windows.Forms.NotifyIcon>(tray, "_notify")!;
            Check(notify.Icon is { Width: 32, Height: 32 }, "Tray loads 32px icon frame");
            window.WindowState = WindowState.Minimized;
            await WaitForAsync(() => !window.IsVisible && tray.IsVisible, "Minimize hides helper while tray remains", window);
            Check(!ownedProcess.HasExited && PipWindowService.FindPip(ownedPid) != 0, "Minimizing retains browser and PiP");
            var menu = GetField<System.Windows.Forms.ContextMenuStrip>(tray, "_menu")!;
            ((System.Windows.Forms.ToolStripMenuItem)menu.Items[0]).PerformClick();
            await WaitForAsync(() => window.IsVisible && window.WindowState == WindowState.Normal, "Tray menu restores helper", window);
            window.WindowState = WindowState.Minimized;
            ((System.Windows.Forms.ToolStripMenuItem)menu.Items[2]).PerformClick();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            Check(ownedProcess.HasExited && PipWindowService.FindPip(ownedPid, includeHidden: true) == 0, "Tray exit closes dedicated browser and native PiP");
            Check(!tray.IsVisible && !GetField<HotkeyService>(window, "_hotkeys")!.IsRegistered(HotkeyAction.TogglePlayback), "Exit removes tray and releases hotkeys");
            Check(!unrelatedProcess.HasExited && (await new CdpClient().SendAsync(unrelated.BrowserSocket, "Browser.getVersion", new { })).ValueKind == JsonValueKind.Object,
                "Another Chrome profile remains alive and responsive");

            var staleProfile = Path.Combine(root, "artifacts", "stale-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staleProfile);
            var staleLines = await File.ReadAllLinesAsync(Path.Combine(unrelated.ProfileDirectory, "DevToolsActivePort"));
            await File.WriteAllLinesAsync(Path.Combine(staleProfile, "DevToolsActivePort"), [staleLines[0], "/devtools/browser/wrong-session"]);
            using var stale = new ChromeBrowser(staleProfile);
            await stale.CloseAsync();
            Check(!unrelatedProcess.HasExited, "Stale port file cannot close a different browser session");
            var startupProfile = Path.Combine(root, "artifacts", "startup-exit-" + Guid.NewGuid().ToString("N"));
            var startupWindow = new GenshinVideoHelper.App.MainWindow(new AppServices(new AppSettings { VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3", SelectedVideoLibraryId = null }, startupProfile, episodeFactory: PreviewService))
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                startupWindow.Show();
                var startupBrowser = startupWindow.Services.Browser;
                var open = (Task)Invoke(startupWindow, "OpenVideoAsync")!;
                await WaitForAsync(() => GetField<List<Process>>(startupBrowser, "_launchedProcesses")!.Count > 0, "Chrome launch enters owned-process tracking", startupWindow);
                var launched = GetField<List<Process>>(startupBrowser, "_launchedProcesses")!.ToArray();
                var identities = launched.Select(process => Process.GetProcessById(process.Id)).ToArray();
                try
                {
                    foreach (var process in identities) _ = process.Handle;
                    startupWindow.Close();
                    await startupWindow.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
                    await open.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(identities.All(process => process.HasExited), "Exit during launch leaves no owned Chrome process");
                    Check(!unrelatedProcess.HasExited, "Startup cancellation still preserves another Chrome session");
                }
                finally { foreach (var process in identities) process.Dispose(); }
            }
            finally { startupWindow.Close(); await startupWindow.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
            Console.WriteLine("Lifecycle: tray icon/menu, minimize/restore, menu exit, owned browser/PiP close, hotkey release, other-profile isolation and stale-port safety and exit during launch passed.");
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
}
