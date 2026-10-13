using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Core.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Progress;

public sealed class WatchProgressServiceTests
{
    private const string Bvid = "BV1hjgG6jEa6";
    private const double Duration = 600;
    private readonly ManualClock _clock = new();
    private readonly FakeProgressStore _store = new();
    private readonly WatchProgressService _progress;

    public WatchProgressServiceTests()
    {
        _progress = new(_store, 0.9, _clock);
        _progress.Describe(Info());
    }

    private static long Cid(int part) => 40000000000L + part;
    private static BilibiliVideoInfo Info(Func<int, EpisodeInfo>? part = null) => new(Bvid, "古兽冰原", 1,
        Enumerable.Range(1, 4).Select(number => part?.Invoke(number) ?? new EpisodeInfo(number, Cid(number), "Part " + number, Duration)).ToArray());
    private Viewer Watch(int part) => new(_progress, _clock, new(Bvid, part), Cid(part), Duration);
    private EpisodeProgress Episode(int part) => _progress.Get(Bvid)!.FindPart(part)!;

    [Fact]
    public void Watching_ReachesCompletionRatio_CompletesOnlyThatEpisode()
    {
        var viewer = Watch(3).Play(538);
        Assert.Equal(EpisodeStatus.InProgress, Episode(3).Status);
        viewer.Play(4);
        Assert.Equal(EpisodeStatus.Completed, Episode(3).Status);
        Assert.NotNull(Episode(3).CompletedAt);
        // Finishing a later episode says nothing about the earlier ones.
        Assert.All([1, 2, 4], part => Assert.Equal(EpisodeStatus.NotStarted, Episode(part).Status));
    }

    [Fact]
    public void JumpingToTheEnd_NeverCompletes()
    {
        Watch(3).Play(10).Seek(Duration - 5).Play(5);
        Assert.Equal(EpisodeStatus.InProgress, Episode(3).Status);
        Assert.InRange(Episode(3).Coverage, 0.01, 0.05);
    }

    [Fact]
    public void ScrubbingAcrossTheWholeEpisode_RecordsNothing()
    {
        var viewer = Watch(3);
        for (var position = 0d; position < Duration; position += 20) viewer.Seek(position).Play(1);
        Assert.Empty(Episode(3).Segments);
        Assert.Equal(EpisodeStatus.NotStarted, Episode(3).Status);
    }

    [Fact]
    public void ResumePoint_MovesOnlyWherePlaybackSettled()
    {
        var viewer = Watch(3).Play(100);
        Assert.Equal(100, Episode(3).Position);
        viewer.Seek(500).Play(1);
        Assert.Equal(100, Episode(3).Position);
        viewer.Seek(101).Play(5);
        Assert.Equal(106, Episode(3).Position);
    }

    [Fact]
    public void RemovedWatchedRange_BecomesCompletedAgainAfterPlayback()
    {
        _progress.MarkWatched(Bvid, Cid(3), 0, Duration);
        _progress.MarkUnwatched(Bvid, Cid(3), 500, Duration);
        Assert.Equal(EpisodeStatus.InProgress, Episode(3).Status);
        Watch(3).Seek(500).Play(100);
        Assert.Equal(1, Episode(3).Coverage, 2);
        Assert.Equal(EpisodeStatus.Completed, Episode(3).Status);
    }

    [Fact]
    public void ChangedDuration_RecalculatesCompletionInBothDirections()
    {
        _progress.MarkWatched(Bvid, Cid(3), 0, 540);
        Assert.Equal(EpisodeStatus.Completed, Episode(3).Status);
        _progress.Describe(Info(part => new(part, Cid(part), "Part " + part, part == 3 ? 900 : Duration)));
        Assert.Equal(EpisodeStatus.InProgress, Episode(3).Status);
        Assert.Null(Episode(3).CompletedAt);
        _progress.Describe(Info(part => new(part, Cid(part), "Part " + part, part == 3 ? 420 : Duration)));
        Assert.Equal(EpisodeStatus.Completed, Episode(3).Status);
        Assert.Equal(420, Episode(3).Watched);
    }

    [Fact]
    public void ManualSpans_DriveCompletionInBothDirections()
    {
        Assert.True(_progress.MarkWatched(Bvid, Cid(2), 0, Duration));
        Assert.Equal(EpisodeStatus.Completed, Episode(2).Status);
        Assert.True(_progress.MarkUnwatched(Bvid, Cid(2), 100, 300));
        Assert.Equal(EpisodeStatus.InProgress, Episode(2).Status);
        Assert.Equal([new WatchSegment(0, 100), new WatchSegment(300, Duration)], Episode(2).Segments);
        Assert.False(_progress.MarkUnwatched(Bvid, Cid(2), 150, 250));
    }

