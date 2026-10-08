using System.IO;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;

namespace GenshinVideoHelper.App.Composition;

/// <summary>The composition root owns concrete adapters; injected services remain owned by their caller.</summary>
public sealed class AppServices : IDisposable
{
    private readonly IDisposable? _ownedBrowser, _ownedEpisodes;
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
        return new(loaded.Settings, Path.Combine(ApplicationPaths.DataDirectory, "Chrome"), store, loadWarning: loaded.Warning);
    }

    public AppServices(AppSettings settings, string browserProfile, ISettingsStore? store = null,
        VideoLibraryCatalog? libraries = null, IEpisodeProvider? episodes = null,
        IBrowserSession? browser = null, IVideoPlayer? video = null, string? loadWarning = null, Func<IEpisodeProvider>? episodeFactory = null)
    {
        Settings = settings; SettingsStore = store; LoadWarning = loadWarning;
        Libraries = libraries ?? VideoLibraryLoader.LoadBuiltIn();
        if (browser is null) { var concrete = new ChromeBrowser(browserProfile); Browser = concrete; _ownedBrowser = concrete; }
        else Browser = browser;
        if (episodes is not null && episodeFactory is not null) throw new ArgumentException("Specify an episode service or a factory, not both.");
        if (episodes is null) { episodes = episodeFactory?.Invoke() ?? new BilibiliEpisodeService(); _ownedEpisodes = episodes as IDisposable; }
        var cache = new EpisodeCache();
        Pip = new(settings);
        Preview = new(episodes, cache);
        Follow = new(Browser, video ?? new VideoController(), episodes, Pip, cache);
    }

    public Task CloseAsync()
    {
        Follow.Stop(); Preview.Stop(); Pip.Dispose();
        return CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        try { await Task.WhenAll(Follow.DrainAsync(), Preview.DrainAsync()); }
        finally { await Browser.CloseAsync(); }
    }

    public void Dispose()
    {
        Follow.Dispose(); Preview.Dispose(); Pip.Dispose();
        _ownedBrowser?.Dispose(); _ownedEpisodes?.Dispose();
    }
}
