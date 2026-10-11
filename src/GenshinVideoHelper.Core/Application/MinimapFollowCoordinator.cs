using System.Diagnostics;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;

namespace GenshinVideoHelper.Core.Application;

public sealed record VisionIntent(BrowserPage Page, VideoIdentity Identity, long FollowVersion, nint GameWindow,
    int IntervalMs, bool Direction);

/// <summary>One analysis worker, one guide read in flight. Slow guide reads never stop game sampling.</summary>
public sealed class MinimapFollowCoordinator : IDisposable
{
    private sealed record GuideRead(Task<VisionFrame?> Task, CancellationTokenSource Cancellation, long Started);
    private readonly IVideoFrameSource _video;
    private readonly IGameFrameSource _game;
    private readonly IMinimapAnalyzer _analyzer;
    private readonly CancellationTokenSource _stop = new();
    private VisionIntent? _intent;
    private long _generation;
    private readonly Task _worker;
    private string? _loggedStatus;
    public event Action<VisionUpdate>? Updated;
    public bool IsConfigured => Volatile.Read(ref _intent) is not null;
    public long Generation => Interlocked.Read(ref _generation);
    public MinimapFollowCoordinator(IVideoFrameSource video, IGameFrameSource game, IMinimapAnalyzer analyzer)
    {
        _video = video; _game = game; _analyzer = analyzer;
        _worker = Task.Run(RunAsync);
    }
    public void Configure(VisionIntent? intent)
    {
        var old = Interlocked.Exchange(ref _intent, intent);
        if (old != intent) Invalidate();
    }
    public void Invalidate()
    {
        var generation = Interlocked.Increment(ref _generation);
        Updated?.Invoke(new(generation, _intent is null ? "未启用或等待跟随" : "正在校验地图"));
    }
    private void Publish(VisionUpdate update)
    {
        if (_stop.IsCancellationRequested || update.Generation != Generation) return;
        if (_loggedStatus != update.Status)
        { _loggedStatus = update.Status; AppLog.Info("Vision", update.Status); }
        Updated?.Invoke(update);
    }
    private async Task RunAsync()
    {
        long previous = -1;
        string? videoEpoch = null, gameEpoch = null, guideError = null;
        VisionFrame? guide = null;
        GuideRead? reading = null;
        double guideMs = 0;
        var retired = new List<Task>();
        void ClearGuide()
        {
            guide?.Dispose(); guide = null;
            if (reading is not null)
            {
                reading.Cancellation.Cancel();
                retired.Add(ReleaseReadAsync(reading)); reading = null;
            }
            videoEpoch = gameEpoch = guideError = null;
            _analyzer.Reset();
        }
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var intent = Volatile.Read(ref _intent);
                var generation = Generation;
                int interval = intent?.IntervalMs ?? 300;
                try
                {
                    retired.RemoveAll(task => task.IsCompleted);
                    if (previous != generation) { ClearGuide(); previous = generation; }
                    if (intent is null || !_game.IsForeground(intent.GameWindow))
                    {
                        if (guide is not null || reading is not null) ClearGuide();
                        _game.Reset();
                        Publish(new(generation, intent is null ? "未启用或等待跟随" : "等待游戏前台"));
                        interval = 300;
                    }
                    else
                    {
                        // A cached guide can be matched against every new game frame while the next read is pending.
                        if (reading?.Task.IsCompleted == true)
                        {
                            var completed = reading; reading = null;
                            try
                            {
                                var next = await completed.Task;
                                guideMs = Stopwatch.GetElapsedTime(completed.Started).TotalMilliseconds;
                                guideError = null;
                                if (next?.Epoch != guide?.Epoch)
                                { _analyzer.Reset(); videoEpoch = gameEpoch = null; Publish(new(generation, "正在校验地图")); }
                                guide?.Dispose(); guide = next;
                                // Null means the page/PiP/episode is invalid, rather than a slow read.
                                if (guide is null) Publish(new(generation, "等待有效视频画面与画中画"));
                            }
                            catch (Exception ex) when (!_stop.IsCancellationRequested)
                            {
                                var message = ex is OperationCanceledException ? "攻略采集暂未完成，正在重试" : $"攻略采集暂不可用：{ex.Message.Split('\n')[0]}";
                                if (guideError != message) AppLog.Warn("Vision", message);
                                guideError = message;
                            }
                            finally { completed.Cancellation.Dispose(); }
                        }
                        if (generation != Generation) continue;
                        if (reading is null)
                        {
                            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            var readStarted = Stopwatch.GetTimestamp();
                            try { reading = new(_video.CaptureAsync(intent.Page, intent.Identity, _analyzer.VideoPlan, cancellation.Token), cancellation, readStarted); }
                            catch { cancellation.Dispose(); throw; }
                        }
                        using var game = await _game.CaptureAsync(intent.GameWindow, _analyzer.GamePlan, _stop.Token);
                        var gameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (generation != Generation) continue;
                        if (guide is null || game is null)
                            Publish(new(generation, guide is null ? guideError ?? "等待有效视频画面与画中画" : "等待游戏捕获画面",
                                RetainMarker: guide is not null && guide.Epoch == videoEpoch));
                        else
                        {
                            var sameSources = guide.Epoch == videoEpoch && game.Epoch == gameEpoch;
                            videoEpoch = guide.Epoch; gameEpoch = game.Epoch;
                            var analysisStart = Stopwatch.GetTimestamp();
                            var result = _analyzer.Analyze(guide, game, intent.Direction);
                            var elapsed = Stopwatch.GetElapsedTime(analysisStart).TotalMilliseconds;
                            if (!_game.IsForeground(intent.GameWindow)) Publish(new(generation, "等待游戏前台"));
                            else Publish(new(generation, result.Status, result.Marker, intent.GameWindow, game.CapturedAt,
                                guideMs + gameMs, elapsed, result.Correlation, result.Inliers, result.RetainMarker && sameSources));
                        }
                    }
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                { Publish(new(generation, "等待游戏捕获画面", RetainMarker: guide is not null)); }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    Publish(new(generation, $"识别暂不可用：{ex.Message.Split('\n')[0]}"));
                    _game.Reset(); ClearGuide();
                }
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await Task.Delay(Math.Max(10, interval - (int)spent), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            ClearGuide();
            await Task.WhenAll(retired);
            _game.Dispose(); _analyzer.Dispose();
        }
    }
    private static async Task ReleaseReadAsync(GuideRead read)
    {
        try { (await read.Task)?.Dispose(); }
        catch { /* Retired results must never surface in the new session. */ }
        finally { read.Cancellation.Dispose(); }
    }
    public Task StopAsync() { _stop.Cancel(); Invalidate(); return _worker; }
    public void Dispose() { _stop.Cancel(); }
}
