using System.Diagnostics;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;

namespace GenshinVideoHelper.Core.Application;

public sealed record VisionIntent(BrowserPage Page, VideoIdentity Identity, long FollowVersion, nint GameWindow,
    int IntervalMs, int SearchIntervalMs, bool Direction);

/// <summary>One worker, one pair in flight, no frame queue. Source adapters are exclusively owned.</summary>
public sealed class MinimapFollowCoordinator : IDisposable
{
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
                    if (previous != generation) { _analyzer.Reset(); previous = generation; }
                    if (intent is null || !_game.IsForeground(intent.GameWindow))
                    {
                        _game.Reset();
                        Publish(new(generation, intent is null ? "未启用或等待跟随" : "等待游戏前台"));
                        interval = 300;
                    }
                    else
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(800);
                        // These sources run independently, outside the player's operation lock.
                        var videoTask = _video.CaptureAsync(intent.Page, intent.Identity, _analyzer.VideoPlan, deadline.Token);
                        VisionFrame? game = null; VisionFrame? video = null;
                        try
                        {
                            game = await _game.CaptureAsync(intent.GameWindow, _analyzer.GamePlan, deadline.Token);
                            video = await videoTask;
                            var captureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            if (generation != Generation) continue;
                            if (video is null || game is null)
                                Publish(new(generation, video is null ? "等待有效视频画面与画中画" : "等待游戏捕获画面"));
                            else if (Stopwatch.GetElapsedTime(game.CapturedAt).TotalMilliseconds > 250 ||
                                     Stopwatch.GetElapsedTime(video.CapturedAt).TotalMilliseconds > 250)
                                Publish(new(generation, "画面已过期，等待新帧"));
                            else
                            {
                                var analysisStart = Stopwatch.GetTimestamp();
                                var result = _analyzer.Analyze(video, game, intent.Direction, intent.SearchIntervalMs);
                                var elapsed = Stopwatch.GetElapsedTime(analysisStart).TotalMilliseconds;
                                if (result.Marker is null) interval = Math.Max(interval, intent.SearchIntervalMs);
                                if (!_game.IsForeground(intent.GameWindow) || Stopwatch.GetElapsedTime(game.CapturedAt).TotalMilliseconds > 250)
                                    Publish(new(generation, "等待新鲜的游戏画面"));
                                else Publish(new(generation, result.Status, result.Marker, intent.GameWindow, game.CapturedAt,
                                    captureMs, elapsed, result.Correlation, result.Inliers));
                            }
                        }
                        finally
                        {
                            game?.Dispose(); video?.Dispose();
                            // A game capture error must still observe and release the independent video request.
                            if (video is null) { try { (await videoTask)?.Dispose(); } catch { } }
                        }
                    }
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                { Publish(new(generation, "采集超时，稍后重试")); interval = 1000; }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    Publish(new(generation, $"识别暂不可用：{ex.Message.Split('\n')[0]}"));
                    _game.Reset(); _analyzer.Reset(); interval = 1000;
                }
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await Task.Delay(Math.Max(10, interval - (int)spent), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _game.Dispose(); _analyzer.Dispose(); }
    }
    public Task StopAsync() { _stop.Cancel(); Invalidate(); return _worker; }
    public void Dispose() { _stop.Cancel(); }
}
