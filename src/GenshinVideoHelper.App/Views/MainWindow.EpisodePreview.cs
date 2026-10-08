using System.Windows;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private bool SelectedVideoIsFollowing => _page is not null && _selectedIdentity is not null && Services.Follow.Session.Current?.Identity == _selectedIdentity;

    private void PrepareEpisodePreview(bool immediate = false, bool forceReload = false)
    {
        _previewDebounce.Stop();
        Services.Preview.Select(UrlInput.Text, forceReload);
        if (!Services.Preview.Current.Loading) return;
        if (immediate) _ = Dispatcher.BeginInvoke(() => _ = LoadSelectedEpisodesAsync());
        else _previewDebounce.Start();
    }

    private void DisplayPreview(EpisodePreview preview)
    {
        UpdateEpisodeControls();
        EpisodeRetryButton.Visibility = preview.Error is null ? Visibility.Collapsed : Visibility.Visible;
        if (preview.Error is not null) EpisodeStatusText.Text = $"分集读取失败：{preview.Error}";
        else if (preview.Loading) EpisodeStatusText.Text = "正在读取所选视频的分集…";
        else if (preview.Identity is null) EpisodeStatusText.Text = string.IsNullOrWhiteSpace(UrlInput.Text)
            ? "选择地图视频或输入链接后，自动读取分集。" : "请输入有效的 B 站视频链接或完整 BV 号。";
    }

    private Task LoadSelectedEpisodesAsync()
    {
        _previewDebounce.Stop();
        var page = _page is not null && Services.Follow.Session.Current?.Identity.Bvid == _selectedIdentity?.Bvid ? _page : null;
        return Services.Preview.LoadAsync(page);
    }

    private Task MoveSelectedEpisodeAsync(int direction)
    {
        if (_selectedInfo is null || _selectedIdentity is null) return Task.CompletedTask;
        var index = _selectedInfo.Episodes.ToList().FindIndex(p => p.Number == _selectedIdentity.Part);
        var next = index + direction;
        return index >= 0 && next >= 0 && next < _selectedInfo.Episodes.Count ? SwitchEpisodeAsync(_selectedInfo.Episodes[next].Number) : Task.CompletedTask;
    }

    private Task SwitchEpisodeAsync(int part)
    {
        if (_selectedInfo is null || _selectedIdentity is null || !_selectedInfo.Episodes.Any(p => p.Number == part) || _selectedIdentity.Part == part) return Task.CompletedTask;
        if (SelectedVideoIsFollowing) return NavigateActiveEpisodeAsync(part);
        UrlInput.Text = new VideoIdentity(_selectedInfo.Bvid, part).Url;
        SetStatus($"已选择 P{part}，点击“开始跟随”播放。");
        return Task.CompletedTask;
    }
}
