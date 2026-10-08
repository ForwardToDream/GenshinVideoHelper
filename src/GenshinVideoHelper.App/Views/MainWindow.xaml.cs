using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.App;

public partial class MainWindow : Window
{
    public AppServices Services { get; }
    private readonly AppSettings _settings;
    private readonly VideoLibraryCatalog _libraryCatalog;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private PipMouseVisibilityService _pipMouseVisibility => Services.Pip.MouseVisibility;
    private BrowserPage? _page => Services.Follow.Current.Page;
    private VideoState? _state;
    private BilibiliVideoInfo? _info => Services.Follow.Current.Info;
    private VideoIdentity? _selectedIdentity => Services.Preview.Current.Identity;
    private BilibiliVideoInfo? _selectedInfo => Services.Preview.Current.Info;
    private HotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private bool _ready, _updatingControls, _closing, _shutdownComplete;
    private WindowState _restoreState = WindowState.Normal;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ShutdownCompletion => _shutdown.Task;
    private VideoIdentity? _displayedIdentity;
    private BilibiliVideoInfo? _displayedInfo;
    private string? _displayedMessage;

    public MainWindow() : this(AppServices.CreateDefault()) { }

    public MainWindow(AppServices services)
    {
        Services = services;
        _settings = services.Settings;
        _libraryCatalog = services.Libraries;
        InitializeComponent();
        UrlInput.Text = _settings.VideoUrl;
        ConfigPathText.Text = "配置文件：根目录 GenshinVideoHelper.settings.json";
        if (Services.LoadWarning is not null) SetStatus(Services.LoadWarning, true);
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
        Services.Follow.Changed += DisplayFollow;
        Services.Preview.Changed += DisplayPreview;
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
        Services.Pip.HelperWindow = new WindowInteropHelper(this).Handle;
        _tray = new TrayIconService(this, RestoreFromTray, Close);
    }

    private void DisplayFollow(FollowSnapshot snapshot)
    {
        FollowRetryButton.Visibility = snapshot.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = snapshot.Phase != FollowPhase.Opening;
        if (snapshot.Request?.Identity is { } identity && identity != _displayedIdentity)
        {
            _displayedIdentity = identity;
            RememberIdentity(identity);
        }
        if (snapshot.Info != _displayedInfo)
        {
            _displayedInfo = snapshot.Info;
            if (snapshot.Info?.Bvid == _selectedIdentity?.Bvid) PrepareEpisodePreview();
        }
        if (snapshot.Video is { } state) UpdateVideo(state);
        else ShowDisconnected();
        if (snapshot.Phase == FollowPhase.Failed && snapshot.Video is null)
        { HeaderConnectionState.Text = "连接已断开"; PlaybackLabel.Text = "未连接"; }
        if (snapshot.Message is not null && snapshot.Message != _displayedMessage)
        { _displayedMessage = snapshot.Message; SetStatus(snapshot.Message, snapshot.IsError); }
    }

    private async void OpenVideo_Click(object sender, RoutedEventArgs e) => await OpenVideoAsync();
    private async Task OpenVideoAsync()
    {
        VideoIdentity identity;
        try { identity = VideoIdentity.Parse(UrlInput.Text); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); return; }
        if (_selectedInfo?.Bvid == identity.Bvid && !_selectedInfo.Episodes.Any(p => p.Number == identity.Part))
        { SetStatus($"此视频没有 P{identity.Part}，请选择有效分集。", true); return; }
        await Services.Follow.OpenAsync(identity);
    }
    private async void UrlInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenVideoAsync();
    }
    private void EpisodeRetry_Click(object sender, RoutedEventArgs e) => PrepareEpisodePreview(immediate: true, forceReload: true);
    private void FollowRetry_Click(object sender, RoutedEventArgs e) => Services.Follow.Retry();
    private async void EpisodeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingControls || EpisodeSelector.SelectedItem is not EpisodeInfo episode) return;
        await SwitchEpisodeAsync(episode.Number);
    }
    private async void PreviousEpisode_Click(object sender, RoutedEventArgs e) => await MoveSelectedEpisodeAsync(-1);
    private async void NextEpisode_Click(object sender, RoutedEventArgs e) => await MoveSelectedEpisodeAsync(1);
    private Task MoveEpisodeAsync(int direction) => Services.Follow.MoveEpisodeAsync(direction);
    private Task NavigateActiveEpisodeAsync(int part) => Services.Follow.NavigateAsync(part);
    private Task PollAsync() => Services.Follow.PollAsync();
    private Task ControlAsync(VideoCommand command) => Services.Follow.ExecuteAsync(command);
    private Task TogglePipAsync() => ControlAsync(new("pip"));
    private Task PlacePipAsync() => Services.Follow.PlacePipAsync();

    private void RememberIdentity(VideoIdentity identity)
    {
        UrlInput.Text = identity.Url;
        if (_settings.VideoUrl == identity.Url) return;
        _settings.VideoUrl = identity.Url;
        SaveSettings();
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

    private void UpdateVideo(VideoState state)
    {
        _state = state;
        var part = Services.Follow.Session.Current?.Identity.Part ?? _info?.CurrentPart;
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
        if (Services.SettingsStore is null) return true;
        try { Services.SettingsStore.Save(_settings); return true; }
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

        _hotkeys?.Disable();
        _pipMouseVisibility.Dispose();
        _tray?.BeginExit();
        SetStatus("正在退出，关闭浏览器和画中画…");
        var closing = Services.CloseAsync();
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

        _hotkeys?.Dispose();
        _tray?.Dispose();

        Services.Dispose();
        _shutdown.TrySetResult();
    }
}
