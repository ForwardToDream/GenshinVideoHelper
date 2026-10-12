using System.Windows;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Infrastructure.Updates;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private static readonly Version CurrentVersion = ReadCurrentVersion();
    private readonly CancellationTokenSource _updateLifetime = new();
    private ReleaseUpdate? _availableUpdate;
    private bool _startupUpdateChecked, _checkingUpdate;

    private static Version ReadCurrentVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        return new(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private void InitializeUpdates()
    {
        VersionText.Text = $"v{CurrentVersion}";
        CurrentVersionText.Text = $"当前版本 {CurrentVersion}";
        Loaded += async (_, _) =>
        {
            if (_startupUpdateChecked || _closing || Services.Updates is null) return;
            _startupUpdateChecked = true;
            await CheckForUpdatesAsync();
        };
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (_closing || _checkingUpdate || Services.Updates is null) return;
        _checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStateText.Text = "正在检查更新…";
        try
        {
            var update = await Services.Updates.CheckAsync(CurrentVersion, _updateLifetime.Token);
            if (_closing) return;
            _availableUpdate = update;
            UpdateNoticeButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateNoticeButton.Content = update is null ? "" : $"发现新版本 {update.Version} · 查看更新";
            UpdateStateText.Text = update is null ? "暂无新版本" : $"发现新版本 {update.Version}，点击下载页手动更新。";
            AppLog.Info("Update", update is null ? "未发现新版本。" : $"发现新版本 {update.Version}。");
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closing) UpdateStateText.Text = "暂时无法检查更新，可稍后重试。";
            AppLog.Warn("Update", "更新检查未完成。", ex);
        }
        finally
        {
            _checkingUpdate = false;
            if (!_closing) CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenUpdate_Click(object sender, RoutedEventArgs e) =>
        OpenWithShell((_availableUpdate?.Page ?? GitHubUpdateChecker.ReleasesPage).AbsoluteUri);

    private void StopUpdates()
    {
        _updateLifetime.Cancel();
        _updateLifetime.Dispose();
    }
}
