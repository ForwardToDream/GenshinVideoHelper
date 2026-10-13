using System.Text.Json;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Infrastructure.Library;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Library;

public sealed class JsonVideoLibraryStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
    private string LibraryDirectory => Path.Combine(_folder, ".gvh", "libraries");
    private string PathFor(string id) => Path.Combine(LibraryDirectory, id + ".json");
    private static VideoLibrary Custom(string id = "custom") =>
        new(id, "我的库", "测试 UP", "https://space.bilibili.com/123/", "自己的路线", [new(1, "地图", "BV1hjgG6jEa6")]);

    [Fact]
    public void Load_FirstRun_GeneratesDefaultsWithOnlyEditableSchema()
    {
        var catalog = new JsonVideoLibraryStore(LibraryDirectory).Load();
        Assert.Empty(catalog.Errors);
        Assert.Equal(4, catalog.Libraries.Count);
        Assert.Equal(4, Directory.GetFiles(LibraryDirectory, "*.json").Length);
        foreach (var library in catalog.Libraries)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(PathFor(library.Id)));
            Assert.Equal(new[] { "Id", "Name", "CreatorName", "CreatorUrl", "Description", "Videos" },
                json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(new[] { "Number", "Title", "Bvid" },
                json.RootElement.GetProperty("Videos")[0].EnumerateObject().Select(property => property.Name));
        }
    }

    [Fact]
    public void Load_AfterEditingAndDeletingDefaults_UsesDiskAndNeverRestoresEmbeddedCopies()
    {
        var store = new JsonVideoLibraryStore(LibraryDirectory);
        var defaults = store.Load().Libraries;
        var edited = defaults[0] with { Name = "改过的内置库", Videos = [defaults[0].Videos[0] with { Title = "改过的标题" }] };
        // External JSON edits use the same live source as the editor.
        File.WriteAllText(PathFor(edited.Id), JsonSerializer.Serialize(edited));
        File.Delete(PathFor(defaults[1].Id));
        var catalog = new JsonVideoLibraryStore(LibraryDirectory).Load();
        Assert.Empty(catalog.Errors);
        Assert.Equal(3, catalog.Libraries.Count);
        Assert.DoesNotContain(catalog.Libraries, library => library.Id == defaults[1].Id);
        var loaded = Assert.Single(catalog.Libraries, library => library.Id == edited.Id);
        Assert.Equal(edited.Name, loaded.Name);
        Assert.Equal(edited.Videos.Single(), loaded.Videos.Single());
    }

    [Fact]
    public void Save_NewAndEditedLibrary_RoundTripsSortedVideosAndKeepsPreviousBackup()
    {
        var store = new JsonVideoLibraryStore(LibraryDirectory);
        store.Load();
        store.Save(Custom());
        var before = File.ReadAllText(PathFor("custom"));
        var edited = Custom() with { Name = "新名称", Videos = [new(3, "后一个", "BV1E9duBYEBV"), new(1, "前一个", "BV1hjgG6jEa6")] };
        store.Save(edited, overwrite: true);
        Assert.Equal(before, File.ReadAllText(PathFor("custom") + ".bak"));
        var loaded = Assert.Single(new JsonVideoLibraryStore(LibraryDirectory).Load().Libraries, library => library.Id == "custom");
        Assert.Equal("新名称", loaded.Name);
        Assert.Equal(new[] { 1, 3 }, loaded.Videos.Select(video => video.Number));
        Assert.Empty(Directory.GetFiles(LibraryDirectory, "*.tmp-*"));
    }

    [Fact]
    public void Save_DuplicateIdOrInvalidDraft_DoesNotOverwriteValidLibrary()
    {
        var store = new JsonVideoLibraryStore(LibraryDirectory);
        store.Load(); store.Save(Custom());
        var before = File.ReadAllText(PathFor("custom"));
        Assert.Throws<IOException>(() => store.Save(Custom() with { Name = "不能覆盖" }));
        foreach (var invalid in new[]
        {
            Custom("../outside"), Custom("bad\n"), Custom() with { Videos = [] },
            Custom() with { CreatorUrl = "https://space.bilibili.com.evil.test/123/" },
            Custom() with { Videos = [new(1, "地图 A", "BV1hjgG6jEa6"), new(1, "地图 B", "BV1E9duBYEBV")] },
            Custom() with { Videos = [new(1, "地图 A", "BV1hjgG6jEa6"), new(2, "地图 B", "BV1hjgG6jEa6")] }
        }) Assert.Throws<ArgumentException>(() => store.Save(invalid, overwrite: true));
        Assert.Equal(before, File.ReadAllText(PathFor("custom")));
        Assert.False(File.Exists(Path.Combine(_folder, ".gvh", "outside.json")));
    }

    [Theory]
    [InlineData("broken JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Load_BadLibrary_IsReportedAndPreservedWhileOtherLibrariesRemainUsable(string contents)
    {
        var store = new JsonVideoLibraryStore(LibraryDirectory);
        store.Load();
        File.WriteAllText(PathFor("broken"), contents);
        var catalog = store.Load();
        Assert.Equal(4, catalog.Libraries.Count);
        Assert.Contains("broken.json", Assert.Single(catalog.Errors));
        Assert.Equal(contents, File.ReadAllText(PathFor("broken")));
    }

    [Fact]
    public void Load_ExistingEmptyDirectory_StaysEmptyWithoutBuiltInFallback()
    {
        Directory.CreateDirectory(LibraryDirectory);
        var catalog = new JsonVideoLibraryStore(LibraryDirectory).Load();
        Assert.Empty(catalog.Libraries);
        Assert.Empty(catalog.Errors);
        Assert.Empty(Directory.GetFiles(LibraryDirectory));
    }

    [Fact]
    public void Load_MismatchedFileName_IsReportedInsteadOfEditingAnotherFile()
    {
        Directory.CreateDirectory(LibraryDirectory);
        File.WriteAllText(PathFor("wrong"), JsonSerializer.Serialize(Custom()));
        var catalog = new JsonVideoLibraryStore(LibraryDirectory).Load();
        Assert.Empty(catalog.Libraries);
        Assert.Contains("文件名", Assert.Single(catalog.Errors));
        Assert.False(File.Exists(PathFor("custom")));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
}
