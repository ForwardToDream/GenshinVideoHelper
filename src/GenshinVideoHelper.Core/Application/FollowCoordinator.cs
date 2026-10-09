using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;

namespace GenshinVideoHelper.Core.Application;

public enum FollowPhase { Idle, Opening, WaitingForMedia, Following, Suppressed, Failed, Closing }
public sealed record FollowSnapshot(FollowRequest? Request, BrowserPage? Page, VideoState? Video,
    BilibiliVideoInfo? Info, FollowPhase Phase, bool AutomaticPending, bool CanRetry, string? Message, bool IsError);

/// <summary>Owns active follow state and asynchronous requests; external adapters are owned by the host.
/// Calls and notifications run on the caller's synchronization context (one UI owner).</summary>
public sealed class FollowCoordinator : IDisposable
{
    private readonly IBrowserSession _browser;
    private readonly IVideoPlayer _video;
    private readonly IEpisodeProvider _episodes;
    private readonly IPipController _pip;
    private readonly EpisodeCache _cache;
    private readonly TimeProvider _clock;
    private readonly WatchProgressService? _progress;
    private readonly long _started;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly BackgroundWork _work = new();
    private long _navigatingVersion;
    private long _metadataVersion;
    private DateTimeOffset _began;
    private bool _polling, _stopped;
    private string? _lastQuietError;
    private long _resumeVersion;
    private double? _resumedTo;
    public FollowSession Session { get; }
    public FollowSnapshot Current { get; private set; } = new(null, null, null, null, FollowPhase.Idle, false, false, null, false);
    public event Action<FollowSnapshot>? Changed;

    public FollowCoordinator(IBrowserSession browser, IVideoPlayer video, IEpisodeProvider episodes,
        IPipController pip, EpisodeCache cache, TimeProvider? clock = null, WatchProgressService? progress = null)
    {
        _browser = browser; _video = video; _episodes = episodes; _pip = pip; _cache = cache;
        _clock = clock ?? TimeProvider.System;
        _progress = progress;
        _started = _clock.GetTimestamp();
        Session = new(_lifetime.Token);
    }

    public Task OpenAsync(VideoIdentity identity) => _work.Track(OpenCoreAsync(identity));
    private async Task OpenCoreAsync(VideoIdentity identity)
    {
        if (_stopped) return;
        var request = Begin(null, identity, FollowPhase.Opening);
        await GuardAsync(async () =>
        {
            var page = await _browser.OpenAsync(identity.Url, request.Token);
            EnsureCurrent(request);
            request = Session.Attach(request, page.Id);
            Publish(Current with { Page = page, Phase = FollowPhase.WaitingForMedia, Message = "Chrome 已打开，正在等待视频。需要登录时请在 Chrome 中完成。" });
            StartMetadata(request, page with { Url = identity.Url });
        }, () => request);
    }

    public Task NavigateAsync(int part) => _work.Track(NavigateCoreAsync(part));
    private async Task NavigateCoreAsync(int part)
    {
        if (_stopped || Current.Page is not { } page || Current.Info is not { } info || Session.Current is not { } current || current.Identity.Part == part) return;
        if (!info.Episodes.Any(p => p.Number == part)) throw new ArgumentException("请选择有效分集。");
        var identity = new VideoIdentity(info.Bvid, part);
        var request = Begin(page, identity, FollowPhase.WaitingForMedia);
        _navigatingVersion = request.Version;
        await GuardAsync(async () =>
        {
            await _browser.NavigateAsync(page, identity, request.Token);
            EnsureCurrent(request);
            Publish(Current with { Page = page with { Url = identity.Url } });
            if (Current.Info is null) StartMetadata(request, Current.Page!);
        }, () => request);
        if (_navigatingVersion == request.Version) _navigatingVersion = 0;
    }

    public Task MoveEpisodeAsync(int direction)
    {
        if (Current.Info is not { } info || Session.Current is not { } request)
        { Message("请先开始跟随并等待分集信息加载完成。", true); return Task.CompletedTask; }
        var index = info.Episodes.ToList().FindIndex(p => p.Number == request.Identity.Part);
        if (index < 0) { Message("当前分集信息尚未就绪，请稍后重试。", true); return Task.CompletedTask; }
        var next = index + direction;
        if (next < 0 || next >= info.Episodes.Count)
        { Message(direction < 0 ? "已经是第一个分集。" : "已经是最后一个分集。"); return Task.CompletedTask; }
        return NavigateAsync(info.Episodes[next].Number);
    }

