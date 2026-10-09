using System.Windows;
using System.Windows.Controls;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private VideoLibrary? _activeLibrary;
    private bool _updatingLibraryControls;

    private void InitializeLibraries()
    {
        _updatingLibraryControls = true;
        try
        {
            PopulateLibrary(_libraryCatalog.Libraries.FirstOrDefault(library => library.Id == _settings.SelectedVideoLibraryId));
            if (_activeLibrary is not null && (!VideoIdentity.TryParse(UrlInput.Text, out var recent) || !_activeLibrary.Videos.Any(video => video.Bvid == recent!.Bvid)))
                UrlInput.Text = _activeLibrary.Videos[0].Url;
            SyncGuideVideoSelection();
        }
        finally { _updatingLibraryControls = false; }
        if (_libraryCatalog.Errors.Count > 0)
            SetStatus($"部分视频库加载失败，仍可自行输入链接：{string.Join("；", _libraryCatalog.Errors)}", true);
        else if (_settings.SelectedVideoLibraryId is not null && _activeLibrary is null)
            SetStatus("配置中选择的视频库不存在，请点击“更换库”重新选择；仍可自行输入链接。", true);
    }

    private void PopulateLibrary(VideoLibrary? library)
    {
        _activeLibrary = library;
        GuideVideoSelector.ItemsSource = library?.Videos ?? [];
        GuideVideoSelector.IsEnabled = library is not null;
        SyncGuideVideoSelection();
        LibrarySummaryText.Text = library?.DisplayName ?? "手动输入攻略链接";
        LibraryVideoCountText.Text = library is null ? "" : $"{library.Videos.Count} 个视频";
        LibraryVideoCountText.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool SelectLibrary(VideoLibrary? library)
    {
        var previous = _settings.SelectedVideoLibraryId;
        _settings.SelectedVideoLibraryId = library?.Id;
        if (!SaveSettings()) { _settings.SelectedVideoLibraryId = previous; return false; }
        AppLog.Info("App", $"用户选择视频库 {library?.Id ?? "手动输入"}。");
        _updatingLibraryControls = true;
        try
        {
            PopulateLibrary(library);
            if (library is not null) UrlInput.Text = library.Videos[0].Url;
            SyncGuideVideoSelection();
        }
        finally { _updatingLibraryControls = false; }
        PrepareEpisodePreview(immediate: true);
        SetStatus(library is null ? "已切换为手动输入，当前视频继续播放。" : $"已选择 {library.DisplayName}，候选列表已更新。");
        return true;
    }

    private void GuideVideoSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingLibraryControls || GuideVideoSelector.SelectedItem is not LibraryVideo video) return;
        _updatingLibraryControls = true;
        try { UrlInput.Text = video.Url; }
        finally { _updatingLibraryControls = false; }
        PrepareEpisodePreview(immediate: true);
        SetStatus($"已选择 {video.Title}，点击“开始跟随”打开。");
    }

    private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _updatingLibraryControls) return;
        _updatingLibraryControls = true;
        try { SyncGuideVideoSelection(); }
        finally { _updatingLibraryControls = false; }
        PrepareEpisodePreview();
    }

    private void SyncGuideVideoSelection()
    {
        if (GuideVideoSelector.ItemsSource is not IEnumerable<LibraryVideo> videos) return;
        var bvid = VideoIdentity.TryParse(UrlInput.Text, out var identity) ? identity!.Bvid : null;
        GuideVideoSelector.SelectedItem = videos.FirstOrDefault(video => video.Bvid == bvid);
    }

    private void ChangeLibrary_Click(object sender, RoutedEventArgs e)
    {
        var picker = new LibraryPickerWindow(_libraryCatalog, _settings.SelectedVideoLibraryId) { Owner = this };
        if (picker.ShowDialog() == true) SelectLibrary(picker.SelectedLibrary);
    }
}
