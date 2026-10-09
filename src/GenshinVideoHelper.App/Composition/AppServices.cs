using System.IO;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.App.Composition;

/// <summary>The composition root owns concrete adapters; injected services remain owned by their caller.</summary>
public sealed class AppServices : IDisposable
{
    private readonly IDisposable? _ownedBrowser, _ownedEpisodes, _ownedVideo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _enableWarmup;
    private Task _warmup = Task.CompletedTask;
    private Task? _closing;
    private bool _warmupStarted;
    public IVideoActivitySource? VideoActivity { get; }
    public AppSettings Settings { get; }
    public ISettingsStore? SettingsStore { get; }
    public string? LoadWarning { get; }
    public VideoLibraryCatalog Libraries { get; }
    public IBrowserSession Browser { get; }
    public FollowCoordinator Follow { get; }
    public EpisodePreviewService Preview { get; }
    public WindowsPipController Pip { get; }

    public static AppServices CreateDefault()
    {
        var store = new JsonSettingsStore(ApplicationPaths.SettingsPath);
        var loaded = store.Load();
        AppLog.MinLevel = loaded.Settings.LogLevel;
        AppLog.Info("App", $"配置已加载，日志级别 {loaded.Settings.LogLevel}{(loaded.Warning is null ? "" : "；" + loaded.Warning)}。");
        return new(loaded.Settings, Path.Combine(ApplicationPaths.DataDirectory, "Chrome"), store, loadWarning: loaded.Warning, enableWarmup: true);
    }

    public AppServices(AppSettings settings, string browserProfile, ISettingsStore? store = null,
        VideoLibraryCatalog? libraries = null, IEpisodeProvider? episodes = null,
        IBrowserSession? browser = null, IVideoPlayer? video = null, string? loadWarning = null, Func<IEpisodeProvider>? episodeFactory = null, bool enableWarmup = false)
    {
        _enableWarmup = enableWarmup;
        Settings = settings; SettingsStore = store; LoadWarning = loadWarning;
        Libraries = libraries ?? VideoLibraryLoader.LoadBuiltIn();
        if (browser is null) { var concrete = new ChromeBrowser(browserProfile); Browser = concrete; _ownedBrowser = concrete; }
        else Browser = browser;
        if (episodes is not null && episodeFactory is not null) throw new ArgumentException("Specify an episode service or a factory, not both.");
        if (episodes is null) { episodes = episodeFactory?.Invoke() ?? new BilibiliEpisodeService(); _ownedEpisodes = episodes as IDisposable; }
        var cache = new EpisodeCache();
        Pip = new(settings);
        Preview = new(episodes, cache);
        if (video is null) { var concrete = new VideoController(); video = concrete; _ownedVideo = concrete; }
        VideoActivity = video as IVideoActivitySource;
        Follow = new(Browser, video, episodes, Pip, cache);
    }

    public Task WarmupAsync()
    {
        if (!_warmupStarted && _enableWarmup && Browser is IBrowserWarmup warmup && !_lifetime.IsCancellationRequested)
        { _warmupStarted = true; _warmup = WarmupCoreAsync(warmup); }
        return _warmup;
    }
    private async Task WarmupCoreAsync(IBrowserWarmup warmup)
    {
        try { await warmup.WarmupAsync(_lifetime.Token); }
        catch (OperationCanceledException) { AppLog.Info("App", "Chrome 预热已取消。"); }
        catch (Exception ex) { AppLog.Warn("App", "Chrome 预热失败，将在开始跟随时重试。", ex); }
    }
    public Task CloseAsync()
    {
        if (_closing is not null) return _closing;
        _lifetime.Cancel();
        Follow.Stop(); Preview.Stop(); Pip.Dispose();
        return _closing = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        // Browser shutdown must not queue behind pending page requests.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var work = Task.WhenAll(_warmup, Follow.DrainAsync(), Preview.DrainAsync(), Browser.CloseAsync());
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(2.5));
            AppLog.Info("App", $"退出清理完成，{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms。");
        }
        catch (TimeoutException) { AppLog.Warn("App", "退出清理超过 2.5 秒预算，不再等待。"); }
    }
    public void Dispose()
    {
        Follow.Dispose(); Preview.Dispose(); Pip.Dispose();
        _ownedVideo?.Dispose(); _ownedBrowser?.Dispose(); _ownedEpisodes?.Dispose();
    }
}