    [Fact]
    public void CompleteBefore_ThenUndo_RestoresEveryEpisode()
    {
        Watch(1).Play(30);
        var before = Episode(1);
        Assert.True(_progress.CompleteBefore(Bvid, 3));
        Assert.All([1, 2], part => Assert.Equal(EpisodeStatus.Completed, Episode(part).Status));
        Assert.All([1, 2], part => Assert.Equal(1, Episode(part).Coverage));
        Assert.All([3, 4], part => Assert.Equal(EpisodeStatus.NotStarted, Episode(part).Status));
        Assert.Contains("P3", _progress.UndoDescription);
        Assert.True(_progress.Undo());
        Assert.Equal(before, Episode(1));
        Assert.Equal(EpisodeStatus.NotStarted, Episode(2).Status);
        Assert.Null(_progress.UndoDescription);
        Assert.False(_progress.Undo());
    }

    [Fact]
    public void CompleteAllAndReset_CoverTheWholeVideo()
    {
        Watch(2).Play(50);
        Assert.True(_progress.CompleteAll(Bvid));
        Assert.Equal(4, _progress.Get(Bvid)!.Completed);
        Assert.False(_progress.CompleteAll(Bvid));
        Assert.True(_progress.ResetVideo(Bvid));
        Assert.All(_progress.Get(Bvid)!.Episodes, episode => Assert.Equal(EpisodeStatus.NotStarted, episode.Status));
        Assert.True(_progress.Undo());
        Assert.Equal(4, _progress.Get(Bvid)!.Completed);
        Assert.All(_progress.Get(Bvid)!.Episodes, episode => Assert.Equal(1, episode.Coverage));
    }

    [Fact]
    public void BulkCompletion_UnknownDurationDoesNotInventWatchedCoverage()
    {
        _progress.Describe(Info(part => new(part, Cid(part), "Part " + part, part == 3 ? 0 : Duration)));
        Assert.True(_progress.CompleteAll(Bvid));
        Assert.Equal(3, _progress.Get(Bvid)!.Completed);
        Assert.Equal(EpisodeStatus.NotStarted, Episode(3).Status);
        Assert.Empty(Episode(3).Segments);
    }

    [Theory]
    [InlineData(0.9, EpisodeStatus.Completed)]
    [InlineData(0.95, EpisodeStatus.InProgress)]
    public void Load_RecalculatesCompletionFromCoverageAndConfiguredRatio(double ratio, EpisodeStatus expected)
    {
        var store = new FakeProgressStore
        {
            Document = new() { Videos = new Dictionary<string, VideoProgress>
            {
                [Bvid] = new(Bvid, "test") { Episodes = [
                    new(Cid(1), 1, "empty", Duration) { CompletedAt = _clock.GetUtcNow() },
                    new(Cid(2), 2, "watched", Duration) { Segments = [new(0, 540)] }]
                }
            } }
        };
        var service = new WatchProgressService(store, ratio, _clock);
        Assert.Equal(EpisodeStatus.NotStarted, service.Get(Bvid)!.FindPart(1)!.Status);
        Assert.Equal(expected, service.Get(Bvid)!.FindPart(2)!.Status);
        service.Flush();
        Assert.Equal(service.Get(Bvid)!.Episodes, store.Document.Videos[Bvid].Episodes);
    }

    [Fact]
    public void Undo_KeepsOnlyTheMostRecentSteps()
    {
        for (var step = 1; step <= 25; step++) Assert.True(_progress.SetPosition(Bvid, Cid(1), step * 10));
        var undone = 0;
        while (_progress.Undo()) undone++;
        Assert.Equal(20, undone);
        Assert.Equal(50, Episode(1).Position);
    }

    [Fact]
    public void Describe_ReplacedPart_KeepsProgressOnlyForTheSameContent()
    {
        _progress.MarkWatched(Bvid, Cid(3), 0, 300);
        // Re-uploaded with a new cid but the same length: the same episode.
        _progress.Describe(Info(part => new(part, part == 3 ? 777 : Cid(part), "Part " + part, part == 3 ? Duration + 1 : Duration)));
        Assert.Equal(777, Episode(3).Cid);
        Assert.Equal(300, Episode(3).Watched, 1);
        // A different length is different content: it starts over and the old record is archived.
        _progress.Describe(Info(part => new(part, part == 3 ? 888 : Cid(part), "Part " + part, part == 3 ? 900 : Duration)));
        Assert.Equal(888, Episode(3).Cid);
        Assert.Empty(Episode(3).Segments);
        Assert.Equal(777, Assert.Single(_progress.Get(Bvid)!.Retired).Cid);
    }

    [Fact]
    public void Observe_BeforePartsAreKnown_IsAdoptedWhenTheyArrive()
    {
        var progress = new WatchProgressService(clock: _clock);
        new Viewer(progress, _clock, new(Bvid, 3), Cid(3), Duration).Play(30);
        Assert.Equal(30, Assert.Single(progress.Get(Bvid)!.Episodes).Watched, 1);
        progress.Describe(Info());
        Assert.Equal(4, progress.Get(Bvid)!.Episodes.Count);
        Assert.Equal("Part 3", progress.Get(Bvid)!.FindPart(3)!.Title);
        Assert.Equal(30, progress.Get(Bvid)!.FindPart(3)!.Watched, 1);
    }

