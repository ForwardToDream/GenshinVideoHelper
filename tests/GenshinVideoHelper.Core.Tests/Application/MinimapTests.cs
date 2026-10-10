using System.Diagnostics;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Vision;
using Xunit;
namespace GenshinVideoHelper.Core.Tests.Application;
public sealed class MinimapTests
{
    [Fact] public void DefaultsAndBounds()
    {
        var s=new VisionSettings();Assert.Equal(100,s.IntervalMs);Assert.True(s.Enabled);
        s.IntervalMs=1;s.SearchIntervalMs=0;s.MarkerSize=100;s.Normalize();
        Assert.Equal(50,s.IntervalMs);Assert.Equal(1000,s.SearchIntervalMs);Assert.Equal(9,s.MarkerSize);
    }
    [Fact] public void ProjectionRotatesDirectionWithoutTranslation()
    {
        var p=MapProjection.Position(0,1,220,-20);Assert.Equal(114,p.X);Assert.Equal(86,p.Y);
        Assert.Equal(Math.PI/2,MapProjection.Direction(0,0,1),8);Assert.Equal(2,MapProjection.AngularDistance(359,1));
        var r=new MapDisk(10,20,30).CaptureRegion(100,100);Assert.Equal(0,r.X);Assert.Equal(0,r.Y);Assert.True(r.Width<=100);
    }
    [Fact] public async Task ObsoleteAnalysisIsNeverPublishedAndFramesAreReleased()
    {
        var video=new Video();var game=new Game();var analyzer=new Analyzer();
        using var coordinator=new MinimapFollowCoordinator(video,game,analyzer);
        var messages=new System.Collections.Concurrent.ConcurrentBag<VisionUpdate>();coordinator.Updated+=messages.Add;
        coordinator.Configure(Intent(1));
        await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        long old=coordinator.Generation;coordinator.Configure(null);analyzer.Release.Set();
        await Task.Delay(150,TestContext.Current.CancellationToken);await coordinator.StopAsync();
        Assert.DoesNotContain(messages,u=>u.Generation==old&&u.Marker is not null);
        Assert.True(analyzer.Disposed);Assert.True(game.Disposed);
        Assert.All(video.Frames,f=>Assert.Throws<ObjectDisposedException>(()=>f.Pixels));
        Assert.All(game.Frames,f=>Assert.Throws<ObjectDisposedException>(()=>f.Pixels));
        Assert.Equal(1,analyzer.Calls);
    }
    [Fact] public async Task ForegroundGateDoesNotCaptureAndStopCancelsPendingRead()
    {
        var video=new Video();var game=new Game{Foreground=false};var analyzer=new Analyzer();
        using var coordinator=new MinimapFollowCoordinator(video,game,analyzer);coordinator.Configure(Intent(1));
        await Task.Delay(100,TestContext.Current.CancellationToken);Assert.Empty(video.Frames);Assert.Empty(game.Frames);
        game.Foreground=true;analyzer.Release.Set();
        await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);await coordinator.StopAsync();
        Assert.True(game.Disposed);
    }
    private static VisionIntent Intent(long version)=>new(new("tab","video","https://www.bilibili.com/video/BV1hjgG6jEa6/","ws://localhost"),new("BV1hjgG6jEa6",1),version,123,100,1000,true);
    private static VisionFrame Frame()=>new(10,10,3,20,20,new(0,0,10,10),"epoch");
    private sealed class Video:IVideoFrameSource
    {
        public List<VisionFrame> Frames=[];
        public Task<VisionFrame?> CaptureAsync(BrowserPage p,VideoIdentity id,CapturePlan plan,CancellationToken token){var f=Frame();Frames.Add(f);return Task.FromResult<VisionFrame?>(f);}
    }
    private sealed class Game:IGameFrameSource
    {
        public List<VisionFrame> Frames=[];public volatile bool Foreground=true;public bool Disposed;
        public bool IsForeground(nint window)=>Foreground;
        public ValueTask<VisionFrame?> CaptureAsync(nint w,CapturePlan p,CancellationToken t){var f=Frame();Frames.Add(f);return ValueTask.FromResult<VisionFrame?>(f);}
        public void Reset(){}public void Dispose()=>Disposed=true;
    }
    private sealed class Analyzer:IMinimapAnalyzer
    {
        public CapturePlan VideoPlan=>new();public CapturePlan GamePlan=>new();
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release=new();public bool Disposed;public int Calls;
        public void Reset(){}public void Dispose(){Disposed=true;Release.Dispose();}
        public MapAnalysis Analyze(VisionFrame v,VisionFrame g,bool d,int search)
        {Calls++;Entered.TrySetResult();Release.Wait(TimeSpan.FromSeconds(2));return new("matched",new(1,1,0,0xff0000,new(10,10,9)));}
    }
}
