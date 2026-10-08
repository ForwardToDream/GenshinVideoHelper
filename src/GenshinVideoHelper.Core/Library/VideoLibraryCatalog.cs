using System.Text.Json;
using System.Text.RegularExpressions;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Library;

public sealed record LibraryVideo(int Number, string Title, string Bvid)
{
    public string Url => new VideoIdentity(Bvid, 1).Url;
    public string DisplayText => $"{Number:00} · {Title}";
}

public sealed record VideoLibrary(string Id, string Name, string CreatorName, string CreatorUrl, string Description, IReadOnlyList<LibraryVideo> Videos)
{
    public string DisplayName => $"{CreatorName} · {Name}";
}

public sealed record VideoLibraryCatalog(IReadOnlyList<VideoLibrary> Libraries, IReadOnlyList<string> Errors)
{
    public const string DefaultLibraryId = "hanqing-zero-start";
}