    public void Retry()
    {
        if (_stopped) return;
        if (Current.Page is not { } page) { Message("请先开始跟随，打开视频页面。", true); return; }
        var request = Begin(page, Session.Current?.Identity ?? VideoIdentity.Parse(page.Url), FollowPhase.WaitingForMedia);
        if (Current.Info is null) StartMetadata(request, page with { Url = request.Identity.Url });
    }

    private FollowRequest Begin(BrowserPage? page, VideoIdentity identity, FollowPhase phase)
    {
        var request = Session.Begin(page?.Id ?? "", identity);
        _began = _clock.GetUtcNow();
        _navigatingVersion = 0;
        _lastQuietError = null;
        _resumedTo = null;
        AppLog.Info("Follow", $"请求 #{request.Version}：{identity.Bvid} P{identity.Part}，目标页 {(page is null ? "新建" : page.Id)}。");
        var info = _cache.Get(identity) ?? (Current.Info?.Bvid == identity.Bvid ? Current.Info with { CurrentPart = identity.Part } : null);
        if (info is not null) Track(progress => progress.Describe(info));
        Publish(new(request, page, null, info, phase, true, false, "正在准备跟随，视频就绪后自动播放并开启画中画。", false));
        return request;
    }

    private void StartMetadata(FollowRequest request, BrowserPage page) => _ = _work.Track(ReadMetadataAsync(request, page));
    private async Task ReadMetadataAsync(FollowRequest request, BrowserPage page)
    {
        var version = ++_metadataVersion;
        try
        {
            var info = await _episodes.ReadAsync(page, request.Token);
            if (!IsCurrent(request) || version != _metadataVersion) return;
            _cache.Store(info);
            Track(progress => progress.Describe(info));
            Publish(Current with { Info = info });
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn("Follow", $"分集读取失败：{request.Identity.Bvid}", ex);
            if (IsCurrent(request) && version == _metadataVersion) Message($"分集读取失败：{ex.Message}", true);
        }
    }

    private async Task<BrowserPage> GetPageAsync(FollowRequest request)
    {
        var page = await _browser.GetPageAsync(request.TargetId, request.Token);
        EnsureCurrent(request);
        if (page is null && Session.AutomaticPending && _clock.GetUtcNow() - _began < TimeSpan.FromSeconds(5))
            throw new VideoNotReadyException("正在等待页面导航完成。");
        if (page is null) throw new InvalidOperationException("视频页面已关闭，请重新开始跟随。");
        if (string.IsNullOrEmpty(page.Url) || page.Url == "about:blank") throw new VideoNotReadyException("正在等待页面导航完成。");
        if (!BilibiliUrl.IsBilibili(page.Url)) throw new InvalidOperationException("视频页面已离开 B 站，请重新开始跟随。");
        return page;
    }

    private async Task<VideoState> ReadVideoAsync(VideoCommand command, FollowRequest request)
    {
        var page = await GetPageAsync(request);
        if (!VideoIdentity.TryParse(page.Url, out var identity) || identity != request.Identity) throw new VideoNotReadyException("正在切换分集，请等待视频就绪。");
        var state = await _video.ExecuteAsync(page, command, request.Token);
        EnsureCurrent(request);
        if (!VideoIdentity.TryParse(state.Url, out var actual) || actual != request.Identity) throw new VideoNotReadyException("正在切换分集，请等待视频就绪。");
        var episode = Current.Info?.Bvid == request.Identity.Bvid ? Current.Info.Episodes.FirstOrDefault(p => p.Number == request.Identity.Part) : null;
        if (episode is not null && (state.Cid is { } cid && cid != episode.Cid || episode.Duration > 0 && state.Duration is { } duration && Math.Abs(duration - episode.Duration) > 2))
            throw new VideoNotReadyException("正在等待所选分集的媒体加载。");
        // Only a state that passed every identity check above may count as watched.
        var at = _clock.GetElapsedTime(_started);
        Track(progress => progress.Observe(request.Identity, episode?.Cid ?? state.Cid, state, at));
        if (state.PictureInPicture)
        {
            var pid = await _browser.GetBrowserProcessIdAsync(request.Token);
            EnsureCurrent(request);
            await _pip.ObserveAsync(pid, true, request.Token);
        }
        else await _pip.ObserveAsync(0, false, request.Token);
        EnsureCurrent(request);
        Publish(Current with { Page = page, Video = state });
        return state;
    }

