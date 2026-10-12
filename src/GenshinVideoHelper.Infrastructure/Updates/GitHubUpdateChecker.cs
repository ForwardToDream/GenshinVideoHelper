using System.Net;
using System.Text.Json;
using GenshinVideoHelper.Core.Contracts;

namespace GenshinVideoHelper.Infrastructure.Updates;

public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    public static Uri ReleasesPage { get; } = new("https://github.com/ForwardToDream/GenshinVideoHelper/releases");
    private static readonly Uri Endpoint = new("https://api.github.com/repos/ForwardToDream/GenshinVideoHelper/releases/latest");
    private readonly HttpClient _http;

    public GitHubUpdateChecker(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(6);
        _http.MaxResponseContentBufferSize = 256 * 1024;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GenshinVideoHelper-update-check");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken token = default)
    {
        using var reply = await _http.GetAsync(Endpoint, token).ConfigureAwait(false);
        // A public repository without a published stable release also returns 404.
        if (reply.StatusCode == HttpStatusCode.NotFound) return null;
        reply.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString();
        var number = tag?.StartsWith('v') == true ? tag[1..] : tag;
        if (!Version.TryParse(number, out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("Release 标签必须为 v主版本.次版本.修订号。");
        var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        if (version <= current) return null;
        // Construct the trusted repository URL rather than opening arbitrary metadata links.
        return new(version, new Uri(ReleasesPage.AbsoluteUri + "/tag/" + Uri.EscapeDataString(tag!)));
    }

    public void Dispose() => _http.Dispose();
}
