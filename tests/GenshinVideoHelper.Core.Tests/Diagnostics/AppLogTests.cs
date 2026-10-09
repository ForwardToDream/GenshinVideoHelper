using System.Collections.Concurrent;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Diagnostics;

// The sink is process-wide and other test classes run in parallel, so assertions only look at entries this class caused.
public sealed class AppLogTests : IDisposable
{
    private sealed class Capture : ILogSink
    {
        public ConcurrentQueue<(LogLevel Level, string Source, string Message, Exception? Exception)> Entries { get; } = new();
        public bool Throw { get; set; }
        public void Write(LogLevel level, string source, string message, Exception? exception)
        {
            if (Throw) throw new IOException("sink failure");
            Entries.Enqueue((level, source, message, exception));
        }
    }

    private readonly Capture _capture = new();
    private readonly string _source = "Test-" + Guid.NewGuid().ToString("N");
    public AppLogTests() { AppLog.Sink = _capture; AppLog.MinLevel = LogLevel.Info; }
    public void Dispose() { AppLog.Sink = null; AppLog.MinLevel = LogLevel.Info; }

    [Fact]
    public void Write_BelowMinimumLevel_IsFiltered()
    {
        AppLog.Debug(_source, "hidden");
        AppLog.Warn(_source, "shown");
        Assert.Equal(["shown"], _capture.Entries.Where(entry => entry.Source == _source).Select(entry => entry.Message));
    }

    [Fact]
    public void Write_SinkThrows_DoesNotReachCaller()
    {
        _capture.Throw = true;
        AppLog.Error(_source, "ignored", new InvalidOperationException());
    }

    [Fact]
    public async Task Follow_PhaseChangesAndFailures_AreRecorded()
    {
        var video = new FakeVideo { Reply = (_, _, _) => Task.FromException<VideoState>(new InvalidOperationException("page script failed")) };
        using var follow = new FollowCoordinator(new FakeBrowser(), video, new FakeEpisodes(), new FakePip(), new());
        await follow.OpenAsync(new("BV1Lg4y1q7Pd", 2));
        await follow.ExecuteAsync(new("mute"));
        var entries = _capture.Entries.Where(entry => entry.Source == "Follow").ToArray();
        Assert.Contains(entries, entry => entry.Message.Contains("BV1Lg4y1q7Pd P2"));
        Assert.Contains(entries, entry => entry.Message.Contains("Opening → WaitingForMedia"));
        Assert.Contains(entries, entry => entry is { Level: LogLevel.Warn, Exception: InvalidOperationException { Message: "page script failed" } });
        Assert.Contains(entries, entry => entry.Message.Contains("→ Failed"));
    }
}
