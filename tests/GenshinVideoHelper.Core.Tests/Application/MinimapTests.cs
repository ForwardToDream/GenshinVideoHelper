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
        s.IntervalMs=1;s.MarkerSize=100;s.Normalize();
        Assert.Equal(50,s.IntervalMs);Assert.Equal(9,s.MarkerSize);
    }
    [Theory][InlineData(1080,168,125,106)][InlineData(2160,336,250,212)]
    public void UserHudIsFixedAndGuideSearchIsOneSixth(int height,double x,double y,double radius)
    {
        Assert.Equal(new MapDisk(x,y,radius),MinimapGeometry.GameDisk(height));
        int width=height*16/9;var guide=MinimapGeometry.GuideSearchRegion(width,height);
        Assert.Equal(new FrameRegion(0,0,width/3,height/2),guide);
        var user=MinimapGeometry.GameCaptureRegion(width,height);
        Assert.True(user.X>0);Assert.True(user.Y>=0);Assert.True(user.Width<width/3);
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
    [Fact] public async Task FailedMatchesKeepSamplingAndOnlySameSourceTransientMissesRetainMarker()
    {
        var video=new Video();var game=new Game();
        using var completed=new SemaphoreSlim(0);
        var updates=new System.Collections.Concurrent.ConcurrentQueue<VisionUpdate>();
        var analyzer=new SequenceAnalyzer(call=>
        {
            if(call==4)video.Epoch="new-episode";
            return call switch
            {
                1 or 3 or 6=>new("matched",new(1,1,null,0xff0000,new(10,10,9))),
                4=>new("black map"),
                _=>new("transient",RetainMarker:true)
            };
        });
        using var coordinator=new MinimapFollowCoordinator(video,game,analyzer);
        coordinator.Updated+=u=>{if(u.Status is "matched" or "transient" or "black map"){updates.Enqueue(u);completed.Release();}};
        coordinator.Configure(Intent(1));
        try
        {
            for(int i=0;i<6;i++)Assert.True(await completed.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken));
        }
        finally {await coordinator.StopAsync();}
        var received=updates.ToArray();
        Assert.NotNull(received[0].Marker);Assert.True(received[1].RetainMarker);
        Assert.NotNull(received[2].Marker);Assert.False(received[3].RetainMarker);
        Assert.False(received[4].RetainMarker);Assert.NotNull(received[5].Marker);
        var times=analyzer.Times.ToArray();
        Assert.InRange(Stopwatch.GetElapsedTime(times[1],times[2]).TotalMilliseconds,0,800);
    }
    private sealed class SequenceAnalyzer(Func<int,MapAnalysis> next):IMinimapAnalyzer
    {
        private int _calls;
        public System.Collections.Concurrent.ConcurrentQueue<long> Times=[];
        public CapturePlan VideoPlan=>new();public CapturePlan GamePlan=>new();
        public void Reset(){}public void Dispose(){}
        public MapAnalysis Analyze(VisionFrame v,VisionFrame g,bool d)
        {Times.Enqueue(Stopwatch.GetTimestamp());return next(++_calls);}
    }
    private static VisionIntent Intent(long version)=>new(new("tab","video","https://www.bilibili.com/video/BV1hjgG6jEa6/","ws://localhost"),new("BV1hjgG6jEa6",1),version,123,100,true);
    private static VisionFrame Frame()=>new(10,10,3,20,20,new(0,0,10,10),"epoch");
    private sealed class Video:IVideoFrameSource
    {
        public List<VisionFrame> Frames=[];public string Epoch="epoch";
        public Task<VisionFrame?> CaptureAsync(BrowserPage p,VideoIdentity id,CapturePlan plan,CancellationToken token){var f=new VisionFrame(10,10,3,20,20,new(0,0,10,10),Epoch);Frames.Add(f);return Task.FromResult<VisionFrame?>(f);}
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
        public MapAnalysis Analyze(VisionFrame v,VisionFrame g,bool d)
        {Calls++;Entered.TrySetResult();Release.Wait(TimeSpan.FromSeconds(2));return new("matched",new(1,1,0,0xff0000,new(10,10,9)));}
    }
}
