using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App.Native;

public sealed class WindowsPipController : IPipController, IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore? _store;
    public nint HelperWindow { get; set; }
    public PipMouseVisibilityService MouseVisibility { get; }

    public WindowsPipController(AppSettings settings, ISettingsStore? store = null)
    {
        _settings = settings;
        _store = store;
        MouseVisibility = new(getPlacement: () => settings.PipPlacement, placementChanged: RememberPlacement);
    }
    public Task ObserveAsync(int browserProcessId, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (enabled && MouseVisibility.BrowserProcessId != browserProcessId) MouseVisibility.TrackBrowser(browserProcessId);
        else if (!enabled) MouseVisibility.Suspend();
        return Task.CompletedTask;
    }
    public Task RestoreAsync(int browserProcessId, VideoState state, CancellationToken token) =>
        PlaceCoreAsync(browserProcessId, state, restore: true, token);

    public Task PlaceAsync(int browserProcessId, VideoState state, CancellationToken token) =>
        PlaceCoreAsync(browserProcessId, state, restore: false, token);

    private async Task PlaceCoreAsync(int browserProcessId, VideoState state, bool restore, CancellationToken token)
    {
        MouseVisibility.FlushPlacement();
        var ratio = state.VideoHeight > 0 ? (double)state.VideoWidth / state.VideoHeight : 16d / 9;
        await PipWindowService.WaitForPipAsync(browserProcessId, token);
        MouseVisibility.TrackBrowser(browserProcessId);
        await PipWindowService.PlaceAsync(browserProcessId, HelperWindow, _settings.PipWidth, _settings.PipMargin, ratio, token,
            restore ? _settings.PipPlacement : null);
        MouseVisibility.Refresh();
        if (!restore && MouseVisibility.Placement is { } placement) RememberPlacement(placement);
    }

    private void RememberPlacement(PipPlacement placement)
    {
        if (_settings.PipPlacement == placement) return;
        _settings.PipPlacement = placement;
        try { _store?.Save(_settings); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { AppLog.Warn("Settings", "浮窗位置和大小未能保存。", ex); }
    }
    public void Dispose() => MouseVisibility.Dispose();
}