    public Task PollAsync() => _work.Track(PollCoreAsync());
    private async Task PollCoreAsync()
    {
        if (_stopped || _polling || Current.Page is null || _navigatingVersion != 0 || Session.Current is not { } request) return;
        _polling = true;
        try
        {
            await GuardAsync(async () =>
            {
                var page = await GetPageAsync(request);
                if (VideoIdentity.TryParse(page.Url, out var identity) && identity != request.Identity)
                {
                    request = Begin(page, identity!, FollowPhase.WaitingForMedia);
                    if (Current.Info is null) StartMetadata(request, page);
                }
                var state = await ReadVideoAsync(new("status"), request);
                _lastQuietError = null;
                if (!Session.AutomaticPending || !IsCurrent(request)) return;
                try
                {
                    state = await ResumeAsync(state, request);
                    if (!Session.AutomaticPending || !IsCurrent(request)) return;
                    state = await ReadVideoAsync(new("ensurePlay"), request);
                    if (!Session.AutomaticPending || !IsCurrent(request)) return;
                    state = await ReadVideoAsync(new("ensurePip"), request);
                    if (!Session.AutomaticPending || !IsCurrent(request)) return;
                    await PlaceAsync(state, request);
                    EnsureCurrent(request);
                    Session.Complete(request);
                    var resumed = _resumedTo is { } position ? $"已从 {TimeText.Format(position)} 继续。" : "";
                    Publish(Current with { Phase = FollowPhase.Following, CanRetry = false, Message = resumed + "已开始跟随，画中画置顶在左下角。可以最小化工具进入游戏。", IsError = false });
                }
                catch (VideoNotReadyException) { throw; }
                catch (OperationCanceledException) when (!IsCurrent(request)) { }
                catch (Exception ex)
                {
                    if (!IsCurrent(request)) return;
                    AppLog.Warn("Follow", $"自动跟随未完成（请求 #{request.Version}）。", ex);
                    Session.Complete(request);
                    Publish(Current with { Phase = FollowPhase.Failed, CanRetry = true, Message = $"自动跟随未完成：{ex.Message}。可点击重试自动跟随。", IsError = true });
                }
            }, () => request, quiet: true);
        }
        finally { _polling = false; }
    }

    // Once per request: continue where the viewer last settled instead of where the player happens to start.
    private async Task<VideoState> ResumeAsync(VideoState state, FollowRequest request)
    {
        if (_resumeVersion == request.Version) return state;
        if (_progress?.ResumePosition(request.Identity) is { } position && Math.Abs(state.CurrentTime - position) > 5)
        {
            state = await ReadVideoAsync(new("seek", position, Absolute: true), request);
            _resumedTo = position;
            AppLog.Info("Follow", $"续播：{request.Identity.Bvid} P{request.Identity.Part} 跳到 {TimeText.Format(position)}。");
        }
        _resumeVersion = request.Version;
        return state;
    }

    // Progress is auxiliary: a failure in it must never interrupt playback control.
    private void Track(Action<WatchProgressService> action)
    {
        if (_progress is null) return;
        try { action(_progress); }
        catch (Exception ex) { AppLog.Error("Progress", "进度记录出错。", ex); }
    }

