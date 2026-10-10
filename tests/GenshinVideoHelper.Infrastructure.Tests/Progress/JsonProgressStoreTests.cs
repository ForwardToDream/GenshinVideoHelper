using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Infrastructure.Progress;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Progress;

public sealed class JsonProgressStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "progress.json");
    private string[] Unreadable() => Directory.GetFiles(_folder, "progress.json.unreadable-*.bak");

    private static ProgressDocument Sample(double position = 312.5) => new()
    {
        Videos = new Dictionary<string, VideoProgress>
        {
            ["BV1hjgG6jEa6"] = new("BV1hjgG6jEa6", "古兽冰原 · 全宝箱")
            {
                UpdatedAt = new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.FromHours(8)),
                Episodes =
                [
                    new(40841121301, 1, "说明", 24) { Mark = ProgressMark.Completed },
                    new(40835484789, 3, "古兽冰原1-7", 604)
                    {
                        Segments = [new(0, 312.5), new(400, 520)], Position = position,
                        CompletedAt = new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.FromHours(8))
                    }
                ],
                Retired = [new(1, 2, "旧分集", 30) { Segments = [new(0, 12)] }]
            }
        }
    };

    [Fact]
    public void Load_MissingFile_ReturnsEmptyWithoutCreatingIt()
    {
        var loaded = new JsonProgressStore(FilePath).Load();
        Assert.Null(loaded.Warning);
        Assert.Empty(loaded.Document.Videos);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryField()
    {
        new JsonProgressStore(FilePath).Save(Sample());
        var video = new JsonProgressStore(FilePath).Load().Document.Videos["BV1hjgG6jEa6"];
        var expected = Sample().Videos["BV1hjgG6jEa6"];
        Assert.Equal((expected.Title, expected.UpdatedAt), (video.Title, video.UpdatedAt));
        Assert.Equal(ProgressMark.Completed, video.FindPart(1)!.Mark);
        var episode = video.FindPart(3)!;
        Assert.Equal((40835484789, "古兽冰原1-7", 604d, 312.5), (episode.Cid, episode.Title, episode.Duration, episode.Position));
        Assert.Equal(expected.FindPart(3)!.Segments, episode.Segments);
        Assert.Equal(expected.FindPart(3)!.CompletedAt, episode.CompletedAt);
        Assert.Equal(12, Assert.Single(video.Retired).Watched);
    }

    [Fact]
    public void Save_WritesReadableTextWithOneSpanPerLine()
    {
        new JsonProgressStore(FilePath).Save(Sample());
        var text = File.ReadAllText(FilePath);
        Assert.Contains("[0, 312.5]", text);
        Assert.Contains("古兽冰原 · 全宝箱", text);
        Assert.Contains("\"Mark\": \"Completed\"", text);
        Assert.DoesNotContain("Coverage", text);
        // Of three stored parts only two have spans and one a resume point; the untouched one carries no empty fields.
        Assert.Equal(2, text.Split("\"Segments\"").Length - 1);
        Assert.Equal(1, text.Split("\"Position\"").Length - 1);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Save_FirstOverwriteOfARun_KeepsOneCopyOfTheStartingFile()
    {
        new JsonProgressStore(FilePath).Save(Sample(position: 100));
        var store = new JsonProgressStore(FilePath);
        store.Load();
        store.Save(Sample(position: 200));
        store.Save(Sample(position: 300));
        Assert.Contains("\"Position\": 100", File.ReadAllText(FilePath + ".bak"));
        Assert.Contains("\"Position\": 300", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"Videos\":{}}")]
    [InlineData("{\"Videos\":{\"BV1hjgG6jEa6\":{\"Episodes\":[{\"Segments\":[[1,2,3]]}]}}}")]
    [InlineData("{\"Videos\":{\"BV1hjgG6jEa6\":{\"Episodes\":[{\"Segments\":[[\"bad\",2]]}]}}}")]
    [InlineData("{\"Videos\":{\"BV1hjgG6jEa6\":{\"Episodes\":[{\"Segments\":[[null,2]]}]}}}")]
    [InlineData("{\"Videos\":{\"BV1hjgG6jEa6\":{\"Episodes\":[{\"Segments\":[[{},2]]}]}}}")]
    public void Load_UnreadableFile_StartsEmptyAndKeepsACopyBeforeAnySave(string contents)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, contents);
        var store = new JsonProgressStore(FilePath);
        var loaded = store.Load();
        Assert.Contains("读取失败", loaded.Warning);
        Assert.Empty(loaded.Document.Videos);
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Unreadable())));
        store.Save(Sample());
        Assert.Single(new JsonProgressStore(FilePath).Load().Document.Videos);
        // The unreadable file already has its own copy; it must not replace the regular one.
        Assert.False(File.Exists(FilePath + ".bak"));
    }

    [Fact]
    public void Load_RepeatedlyUnreadable_KeepsOnlyTheNewestCopies()
    {
        Directory.CreateDirectory(_folder);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            File.WriteAllText(FilePath, "broken " + attempt);
            new JsonProgressStore(FilePath).Load();
            Thread.Sleep(5);
        }
        var copies = Unreadable();
        Assert.Equal(3, copies.Length);
        Assert.Contains(copies, copy => File.ReadAllText(copy) == "broken 5");
        Assert.DoesNotContain(copies, copy => File.ReadAllText(copy) == "broken 0");
    }

    [Fact]
    public void Load_HandEditedFile_IsRepairedRatherThanRejected()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """
            { "Videos": { "BV1hjgG6jEa6": { "Episodes": [
              { "Cid": 7, "Part": 2, "Duration": 100, "Segments": [[50, 80], [0, 60], [90, 500]], "Position": 999, "Mark": "NotCompleted" }
            ] } } }
            """);
        var loaded = new JsonProgressStore(FilePath).Load();
        Assert.Null(loaded.Warning);
        var episode = loaded.Document.Videos["BV1hjgG6jEa6"].FindPart(2)!;
        Assert.Equal([new WatchSegment(0, 80), new WatchSegment(90, 100)], episode.Segments);
        Assert.Equal((100d, ProgressMark.NotCompleted, ""), (episode.Position, episode.Mark, episode.Title));
    }

    [Fact]
    public void Save_OriginalUnreadableAndUncopyable_RefusesToOverwrite()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "precious but locked");
        var store = new JsonProgressStore(FilePath);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("不会覆盖", store.Load().Warning);
        Assert.Throws<IOException>(() => store.Save(Sample()));
        Assert.Equal("precious but locked", File.ReadAllText(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