    [Fact]
    public void Describe_UnchangedParts_DoesNotRaiseOrDirty()
    {
        _progress.Flush();
        var saves = _store.Saves;
        var raised = 0;
        _progress.Changed += _ => raised++;
        _progress.Describe(Info());
        _progress.Flush();
        Assert.Equal(0, raised);
        Assert.Equal(saves, _store.Saves);
    }

    [Fact]
    public void Saving_IsThrottledDuringPlaybackAndImmediateAtBoundaries()
    {
        var viewer = Watch(1).Play(14);
        Assert.Equal(0, _store.Saves);
        viewer.Play(2);
        Assert.Equal(1, _store.Saves);
        viewer.Pause(3);
        Assert.Equal(2, _store.Saves);
        _progress.SetPosition(Bvid, Cid(2), 120);
        Assert.Equal(3, _store.Saves);
        _progress.Flush();
        Assert.Equal(3, _store.Saves);
        Assert.Equal(120, _store.Document.Videos[Bvid].FindPart(2)!.Position);
    }

    [Fact]
    public void Saving_SwitchingEpisodeAndCompleting_PersistAtOnce()
    {
        Watch(1).Play(5);
        var saves = _store.Saves;
        var next = Watch(2).Play(1);
        Assert.Equal(saves + 1, _store.Saves);
        _progress.MarkWatched(Bvid, Cid(2), 10, 545);
        saves = _store.Saves;
        next.Play(8);
        Assert.Equal(EpisodeStatus.Completed, Episode(2).Status);
        Assert.Equal(saves + 1, _store.Saves);
    }

    [Fact]
    public void SaveFailure_IsReportedOnceAndRetriedLater()
    {
        var failures = new List<string>();
        _progress.SaveFailed += failures.Add;
        _store.SaveError = new IOException("磁盘已满");
        _progress.SetPosition(Bvid, Cid(1), 60);
        _progress.SetPosition(Bvid, Cid(1), 90);
        Assert.Equal(["磁盘已满"], failures);
        _store.SaveError = null;
        _progress.Flush();
        Assert.Equal(90, _store.Document.Videos[Bvid].FindPart(1)!.Position);
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(100, 100d)]
    [InlineData(575, null)]
    public void ResumePosition_OnlyWhenWorthAJump(double position, double? expected)
    {
        _progress.SetPosition(Bvid, Cid(3), position);
        Assert.Equal(expected, _progress.ResumePosition(new(Bvid, 3)));
        _progress.MarkWatched(Bvid, Cid(3), 0, Duration);
        Assert.Null(_progress.ResumePosition(new(Bvid, 3)));
        Assert.Null(_progress.ResumePosition(new(Bvid, 9)));
    }

    [Fact]
    public void FirstUnfinishedPart_NeedsProgressToGoBy()
    {
        Assert.Null(_progress.FirstUnfinishedPart(Bvid, out var all));
        Assert.False(all);
        _progress.MarkWatched(Bvid, Cid(1), 0, Duration);
        Watch(2).Play(20);
        Assert.Equal(2, _progress.FirstUnfinishedPart(Bvid, out all));
        Assert.False(all);
        _progress.CompleteAll(Bvid);
        Assert.Null(_progress.FirstUnfinishedPart(Bvid, out all));
        Assert.True(all);
        Assert.Null(_progress.FirstUnfinishedPart("BV1MXfEY4EQ2", out _));
    }

    [Fact]
    public void Load_DamagedDocument_IsRepairedBeforeUse()
    {
        var store = new FakeProgressStore
        {
            Document = new()
            {
                Videos = new Dictionary<string, VideoProgress>
                {
                    [Bvid] = new("", null!)
                    {
                        Episodes =
                        [
                            new(Cid(2), 2, null!, Duration) { Segments = [new(400, 9000), new(-50, 30), new(20, 60), new(double.NaN, 5)], Position = double.NaN },
                            new(Cid(1), 1, "Part 1", double.NaN) { Position = -3 },
                            new(Cid(1), 1, "duplicate", Duration),
                            new(5, 0, "invalid part", Duration)
                        ],
                        Retired = null!
                    }
                }
            }
        };
        var video = new WatchProgressService(store).Get(Bvid)!;
        Assert.Equal(Bvid, video.Bvid);
        Assert.Equal([1, 2], video.Episodes.Select(episode => episode.Part));
        Assert.Equal([new WatchSegment(0, 60), new WatchSegment(400, Duration)], video.FindPart(2)!.Segments);
        Assert.Equal(0, video.FindPart(2)!.Position);
        Assert.Equal((0d, 0d), (video.FindPart(1)!.Duration, video.FindPart(1)!.Position));
        Assert.Empty(video.Retired);
    }
}
