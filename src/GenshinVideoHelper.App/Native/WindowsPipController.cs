using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App.Native;

public sealed class WindowsPipController(AppSettings settings) : IPipController, IDisposable
{
    public nint HelperWindow { get; set; }
    public PipMouseVisibilityService MouseVisibility { get; } = new();
    public Task ObserveAsync(int browserProcessId, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (enabled && MouseVisibility.BrowserProcessId != browserProcessId) MouseVisibility.TrackBrowser(browserProcessId);
        else if (!enabled) MouseVisibility.Suspend();
        return Task.CompletedTask;
    }
    public async Task PlaceAsync(int browserProcessId, VideoState state, CancellationToken token)
    {
        var ratio = state.VideoHeight > 0 ? (double)state.VideoWidth / state.VideoHeight : 16d / 9;
        await PipWindowService.PlaceAsync(browserProcessId, HelperWindow, settings.PipWidth, settings.PipMargin, ratio, token);
        MouseVisibility.TrackBrowser(browserProcessId);
    }
    public void Dispose() => MouseVisibility.Dispose();
}
