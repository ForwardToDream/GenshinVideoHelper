using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Library;

public sealed record LibraryVideo(int Number, string Title, string Bvid)
{
    [JsonIgnore]
    public string Url => new VideoIdentity(Bvid, 1).Url;
    [JsonIgnore]
    public string DisplayText => $"{Number:00} · {Title}";
}

public sealed record VideoLibrary(string Id, string Name, string CreatorName, string CreatorUrl, string Description, IReadOnlyList<LibraryVideo> Videos)
{
    [JsonIgnore]
    public string DisplayName => $"{CreatorName} · {Name}";

    public VideoLibrary Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Regex.IsMatch(Id, "^[a-z0-9-]+\\z") ||
            string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(CreatorName) || Videos is not { Count: > 0 })
            throw new ArgumentException("请填写库编号、名称、UP 主昵称，并添加至少一个视频。");
        if (!Uri.TryCreate(CreatorUrl, UriKind.Absolute, out var creator) || creator.Scheme != Uri.UriSchemeHttps ||
            creator.Host != "space.bilibili.com" || creator.UserInfo.Length > 0 || !creator.IsDefaultPort || !Regex.IsMatch(creator.AbsolutePath, "^/[0-9]+/?$"))
            throw new ArgumentException("请填写 UP 主 UID 或有效的 B 站个人空间链接。");
        var numbers = new HashSet<int>();
        var bvids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var video in Videos)
        {
            if (video is null || video.Number <= 0 || string.IsNullOrWhiteSpace(video.Title) ||
                !VideoIdentity.TryParse(video.Bvid, out var identity) || identity!.Bvid != video.Bvid)
                throw new ArgumentException("每个视频需要正整数序号、标题和有效的 B 站视频链接 / BV 号。");
            if (!numbers.Add(video.Number) || !bvids.Add(video.Bvid))
                throw new ArgumentException("视频序号和 BV 号不能重复。");
        }
        return this with
        {
            Name = Name.Trim(), CreatorName = CreatorName.Trim(), CreatorUrl = $"https://space.bilibili.com{creator.AbsolutePath.TrimEnd('/')}/",
            Description = Description?.Trim() ?? "",
            Videos = Array.AsReadOnly(Videos.OrderBy(video => video.Number).Select(video => video with { Title = video.Title.Trim() }).ToArray())
        };
    }
}

public interface IVideoLibraryStore
{
    VideoLibraryCatalog Load();
    void Save(VideoLibrary library, bool overwrite = false);
}

public sealed record VideoLibraryCatalog(IReadOnlyList<VideoLibrary> Libraries, IReadOnlyList<string> Errors)
{
    public const string DefaultLibraryId = "hanqing-zero-start";
}