    public Task ExecuteAsync(VideoCommand command) => _work.Track(ExecuteCoreAsync(command));
    private async Task ExecuteCoreAsync(VideoCommand command)
    {
        var request = Session.Current;
        AppLog.Info("Follow", $"命令 {command.Action}。");
        if (command.Action is "toggle" or "pip")
        {
            Session.Suppress();
            Publish(Current with { Phase = FollowPhase.Suppressed, CanRetry = false });
        }
        await GuardAsync(async () =>
        {
            if (request is null) throw new InvalidOperationException("请先开始跟随。");
            await _commands.WaitAsync(request.Token);
            try
            {
                EnsureCurrent(request);
                var state = await ReadVideoAsync(command, request);
                if (command.Action == "pip" && state.PictureInPicture) await PlaceAsync(state, request);
                EnsureCurrent(request);
                Message(command.Action switch
                {
                    "toggle" => state.Paused ? "视频已暂停。" : "视频继续播放。",
                    "seek" => $"已跳转到 {FormatTime(state.CurrentTime)}。",
                    "mute" => state.Muted ? "视频已静音。" : "已恢复视频声音。",
                    "rate" => $"播放速度 {state.PlaybackRate:0.##}×。",
                    "pip" => state.PictureInPicture ? "画中画已置顶在左下角。" : "画中画已关闭；当前分集不会自动重新开启。",
                    _ => "视频状态已更新。"
                });
            }
            finally { _commands.Release(); }
        }, () => request);
    }

    public Task PlacePipAsync() => _work.Track(PlaceCoreAsync());
    private async Task PlaceCoreAsync()
    {
        var request = Session.Current;
        await GuardAsync(async () =>
        {
            if (request is null) throw new InvalidOperationException("请先开始跟随。");
            var state = await ReadVideoAsync(new("status"), request);
            if (!state.PictureInPicture) throw new InvalidOperationException("请先开启画中画，再调整位置。");
            await PlaceAsync(state, request);
            EnsureCurrent(request);
            Message("画中画已放回本工具所在屏幕的左下角。");
        }, () => request);
    }

    private async Task PlaceAsync(VideoState state, FollowRequest request)
    {
        var pid = await _browser.GetBrowserProcessIdAsync(request.Token);
        EnsureCurrent(request);
        await _pip.PlaceAsync(pid, state, request.Token);
    }

    private async Task GuardAsync(Func<Task> action, Func<FollowRequest?> owner, bool quiet = false)
    {
        if (_stopped) return;
        try { await action(); }
        catch (OperationCanceledException) when (_stopped || owner() is { } request && !IsCurrent(request)) { }
        catch (Exception ex)
        {
            var request = owner();
            if (_stopped || request is not null && !IsCurrent(request)) return;
            if (!quiet)
            {
                AppLog.Warn("Follow", $"操作失败：{ex.Message}", ex);
                if (request is not null) Session.Complete(request);
                Publish(Current with { Phase = FollowPhase.Failed, CanRetry = request is not null, Message = ex.Message, IsError = true });
                return;
            }
            var message = ex is VideoNotReadyException or IOException or TimeoutException ? "正在等待视频就绪。需要登录时，请在 Chrome 中完成后等待。" : ex.Message;
            var phase = ex is VideoNotReadyException ? FollowPhase.WaitingForMedia : FollowPhase.Failed;
            if (ex is InvalidOperationException && ex is not VideoNotReadyException) Session.Suppress();
            if (_lastQuietError != message || Current.Phase != phase)
            {
                _lastQuietError = message;
                if (ex is VideoNotReadyException) AppLog.Debug("Follow", $"等待视频：{ex.Message}");
                else AppLog.Warn("Follow", $"轮询失败：{ex.Message}", ex);
                Publish(Current with { Video = null, Phase = phase, Message = message, IsError = ex is not VideoNotReadyException });
            }
        }
    }

    private bool IsCurrent(FollowRequest request) => !_stopped && Session.IsCurrent(request);
    private void EnsureCurrent(FollowRequest request) { if (!IsCurrent(request)) throw new OperationCanceledException(request.Token); }
    private void Message(string message, bool error = false) { if (!_stopped) Publish(Current with { Message = message, IsError = error }); }
    private void Publish(FollowSnapshot state)
    {
        if (_stopped) return;
        if (state.Phase != Current.Phase) AppLog.Info("Follow", $"阶段 {Current.Phase} → {state.Phase}：{state.Message}");
        Current = state with { Request = Session.Current, AutomaticPending = Session.AutomaticPending };
        Changed?.Invoke(Current);
    }
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }
    public void Stop() { if (!_stopped) AppLog.Info("Follow", "停止跟随。"); _stopped = true; _lifetime.Cancel(); Session.Suppress(); }
    public Task DrainAsync() => _work.DrainAsync();
    public void Dispose() { Stop(); Session.Dispose(); _lifetime.Dispose(); _commands.Dispose(); }
}
