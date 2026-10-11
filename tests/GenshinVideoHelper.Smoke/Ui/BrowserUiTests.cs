using System.Windows.Controls;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;

namespace GenshinVideoHelper.Smoke;

internal static class BrowserUiTests
{
    private sealed class Catalog(IReadOnlyList<BrowserInstallation> installed) : IBrowserCatalog
    { public IReadOnlyList<BrowserInstallation> Detect() => installed; }
    private sealed class Store : ISettingsStore
    {
        public BrowserPreference? Saved;
        public SettingsLoadResult Load() => new(new());
        public void Save(AppSettings settings) => Saved = settings.BrowserPreference;
    }
    private sealed class Browser : IBrowserSession
    {
        public Task<BrowserPage> OpenAsync(string url, CancellationToken token = default) => Task.FromResult(new BrowserPage("test", "video", url, "ws://test"));
        public Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default) => Task.CompletedTask;
        public Task<BrowserPage?> GetPageAsync(string id, CancellationToken token = default) => Task.FromResult<BrowserPage?>(null);
        public Task<int> GetBrowserProcessIdAsync(CancellationToken token = default) => Task.FromResult(0);
        public Task CloseAsync() => Task.CompletedTask;
    }

    public static void Run(string root)
    {
        var settings = new AppSettings { HotkeysEnabled = false };
        var store = new Store();
        var both = new Catalog([new(BrowserKind.Chrome, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "155.0.0.1"),
            new(BrowserKind.Edge, @"C:\Users\developer\AppData\Local\Microsoft\Edge\Application\msedge.exe", "155.0.0.2")]);
        using var manager = new BrowserSessionManager(settings, both, _ => new Browser());
        var window = new MainWindow(new AppServices(settings, TestArtifacts.PathFor(root, "browser-ui"), store,
            browser: manager, browserCatalog: both, episodeFactory: PreviewService));
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        var selector = (ComboBox)window.FindName("BrowserSelector");
        Check(((ComboBoxItem)selector.SelectedItem).Tag.Equals("Auto"), "Browser defaults to Auto");
        manager.WarmupAsync().GetAwaiter().GetResult();
        Check(((TextBlock)window.FindName("ActiveBrowserText")).Text.Contains("Chrome"), "Warmup reports actual Chrome selection");
        selector.SelectedIndex = 2;
        Check(store.Saved == BrowserPreference.Edge && manager.ActiveBrowser!.Kind == BrowserKind.Chrome,
            "Browser selection saves immediately without applying to current session");
        Check(((TextBlock)window.FindName("BrowserSelectionHint")).Text.Contains("下次“开始跟随”时使用 Edge"), "Pending browser change is explained");
        Capture(window, root, "ui-browser-pending.png", 1020, 730);
        Capture(window, root, "ui-browser-pending-compact.png", 860, 600);
        Check(((System.Windows.FrameworkElement)window.FindName("BrowserCard")).ActualHeight == 128, "Browser card keeps its fixed compact height");
        window.Left = -10000; window.Top = -10000; window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        CaptureMenu(window, selector, root, "ui-browser-menu.png");
        manager.CloseAsync().GetAwaiter().GetResult();
        window.Close();

        var missingSettings = new AppSettings { HotkeysEnabled = false, BrowserPreference = BrowserPreference.Edge };
        var missing = new MainWindow(new AppServices(missingSettings, TestArtifacts.PathFor(root, "browser-ui-missing"),
            browserCatalog: new Catalog([]), episodeFactory: PreviewService));
        ((RadioButton)missing.FindName("NavSettings")).IsChecked = true;
        var choices = (ComboBox)missing.FindName("BrowserSelector");
        Check(!((ComboBoxItem)choices.Items[1]).IsEnabled && !((ComboBoxItem)choices.Items[2]).IsEnabled,
            "Uninstalled browsers cannot be newly selected");
        Check(choices.SelectedIndex == 2 && ((TextBlock)missing.FindName("BrowserSelectionHint")).Text.Contains("已失效"),
            "Saved unavailable manual choice is retained and reported");
        Capture(missing, root, "ui-browser-missing.png", 1020, 730);
        Capture(missing, root, "ui-browser-missing-compact.png", 860, 600);
        choices.SelectedIndex = 0;
        Check(((TextBlock)missing.FindName("BrowserSelectionHint")).Text.Contains("未检测到"), "No-install Auto state has installation guidance");
        missing.Close();
    }
}
