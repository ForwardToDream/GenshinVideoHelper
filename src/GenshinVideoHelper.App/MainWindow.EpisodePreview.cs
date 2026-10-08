using System.Windows;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Browser;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private readonly Dictionary<string, BilibiliVideoInfo> _episodeCache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _previewCancellation;
    private PreviewRequest? _previewRequest;
    private long _previewVersion;
    private long _previewLoadingVersion;
    private Task? _previewTask;
    private VideoIdentity? _selectedIdentity;
    private BilibiliVideoInfo? _selectedInfo;
    private sealed record PreviewRequest(long Version, VideoIdentity Identity, CancellationToken Token);
    private bool SelectedVideoIsFollowing => _page is not null && _selectedIdentity is not null && _follow.Current?.Identity == _selectedIdentity;

    private void PrepareEpisodePreview(bool immediate = false, bool forceReload = false)
    {
        _previewDebounce.Stop();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _previewRequest = null;
        var version = ++_previewVersion;
        _selectedInfo = null;
        EpisodeRetryButton.Visibility = Visibility.Collapsed;
        if (!VideoIdentity.TryParse(UrlInput.Text, out var identity))
        {
            _selectedIdentity = null;
            UpdateEpisodeControls();
            EpisodeStatusText.Text = string.IsNullOrWhiteSpace(UrlInput.Text) ? "选择地图视频或输入链接后，自动读取分集。" : "请输入有效的 B 站视频链接或完整 BV 号。";
            return;
        }
        _selectedIdentity = identity;
        _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var request = _previewRequest = new PreviewRequest(version, identity!, _previewCancellation.Token);
        if (!forceReload && _episodeCache.TryGetValue(identity!.Bvid, out var cached))
        {
            PublishSelectedEpisodes(cached);
            return;
        }
        UpdateEpisodeControls();
        EpisodeStatusText.Text = "正在读取所选视频的分集…";
        if (immediate)
            _ = Dispatcher.BeginInvoke(() => { if (PreviewIsCurrent(request)) _ = StartPreviewLoad(request); }, DispatcherPriority.Background);
        else _previewDebounce.Start();
    }

    private bool PreviewIsCurrent(PreviewRequest request) => !request.Token.IsCancellationRequested &&
        request.Version == _previewVersion && _selectedIdentity == request.Identity;

    private Task LoadSelectedEpisodesAsync()
    {
        _previewDebounce.Stop();
        return _previewRequest is { } request ? StartPreviewLoad(request) : Task.CompletedTask;
    }

    private Task StartPreviewLoad(PreviewRequest request)
    {
        if (!PreviewIsCurrent(request)) return Task.CompletedTask;
        if (_previewLoadingVersion == request.Version && _previewTask is not null) return _previewTask;
        _previewLoadingVersion = request.Version;
        return _previewTask = LoadPreviewRequestAsync(request);
    }

    private async Task LoadPreviewRequestAsync(PreviewRequest request)
    {
        try
        {
            // Read the whole list from P1 even when the typed P number does not exist, so the user can correct it.
            var identity = request.Identity with { Part = 1 };
            var page = _page is not null && _follow.Current?.Identity.Bvid == identity.Bvid ? _page : null;
            var info = page is null ? await _episodes.ReadFromApiAsync(identity, request.Token) :
                await _episodes.ReadAsync(page with { Url = identity.Url }, request.Token);
            if (!PreviewIsCurrent(request)) return;
            _episodeCache[info.Bvid] = info;
            PublishSelectedEpisodes(info);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!PreviewIsCurrent(request)) return;
            EpisodeStatusText.Text = $"分集读取失败：{ex.Message}";
            EpisodeRetryButton.Visibility = Visibility.Visible;
        }
    }

    private void PublishSelectedEpisodes(BilibiliVideoInfo info)
    {
        if (_selectedIdentity?.Bvid != info.Bvid) return;
        _selectedInfo = info with { CurrentPart = _selectedIdentity.Part };
        EpisodeRetryButton.Visibility = Visibility.Collapsed;
        UpdateEpisodeControls();
    }

    private Task MoveSelectedEpisodeAsync(int direction)
    {
        if (_selectedInfo is null || _selectedIdentity is null) return Task.CompletedTask;
        var index = _selectedInfo.Episodes.ToList().FindIndex(episode => episode.Number == _selectedIdentity.Part);
        var next = index + direction;
        return index >= 0 && next >= 0 && next < _selectedInfo.Episodes.Count ? SwitchEpisodeAsync(_selectedInfo.Episodes[next].Number) : Task.CompletedTask;
    }

    private Task SwitchEpisodeAsync(int part)
    {
        if (_selectedInfo is null || _selectedIdentity is null || !_selectedInfo.Episodes.Any(episode => episode.Number == part) || _selectedIdentity.Part == part)
            return Task.CompletedTask;
        if (SelectedVideoIsFollowing) return NavigateActiveEpisodeAsync(part);
        UrlInput.Text = new VideoIdentity(_selectedInfo.Bvid, part).Url;
        SetStatus($"已选择 P{part}，点击“开始跟随”播放。");
        return Task.CompletedTask;
    }
}
