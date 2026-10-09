using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Core.Contracts;

public interface IBrowserSession
{
    Task<BrowserPage> OpenAsync(string videoUrl, CancellationToken token = default);
    Task NavigateAsync(BrowserPage page, VideoIdentity identity, CancellationToken token = default);
    Task<BrowserPage?> GetPageAsync(string targetId, CancellationToken token = default);
    Task<int> GetBrowserProcessIdAsync(CancellationToken token = default);
    Task CloseAsync();
}

public interface IVideoPlayer
{
    Task<VideoState> ExecuteAsync(BrowserPage page, VideoCommand command, CancellationToken cancellationToken = default);
}

public interface IEpisodeProvider
{
    Task<BilibiliVideoInfo> ReadAsync(BrowserPage page, CancellationToken token = default);
    Task<BilibiliVideoInfo> ReadFromApiAsync(VideoIdentity identity, CancellationToken token = default);
}

public interface IPipController
{
    Task ObserveAsync(int browserProcessId, bool enabled, CancellationToken token);
    Task PlaceAsync(int browserProcessId, VideoState state, CancellationToken token);
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);

public interface ISettingsStore
{
    SettingsLoadResult Load();
    void Save(AppSettings settings);
}

public sealed record ProgressLoadResult(Progress.ProgressDocument Document, string? Warning = null);

public interface IProgressStore
{
    ProgressLoadResult Load();
    void Save(Progress.ProgressDocument document);
}

public interface IBrowserWarmup
{
    Task WarmupAsync(CancellationToken token = default);
}

public interface IVideoActivitySource
{
    // Raised on a transport thread; the host dispatches to the application's state owner.
    event Action<string>? MediaActivity;
}
