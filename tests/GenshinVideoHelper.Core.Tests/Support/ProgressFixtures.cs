using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;

namespace GenshinVideoHelper.Core.Tests.Support;

/// <summary>Time that only moves when a test says so.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);
    public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);
    public void Advance(double seconds) => _ticks += (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}

internal sealed class FakeProgressStore : IProgressStore
{
    public ProgressDocument Document { get; set; } = new();
    public int Saves { get; private set; }
    public Exception? SaveError { get; set; }
    public ProgressLoadResult Load() => new(Document);
    public void Save(ProgressDocument document)
    {
        if (SaveError is not null) throw SaveError;
        Document = document;
        Saves++;
    }
}

/// <summary>Drives a progress service the way the follow coordinator does: one state per poll.</summary>
internal sealed class Viewer(WatchProgressService progress, ManualClock clock, VideoIdentity identity, long cid, double duration)
{
    public double Position { get; private set; }
    public bool Paused { get; private set; }
    public double Rate { get; set; } = 1;

    private void Sample() => progress.Observe(identity, cid,
        new VideoState("fixture", identity.Url, Paused, Position, duration, false, Rate, true, 640, 360, cid), clock.Elapsed);

    /// <summary>Plays for the given wall-clock seconds, polled once a second.</summary>
    public Viewer Play(double seconds)
    {
        Paused = false;
        Sample();
        for (var elapsed = 0d; elapsed < seconds; elapsed += 1)
        {
            var step = Math.Min(1, seconds - elapsed);
            clock.Advance(step);
            Position = Math.Min(duration, Position + step * Rate);
            Sample();
        }
        return this;
    }

    public Viewer Pause(double seconds)
    {
        Paused = true;
        Sample();
        for (var elapsed = 0d; elapsed < seconds; elapsed += 1) { clock.Advance(1); Sample(); }
        return this;
    }

    /// <summary>Jumps the playhead; the next poll arrives shortly after, as a media event would trigger it.</summary>
    public Viewer Seek(double position)
    {
        clock.Advance(0.2);
        Position = Math.Clamp(position, 0, duration);
        Sample();
        return this;
    }
}
