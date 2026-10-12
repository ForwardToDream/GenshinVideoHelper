using System.Text.Json;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Infrastructure.Library;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Library;
public sealed class VideoLibraryTests
{
    [Theory]
    [InlineData("yingyueyue-no-quests", "你的影月月", "431073645", 19, "BV1cE4m197fY", "BV1BMgj6MEz4")]
    [InlineData("chuanshanjia-with-quests", "提瓦特高级特工穿山甲", "484281260", 22, "BV1b84y167Sn", "BV1hjgG6jEa6")]
    [InlineData("chuanshanjia-no-quests", "提瓦特高级特工穿山甲", "484281260", 21, "BV1td4y1b7Hs", "BV1xKbS6WEuK")]
    public void LoadBuiltIn_AdditionalLibraries_LoadOfflineWithOrderedFirstPartLinks(
        string id, string creator, string uid, int count, string first, string last)
    {
        var catalog = VideoLibraryLoader.LoadBuiltIn();
        Assert.Empty(catalog.Errors);
        var library = Assert.Single(catalog.Libraries, library => library.Id == id);
        Assert.Equal(creator, library.CreatorName);
        Assert.Equal($"https://space.bilibili.com/{uid}/", library.CreatorUrl);
        Assert.Equal(count, library.Videos.Count);
        Assert.Equal(Enumerable.Range(1, count), library.Videos.Select(video => video.Number));
        Assert.Equal(first, library.Videos[0].Bvid);
        Assert.Equal(last, library.Videos[^1].Bvid);
        Assert.All(library.Videos, video => Assert.Equal(new VideoIdentity(video.Bvid, 1), VideoIdentity.Parse(video.Url)));
    }

    [Fact]
    public void LoadBuiltIn_ValidLibraries_PreservesSuppliedCatalog()
    {
        var catalog = VideoLibraryLoader.LoadBuiltIn();
        Assert.True(catalog.Errors.Count == 0 && catalog.Libraries.Count > 0, "Built-in library resources are valid");
        var library = catalog.Libraries.Single(library => library.Id == VideoLibraryCatalog.DefaultLibraryId);
        Assert.True(library.CreatorName == "汉卿导航" && library.CreatorUrl == "https://space.bilibili.com/3546597013064142/" && library.Name == "汉卿导航·从零开始的一条龙", "Library name and creator match supplied list");
        var expected = new[] { "BV1MXfEY4EQ2", "BV1zTjAzAETy", "BV1y2gfzHE2M", "BV1uGuBzwE3n", "BV1STtJz4EMc", "BV1fKntzyEjv", "BV1AvWdzgEG8", "BV1ExCjBQEcX", "BV1krUDBTEyx", "BV1SkqaBeEHf", "BV1KUBLBJEsq", "BV18Li3BBEPU", "BV1yJi8BmEk5", "BV1HU62BqEfW", "BV1hRznBCEfH", "BV18L6PBFEsE", "BV15aFQzpEhW", "BV1E9duBYEBV", "BV1KL5Y6GEFa", "BV1TT7S6YETR", "BV1CATe69EKH", "BV1hCuC6NE47" };
        Assert.True(library.Videos.Select(video => video.Bvid).SequenceEqual(expected) && library.Videos.Select(video => video.Number).SequenceEqual(Enumerable.Range(1, 22)), "All 22 supplied BV IDs and map ordering preserved");
        Assert.True(library.Videos.All(video => VideoIdentity.Parse(video.Url) == new VideoIdentity(video.Bvid, 1)), "Preset links start at first part");
        var valid = JsonSerializer.Serialize(library);
        foreach (var invalid in new[] { valid.Replace(library.CreatorUrl, "https://space.bilibili.com.evil.test/123"), valid.Replace(expected[1], expected[0]), valid.Replace(expected[0], "broken-bvid"), "{}", "null" })
        {
            var rejected = false;
            try { VideoLibraryLoader.Parse(invalid); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException) { rejected = true; }
            Assert.True(rejected, "Reject invalid library data");
        }
        Assert.True(VideoLibraryLoader.Parse(valid.Replace(library.Id, "second-library")).Id == "second-library", "Schema supports additional libraries independent of first ID");
        Console.WriteLine("Video libraries: creator, all 22 supplied links/order, offline resources, malformed data and extensible IDs passed.");
    }

}
