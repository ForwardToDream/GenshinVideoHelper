using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Application;

public sealed record EpisodePreview(VideoIdentity? Identity, BilibiliVideoInfo? Info, bool Loading, string? Error);

/// <summary>Selection and late-response isolation, independent of the active browser session. Called by one UI owner.</summary>
public sealed class EpisodePreviewService(IEpisodeProvider provider, EpisodeCache cache) : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private long _version;
    private Task? _loading;
    private readonly BackgroundWork _work = new();
    private bool _stopped;
    public EpisodePreview Current { get; private set; } = new(null, null, false, null);
    public event Action<EpisodePreview>? Changed;

    public void Select(string? url, bool forceReload = false)
    {
        if (_stopped) return;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _version++;
        _loading = null;
        if (!VideoIdentity.TryParse(url, out var identity)) { Publish(new(null, null, false, null)); return; }
        _cancellation = new();
        var cached = forceReload ? null : cache.Get(identity!);
        Publish(new(identity, cached, cached is null, null));
    }

    public Task LoadAsync(BrowserPage? page = null)
    {
        if (_stopped || Current.Identity is null || !Current.Loading) return Task.CompletedTask;
        return _loading ??= _work.Track(ReadAsync(Current.Identity, page, _version, _cancellation!.Token));
    }

    private async Task ReadAsync(VideoIdentity selected, BrowserPage? page, long version, CancellationToken token)
    {
        try
        {
            var identity = selected with { Part = 1 }; // Preserve the list for correction of an invalid P.
            var info = page is null ? await provider.ReadFromApiAsync(identity, token) : await provider.ReadAsync(page with { Url = identity.Url }, token);
            if (!IsCurrent()) return;
            cache.Store(info);
            Publish(new(selected, info with { CurrentPart = selected.Part }, false, null));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn("Preview", $"分集预览失败：{selected.Bvid}", ex);
            if (IsCurrent()) Publish(new(selected, null, false, ex.Message));
        }
        bool IsCurrent() => !_stopped && !token.IsCancellationRequested && version == _version;
    }

    private void Publish(EpisodePreview state) { Current = state; Changed?.Invoke(state); }
    public void Stop() { _stopped = true; _cancellation?.Cancel(); }
    public Task DrainAsync() => _work.DrainAsync();
    public void Dispose() { Stop(); _cancellation?.Dispose(); }
}
