using System.Windows;
using System.Windows.Controls;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Infrastructure.Browser;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private sealed record BrowserChoice(string Name, string Header, string Detail)
    {
        public override string ToString() => Name;
    }
    private IReadOnlyList<BrowserInstallation> _installedBrowsers = [];
    private bool _updatingBrowserControls;

    private void InitializeBrowsers()
    {
        _installedBrowsers = Services.BrowserCatalog.Detect();
        if (Services.Browser is BrowserSessionManager manager) manager.ActiveBrowserChanged += OnActiveBrowserChanged;
        UpdateBrowserControls();
    }

    private void OnActiveBrowserChanged()
    {
        if (_closing) return;
        if (Dispatcher.CheckAccess()) UpdateBrowserControls();
        else Dispatcher.BeginInvoke(() => { if (!_closing) UpdateBrowserControls(); });
    }

    private void UpdateBrowserControls()
    {
        _updatingBrowserControls = true;
        foreach (ComboBoxItem item in BrowserSelector.Items)
        {
            var preference = Enum.Parse<BrowserPreference>((string)item.Tag);
            var installation = BrowserSelection.Resolve(preference, _installedBrowsers);
            var installed = preference == BrowserPreference.Auto || installation is not null;
            item.IsEnabled = installed;
            var name = preference == BrowserPreference.Auto ? "自动" : preference + (installed ? "" : "（未安装）");
            item.Content = new BrowserChoice(name,
                preference == BrowserPreference.Auto ? "自动 · Chrome 优先" : installation is null ? name : $"{name} · {installation.Version}",
                preference == BrowserPreference.Auto ? "优先使用已安装的 Chrome，其次 Edge。" : installation?.ExecutablePath ?? "未找到桌面版浏览器");
            if (preference == _settings.BrowserPreference) BrowserSelector.SelectedItem = item;
        }
        _updatingBrowserControls = false;
        var active = (Services.Browser as BrowserSessionManager)?.ActiveBrowser;
        ActiveBrowserText.Text = active is null ? "当前使用：尚未启动" : $"当前使用：{active.DisplayName}";
        var selected = BrowserSelection.Resolve(_settings.BrowserPreference, _installedBrowsers);
        var missing = selected is null;
        BrowserSelectionHint.Text = missing
            ? _settings.BrowserPreference == BrowserPreference.Auto ? "未检测到 Chrome 或 Edge，安装后请重新检测。" : $"所选 {_settings.BrowserPreference} 已失效，请重新选择。"
            : active is not null && (active.Kind != selected!.Kind || !string.Equals(active.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                ? $"已保存，下次“开始跟随”时使用 {selected.DisplayName}。当前视频继续播放。"
                : _settings.BrowserPreference == BrowserPreference.Auto ? "自动优先选择 Chrome，其次 Edge。" : "选择已保存，下次“开始跟随”时生效。";
        BrowserSelectionHint.Foreground = (System.Windows.Media.Brush)FindResource(missing ? "WarningBrush" : "SecondaryTextBrush");
    }

    private void Browser_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingBrowserControls || BrowserSelector.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        var preference = Enum.Parse<BrowserPreference>(value);
        if (preference != BrowserPreference.Auto && BrowserSelection.Resolve(preference, _installedBrowsers) is null)
        { UpdateBrowserControls(); return; }
        _settings.BrowserPreference = preference;
        SaveSettings();
        UpdateBrowserControls();
    }

    private async void BrowserDetect_Click(object sender, RoutedEventArgs e)
    {
        BrowserDetectButton.IsEnabled = false;
        try
        {
            var installed = await Task.Run(Services.BrowserCatalog.Detect);
            if (_closing) return;
            _installedBrowsers = installed;
            UpdateBrowserControls();
        }
        catch (Exception ex) { AppLog.Warn("Browser", "浏览器检测失败。", ex); if (!_closing) SetStatus("浏览器检测失败，请重试。", true); }
        finally { if (!_closing) BrowserDetectButton.IsEnabled = true; }
    }
}
