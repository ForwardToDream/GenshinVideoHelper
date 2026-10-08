using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Browser;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ChromeBrowser _browser;
    private readonly bool _persistSettings;
    private readonly VideoController _video = new();
    private readonly BilibiliEpisodeService _episodes;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FollowSession _follow;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PipMouseVisibilityService _pipMouseVisibility = new();
    private HotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private bool _closing, _shutdownComplete;
    private WindowState _restoreState = WindowState.Normal;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ShutdownCompletion => _shutdown.Task;
    private BrowserPage? _page;
    private VideoState? _state;
    private BilibiliVideoInfo? _info;
    private bool _ready;
    private bool _updatingControls;
    private long _navigatingVersion;
    private long _metadataVersion;
    private DateTime _sessionBegan;
    private string? _lastQuietError;

    public MainWindow() : this(AppSettings.Load(), Path.Combine(AppSettings.DataDirectory, "Chrome"), true) { }

    // Tests inject an isolated Chrome profile and disable persistence.
    public MainWindow(AppSettings settings, string browserProfile, bool persistSettings, VideoLibraryCatalog? libraryCatalog = null, BilibiliEpisodeService? episodeService = null)
    {
        _settings = settings;
        _episodes = episodeService ?? new();
        _libraryCatalog = libraryCatalog ?? VideoLibraryCatalog.LoadBuiltIn();
        _browser = new(browserProfile);
        _persistSettings = persistSettings;
        _follow = new(_lifetime.Token);
        InitializeComponent();
        UrlInput.Text = _settings.VideoUrl;
        ConfigPathText.Text =  $"配置文件：{AppSettings.SettingsPath}";
        if (_settings.LoadWarning is not null) SetStatus(_settings.LoadWarning, true);
        PipWidthSlider.Value = _settings.PipWidth;
        PipWidthLabel.Text = $"{_settings.PipWidth} px";
        var seekItem = SeekSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == _settings.SeekSeconds.ToString());
        if (seekItem is null)
        {
            seekItem = new ComboBoxItem { Content = $"{_settings.SeekSeconds} 秒", Tag = _settings.SeekSeconds.ToString() };
            SeekSelector.Items.Add(seekItem);
        }
        SeekSelector.SelectedItem = seekItem;
        RateSelector.SelectedIndex = 1;
        HotkeysCheckbox.IsChecked = _settings.HotkeysEnabled;
        PopulateBindings(_settings.Hotkeys);
        UpdateSeekButtons();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        _poll.Tick += async (_, _) => await PollAsync();
        InitializeLibraries();
        _ready = true;
        _previewDebounce.Tick += async (_, _) => await LoadSelectedEpisodesAsync();
        PrepareEpisodePreview(immediate: true);
        ShowPage("Follow");
    }



    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready && sender is RadioButton { Tag: string page }) ShowPage(page);
    }

    private void ShowPage(string key)
    {
        FollowPage.Visibility = key == "Follow" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = key == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPage.Visibility = key == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = key switch { "Settings" => "设置", "Hotkeys" => "快捷键", _ => "启动" };
        PageDescription.Text = key switch { "Settings" => "调整播放节奏与浮窗。", "Hotkeys" => "查看控制说明，修改游戏中使用的组合键。", _ => "选择攻略，自动播放并进入左下角画中画。" };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "README.md");
        if (!File.Exists(path)) { SetStatus("使用说明未找到，请查看项目根目录 README.md。", true); return; }
        OpenWithShell(path);
    }
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) OpenWithShell(url);
    }
    private void OpenWithShell(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetStatus($"无法打开：{ex.Message}", true); }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotkeyService(new WindowInteropHelper(this).Handle);
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();
        _poll.Start();
        _tray = new TrayIconService(this, RestoreFromTray, Close);
    }

    private FollowRequest BeginFollow(BrowserPage? page, VideoIdentity identity)
    {
        var request = _follow.Begin(page?.Id ?? "", identity);
        _sessionBegan = DateTime.UtcNow;
        _page = page;
        _navigatingVersion = 0;
        _lastQuietError = null;
        FollowRetryButton.Visibility = Visibility.Collapsed;
        EpisodeRetryButton.Visibility = Visibility.Collapsed;
        OpenButton.IsEnabled = true;
        if (_episodeCache.TryGetValue(identity.Bvid, out var cached)) _info = cached with { CurrentPart = identity.Part };
        else if (_info?.Bvid != identity.Bvid) _info = null;
        else if (_info is not null) _info = _info with { CurrentPart = identity.Part };
        ShowDisconnected();
        UpdateEpisodeControls();
        SetStatus("正在准备跟随，视频就绪后自动播放并开启画中画。");
        return request;
    }

    private async void OpenVideo_Click(object sender, RoutedEventArgs e) => await OpenVideoAsync();
    private async Task OpenVideoAsync()
    {
        VideoIdentity identity;
        try { identity = VideoIdentity.Parse(UrlInput.Text); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); return; }
        if (_selectedInfo?.Bvid == identity.Bvid && !_selectedInfo.Episodes.Any(episode => episode.Number == identity.Part))
        { SetStatus($"此视频没有 P{identity.Part}，请选择有效分集。", true); return; }
        var request = BeginFollow(null, identity);
        UrlInput.Text = identity.Url;
        _settings.VideoUrl = identity.Url;
        SaveSettings();
        OpenButton.IsEnabled = false;
        await RunAsync(async () =>
        {
            var page = await _browser.OpenAsync(identity.Url, request.Token);
            if (!_follow.IsCurrent(request)) return;
            request = _follow.Attach(request, page.Id);
            _page = page;
            StartMetadata(request, page with { Url = identity.Url });
            SetStatus("Chrome 已打开，正在等待视频。需要登录时请在 Chrome 中完成。");
        }, request);
        if (_follow.IsCurrent(request)) OpenButton.IsEnabled = true;
    }

    private async void UrlInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenVideoAsync();
    }

    private void StartMetadata(FollowRequest request, BrowserPage page) => _ = LoadEpisodesAsync(request, page);
    private async Task LoadEpisodesAsync(FollowRequest request, BrowserPage page)
    {
        var metadataVersion = ++_metadataVersion;
        if (_selectedIdentity?.Bvid == request.Identity.Bvid && _selectedInfo is null)
        {
            EpisodeRetryButton.Visibility = Visibility.Collapsed;
            EpisodeStatusText.Text = "正在读取分集…";
        }
        try
        {
            var info = await _episodes.ReadAsync(page, request.Token);
            if (!_follow.IsCurrent(request) || metadataVersion != _metadataVersion) return;
            _info = info;
            _episodeCache[info.Bvid] = info;
            if (_selectedIdentity?.Bvid == info.Bvid) PrepareEpisodePreview();
            if (_state is not null) UpdateVideo(_state);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_follow.IsCurrent(request) || metadataVersion != _metadataVersion) return;
            if (_selectedIdentity?.Bvid == request.Identity.Bvid && _selectedInfo is null)
            {
                EpisodeStatusText.Text = $"分集读取失败：{ex.Message}";
                EpisodeRetryButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void UpdateEpisodeControls()
    {
        var part = _selectedIdentity?.Part ?? _selectedInfo?.CurrentPart ?? 1;
        _updatingControls = true;
        try
        {
            EpisodeSelector.ItemsSource = _selectedInfo?.Episodes;
            EpisodeSelector.SelectedItem = _selectedInfo?.Episodes.FirstOrDefault(p => p.Number == part);
            EpisodeSelector.IsEnabled = _selectedInfo is not null;
            var index = _selectedInfo is null ? -1 : _selectedInfo.Episodes.ToList().FindIndex(p => p.Number == part);
            PreviousEpisodeButton.IsEnabled = index > 0;
            NextEpisodeButton.IsEnabled = index >= 0 && index < _selectedInfo!.Episodes.Count - 1;
            EpisodeStatusText.Text = _selectedInfo is null ? "等待分集信息…" : index < 0 ? $"此视频没有 P{part}，请选择有效分集。" : "";
        }
        finally { _updatingControls = false; }
    }

    private async void EpisodeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingControls || EpisodeSelector.SelectedItem is not EpisodeInfo episode) return;
        await SwitchEpisodeAsync(episode.Number);
    }
    private async void PreviousEpisode_Click(object sender, RoutedEventArgs e) => await MoveSelectedEpisodeAsync(-1);
    private async void NextEpisode_Click(object sender, RoutedEventArgs e) => await MoveSelectedEpisodeAsync(1);
    private Task MoveEpisodeAsync(int direction)
    {
        if (_info is null || _page is null || _follow.Current is null)
        { SetStatus("请先开始跟随并等待分集信息加载完成。", true); return Task.CompletedTask; }
        var index = _info.Episodes.ToList().FindIndex(p => p.Number == _follow.Current?.Identity.Part);
        var next = index + direction;
        if (index < 0) { SetStatus("当前分集信息尚未就绪，请稍后重试。", true); return Task.CompletedTask; }
        if (next < 0 || next >= _info.Episodes.Count)
        { SetStatus(direction < 0 ? "已经是第一个分集。" : "已经是最后一个分集。"); return Task.CompletedTask; }
        return next >= 0 && next < _info.Episodes.Count ? NavigateActiveEpisodeAsync(_info.Episodes[next].Number) : Task.CompletedTask;
    }

    private async Task NavigateActiveEpisodeAsync(int part)
    {
        if (_page is null || _info is null || _follow.Current is not { } current || current.Identity.Part == part) return;
        if (!_info.Episodes.Any(p => p.Number == part)) { SetStatus("请选择有效分集。", true); return; }
        var identity = new VideoIdentity(_info.Bvid, part);
        var page = _page;
        var request = BeginFollow(page, identity);
        _navigatingVersion = request.Version;
        RememberIdentity(identity);
        await RunAsync(async () =>
        {
            await _browser.NavigateAsync(page, identity, request.Token);
            if (!_follow.IsCurrent(request)) return;
            _page = page with { Url = identity.Url };
            if (_info is null) StartMetadata(request, _page);
        }, request);
        if (_navigatingVersion == request.Version) _navigatingVersion = 0;
    }

    private void EpisodeRetry_Click(object sender, RoutedEventArgs e)
    {
        PrepareEpisodePreview(immediate: true, forceReload: true);
    }

    private void FollowRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_page is null) { SetStatus("请先开始跟随，打开视频页面。", true); return; }
        var identity = _follow.Current?.Identity ?? VideoIdentity.Parse(_page.Url);
        var request = BeginFollow(_page, identity);
        if (_info is null) StartMetadata(request, _page with { Url = identity.Url });
    }

    private void RememberIdentity(VideoIdentity identity)
    {
        UrlInput.Text = identity.Url;
        if (_settings.VideoUrl == identity.Url) return;
        _settings.VideoUrl = identity.Url;
        SaveSettings();
    }

    private async Task<BrowserPage> GetCurrentPageAsync(FollowRequest request)
    {
        var page = await _browser.GetPageAsync(request.TargetId, request.Token);
        if (page is null && _follow.AutomaticPending && DateTime.UtcNow - _sessionBegan < TimeSpan.FromSeconds(5))
            throw new VideoNotReadyException("正在等待页面导航完成。");
        if (page is null) throw new InvalidOperationException("视频页面已关闭，请重新开始跟随。");
        if (string.IsNullOrEmpty(page.Url) || page.Url == "about:blank")
            throw new VideoNotReadyException("正在等待页面导航完成。");
        if (!ChromeBrowser.IsBilibiliUrl(page.Url))
            throw new InvalidOperationException("视频页面已离开 B 站，请重新开始跟随。");
        if (!_follow.IsCurrent(request)) throw new OperationCanceledException(request.Token);
        return page;
    }

    private async Task<VideoState> ReadVideoAsync(VideoCommand command, FollowRequest request)
    {
        var page = await GetCurrentPageAsync(request);
        if (!VideoIdentity.TryParse(page.Url, out var identity) || identity != request.Identity)
            throw new VideoNotReadyException("正在切换分集，请等待视频就绪。");
        var state = await _video.ExecuteAsync(page, command, request.Token);
        if (!_follow.IsCurrent(request)) throw new OperationCanceledException(request.Token);
        if (!VideoIdentity.TryParse(state.Url, out var actual) || actual != request.Identity)
            throw new VideoNotReadyException("正在切换分集，请等待视频就绪。");
        var episode = _info?.Bvid == request.Identity.Bvid ? _info.Episodes.FirstOrDefault(p => p.Number == request.Identity.Part) : null;
        if (episode is not null && (state.Cid is { } cid && cid != episode.Cid ||
            episode.Duration > 0 && state.Duration is { } duration && Math.Abs(duration - episode.Duration) > 2))
            throw new VideoNotReadyException("正在等待所选分集的媒体加载。");
        if (state.PictureInPicture && _pipMouseVisibility.BrowserProcessId == 0)
        {
            var browserProcessId = await _browser.GetBrowserProcessIdAsync(request.Token);
            if (!_follow.IsCurrent(request)) throw new OperationCanceledException(request.Token);
            _pipMouseVisibility.TrackBrowser(browserProcessId);
        }
        else if (!state.PictureInPicture) _pipMouseVisibility.Suspend();
        _page = page;
        UpdateVideo(state);
        return state;
    }

    private async Task PollAsync()
    {
        if (_page is null || _navigatingVersion != 0 || _lifetime.IsCancellationRequested) return;
        await RunAsync(async () =>
        {
            if (_follow.Current is not { } request) return;
            var page = await GetCurrentPageAsync(request);
            if (VideoIdentity.TryParse(page.Url, out var identity) && identity != request.Identity)
            {
                request = BeginFollow(page, identity!);
                RememberIdentity(identity!);
                if (_info is null) StartMetadata(request, page);
            }
            var state = await ReadVideoAsync(new("status"), request);
            _lastQuietError = null;
            if (!_follow.AutomaticPending || !_follow.IsCurrent(request)) return;
            try
            {
                state = await ReadVideoAsync(new("ensurePlay"), request);
                if (!_follow.AutomaticPending) return;
                state = await ReadVideoAsync(new("ensurePip"), request);
                if (!_follow.AutomaticPending || !_follow.IsCurrent(request)) return;
                await PlacePipCoreAsync(state, request.Token);
                if (!_follow.IsCurrent(request)) return;
                _follow.Complete(request);
                FollowRetryButton.Visibility = Visibility.Collapsed;
                SetStatus("已开始跟随，画中画置顶在左下角。可以最小化工具进入游戏。");
            }
            catch (VideoNotReadyException) { throw; }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_follow.IsCurrent(request)) return;
                _follow.Complete(request);
                FollowRetryButton.Visibility = Visibility.Visible;
                SetStatus($"自动跟随未完成：{ex.Message}。可点击重试自动跟随。", true);
            }
        }, quiet: true);
    }

    private void UpdateVideo(VideoState state)
    {
        _state = state;
        var part = _follow.Current?.Identity.Part ?? _info?.CurrentPart;
        var episode = _info?.Episodes.FirstOrDefault(p => p.Number == part);
        VideoTitle.Text = episode is null ? state.Title : $"P{episode.Number} · {episode.Title}";
        VideoTitle.ToolTip = episode is null ? state.Title : $"{episode.DisplayText}\n{state.Title}";
        TimeLabel.Text = $"{FormatTime(state.CurrentTime)} / {(state.Duration is { } duration ? FormatTime(duration) : "直播")}";
        PlaybackLabel.Text = state.Paused ? "已暂停" : $"播放中 · {state.PlaybackRate:0.##}×";
        PlayButton.Content = state.Paused ? "继续播放" : "暂停播放";
        PipButton.Content = state.PictureInPicture ? "关闭画中画" : "开启画中画";
        MuteButton.Content = state.Muted ? "取消静音" : "静音";
        HeaderConnectionState.Text = "视频已连接";
        PipStateText.Text = state.PictureInPicture ? "浮窗已开启 · 置顶" : "浮窗尚未开启";
        UpdatePlaybackPreferenceText(state.PlaybackRate);
        _updatingControls = true;
        try
        {
            RateSelector.SelectedItem = RateSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                Math.Abs(double.Parse(item.Tag.ToString()!, CultureInfo.InvariantCulture) - state.PlaybackRate) < 0.01);
        }
        finally { _updatingControls = false; }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private void ShowDisconnected()
    {
        _state = null;
        VideoTitle.Text = _page?.Title ?? "等待开始跟随";
        TimeLabel.Text = "--:-- / --:--";
        PlaybackLabel.Text = "等待视频";
        PlayButton.Content = "播放 / 暂停";
        PipButton.Content = "开启画中画";
        HeaderConnectionState.Text = _page is null ? "等待开始" : "等待视频";
        PipStateText.Text = "视频就绪后开启浮窗";
        UpdatePlaybackPreferenceText(1);
    }

    private async Task ControlAsync(VideoCommand command)
    {
        if (command.Action == "toggle") _follow.Suppress();
        var request = _follow.Current;
        await RunAsync(async () =>
        {
            if (request is null) throw new InvalidOperationException("请先开始跟随。");
            var state = await ReadVideoAsync(command, request);
            SetStatus(command.Action switch
            {
                "toggle" => state.Paused ? "视频已暂停。" : "视频继续播放。",
                "seek" => $"已跳转到 {FormatTime(state.CurrentTime)}。",
                "mute" => state.Muted ? "视频已静音。" : "已恢复视频声音。",
                "rate" => $"播放速度 {state.PlaybackRate:0.##}×。",
                _ => "视频状态已更新。"
            });
        }, request, shortWait: true);
    }

    private async Task TogglePipAsync()
    {
        _follow.Suppress();
        var request = _follow.Current;
        await RunAsync(async () =>
        {
            if (request is null) throw new InvalidOperationException("请先开始跟随。");
            var state = await ReadVideoAsync(new("pip"), request);
            if (state.PictureInPicture)
            {
                await PlacePipCoreAsync(state, request.Token);
                SetStatus("画中画已置顶在左下角。");
            }
            else SetStatus("画中画已关闭；当前分集不会自动重新开启。");
            FollowRetryButton.Visibility = Visibility.Collapsed;
        }, request, shortWait: true);
    }

    private async Task PlacePipAsync()
    {
        var request = _follow.Current;
        await RunAsync(async () =>
        {
            if (request is null) throw new InvalidOperationException("请先开始跟随。");
            var state = await ReadVideoAsync(new("status"), request);
            if (!state.PictureInPicture) throw new InvalidOperationException("请先开启画中画，再调整位置。");
            await PlacePipCoreAsync(state, request.Token);
            SetStatus("画中画已放回本工具所在屏幕的左下角。");
        }, request, shortWait: true);
    }

    private async Task PlacePipCoreAsync(VideoState state, CancellationToken token)
    {
        var processId = await _browser.GetBrowserProcessIdAsync(token);
        var ratio = state.VideoHeight > 0 ? (double)state.VideoWidth / state.VideoHeight : 16d / 9;
        await PipWindowService.PlaceAsync(processId, new WindowInteropHelper(this).Handle,
            _settings.PipWidth, _settings.PipMargin, ratio, token);
        _pipMouseVisibility.TrackBrowser(processId);
    }

    private async Task RunAsync(Func<Task> action, FollowRequest? request = null, bool quiet = false, bool shortWait = false)
    {
        var token = request?.Token ?? _lifetime.Token;
        if (token.IsCancellationRequested) return;
        var acquired = false;
        try
        {
            if (quiet) acquired = await _operations.WaitAsync(0, token);
            else if (shortWait) acquired = await _operations.WaitAsync(TimeSpan.FromMilliseconds(250), token);
            else { await _operations.WaitAsync(token); acquired = true; }
            if (!acquired)
            {
                if (!quiet) SetStatus("正在处理上一条操作，请稍后重试。", true);
                return;
            }
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || token.IsCancellationRequested || request is not null && !_follow.IsCurrent(request)) { }
        catch (Exception ex)
        {
            if (request is not null && !_follow.IsCurrent(request)) return;
            if (quiet)
            {
                var message = ex is BrowserConnectionException or HttpRequestException ? "浏览器连接已断开或暂未响应，请重新开始跟随。" : ex is VideoNotReadyException or IOException or TimeoutException
                    ? "正在等待视频就绪。需要登录时，请在 Chrome 中完成后等待。" : ex.Message;
                if (_lastQuietError != message) { SetStatus(message, ex is not VideoNotReadyException); _lastQuietError = message; }
                if (ex is VideoNotReadyException) ShowDisconnected();
                else if (ex is InvalidOperationException or HttpRequestException or BrowserConnectionException)
                {
                    _follow.Suppress();
                    ShowDisconnected();
                    HeaderConnectionState.Text = "连接已断开";
                    PlaybackLabel.Text = "未连接";
                }
            }
            else
            {
                SetStatus(ex.Message, true);
                if (request is not null) { _follow.Complete(request); FollowRetryButton.Visibility = Visibility.Visible; }
            }
        }
        finally { if (acquired) _operations.Release(); }
    }

    private async void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.TogglePlayback: await ControlAsync(new("toggle")); break;
            case HotkeyAction.SeekBackward: await ControlAsync(new("seek", -_settings.SeekSeconds)); break;
            case HotkeyAction.SeekForward: await ControlAsync(new("seek", _settings.SeekSeconds)); break;
            case HotkeyAction.TogglePip: await TogglePipAsync(); break;
            case HotkeyAction.PlacePip: await PlacePipAsync(); break;
            case HotkeyAction.ToggleMute: await ControlAsync(new("mute")); break;
            case HotkeyAction.PreviousEpisode: await MoveEpisodeAsync(-1); break;
            case HotkeyAction.NextEpisode: await MoveEpisodeAsync(1); break;
        }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await ControlAsync(new("toggle"));
    private async void Backward_Click(object sender, RoutedEventArgs e) => await ControlAsync(new("seek", -_settings.SeekSeconds));
    private async void Forward_Click(object sender, RoutedEventArgs e) => await ControlAsync(new("seek", _settings.SeekSeconds));
    private async void Mute_Click(object sender, RoutedEventArgs e) => await ControlAsync(new("mute"));
    private async void Pip_Click(object sender, RoutedEventArgs e) => await TogglePipAsync();
    private async void PlacePip_Click(object sender, RoutedEventArgs e) => await PlacePipAsync();

    private async void Rate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingControls || _page is null || RateSelector.SelectedItem is not ComboBoxItem item) return;
        await ControlAsync(new("rate", double.Parse(item.Tag.ToString()!, CultureInfo.InvariantCulture)));
    }
    private void Seek_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SeekSelector.SelectedItem is not ComboBoxItem item) return;
        _settings.SeekSeconds = int.Parse(item.Tag.ToString()!);
        UpdateSeekButtons();
        SaveSettings();
    }
    private void UpdateSeekButtons()
    {
        BackButton.Content = $"后退 {_settings.SeekSeconds} 秒";
        ForwardButton.Content = $"前进 {_settings.SeekSeconds} 秒";
        UpdatePlaybackPreferenceText(_state?.PlaybackRate ?? 1);
    }
    private void UpdatePlaybackPreferenceText(double rate) =>
        PlaybackPreferenceText.Text = $"跳转步长 {_settings.SeekSeconds} 秒 · 播放速度 {rate:0.##}×";
    private void PipWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.PipWidth = (int)PipWidthSlider.Value;
        PipWidthLabel.Text = $"{_settings.PipWidth} px";
        SaveSettings();
    }
    private void Hotkeys_Click(object sender, RoutedEventArgs e)
    {
        _settings.HotkeysEnabled = HotkeysCheckbox.IsChecked == true;
        ApplyHotkeys();
        SaveSettings();
    }
    private void ApplyHotkeys()
    {
        if (_editingBindings) return;
        HotkeyGesture.TryParse(_settings.Hotkeys[HotkeyAction.ReversePipVisibility], out var reversal, allowUnmodified: true);
        _pipMouseVisibility.SetReversalBinding(_settings.HotkeysEnabled ? reversal : null);
        if (_settings.HotkeysEnabled && _hotkeys is not null)
        {
            var conflicts = _hotkeys.Enable(_settings.Hotkeys);
            HotkeyStatus.Text = conflicts.Count == 0 ? "快捷键已启用" :
                $"已启用 {_hotkeys.RegisteredCount + 1}/9 项快捷键。被占用：{string.Join("；", conflicts)}。可使用面板按钮。";
        }
        else { _hotkeys?.Disable(); HotkeyStatus.Text = _hotkeys is null && _settings.HotkeysEnabled ? "启动后注册快捷键" : "快捷键已停用"; }
    }
    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(error ? "WarningBrush" : "SuccessBrush");
    }
    private bool SaveSettings()
    {
        if (!_persistSettings) return true;
        try { _settings.Save(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus($"设置未能保存：{ex.Message}", true); return false; }
    }
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (WindowState == WindowState.Minimized) Hide();
        else _restoreState = WindowState;
    }

    private void RestoreFromTray()
    {
        if (_closing) return;
        var restoreState = _restoreState;
        Show();
        WindowState = restoreState;
        Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        if (_closing) { e.Cancel = true; return; }
        _closing = true;
        _ready = false;
        _poll.Stop();
        _previewDebounce.Stop();
        _previewCancellation?.Cancel();
        _lifetime.Cancel();
        _follow.Suppress();
        _hotkeys?.Disable();
        _pipMouseVisibility.Dispose();
        _tray?.BeginExit();
        SetStatus("正在退出，关闭浏览器和画中画…");
        var closing = _browser.CloseAsync();
        if (closing.IsCompleted)
        {
            try { closing.GetAwaiter().GetResult(); }
            catch (Exception ex) { Debug.WriteLine($"Browser exit: {ex.Message}"); }
            _shutdownComplete = true;
            return;
        }
        e.Cancel = true;
        _ = FinishExitAsync(closing);
    }

    private async Task FinishExitAsync(Task closing)
    {
        try { await closing; }
        catch (Exception ex) { Debug.WriteLine($"Browser exit: {ex.Message}"); }
        finally
        {
            _shutdownComplete = true;
            // Never reenter Close inside the original Closing event, even with a completed task.
            await Dispatcher.InvokeAsync(Close);
        }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        _pipMouseVisibility.Dispose();
        _previewDebounce.Stop();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _lifetime.Cancel();
        _follow.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _browser.Dispose();
        _episodes.Dispose();
        _shutdown.TrySetResult();
    }
}


