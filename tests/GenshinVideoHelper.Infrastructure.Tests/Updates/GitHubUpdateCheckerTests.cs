using System.Net;
using System.Text.Json;
using GenshinVideoHelper.Infrastructure.Updates;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Updates;

public sealed class GitHubUpdateCheckerTests
{
    [Theory]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("v0.1.1", "0.1.0.0", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("v0.10.0", "0.9.0", true)]
    [InlineData("v2.0.0", "1.99.99", true)]
    [InlineData("0.1.1", "0.1.0", true)]
    public async Task Check_ComparesNumericVersions(string tag, string current, bool newer)
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler(Release(tag)));
        var result = await checker.CheckAsync(Version.Parse(current), TestContext.Current.CancellationToken);
        Assert.Equal(newer, result is not null);
        if (result is not null)
        {
            Assert.Equal(Version.Parse(tag.TrimStart('v')), result.Version);
            Assert.Equal($"https://github.com/ForwardToDream/GenshinVideoHelper/releases/tag/{tag}", result.Page.AbsoluteUri);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Check_IgnoresDraftsAndPrereleases(bool draft, bool prerelease)
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler(Release("v9.0.0", draft, prerelease)));
        Assert.Null(await checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Check_NoPublishedRelease_IsNotAnError()
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler("{}", HttpStatusCode.NotFound));
        Assert.Null(await checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Check_NetworkFailure_IsNotMistakenForLatestVersion(HttpStatusCode status)
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler("{}", status));
        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-beta.1")]
    [InlineData("latest")]
    public async Task Check_InvalidStableTag_IsReported(string tag)
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler(Release(tag)));
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Check_MalformedResponse_IsReported()
    {
        using var checker = new GitHubUpdateChecker(new ReplyHandler("not json"));
        await Assert.ThrowsAnyAsync<JsonException>(() => checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Check_OnlyReadsMetadataWithoutCredentialsOrDownloads()
    {
        var handler = new ReplyHandler(Release("v0.2.0"));
        using var checker = new GitHubUpdateChecker(handler);
        await checker.CheckAsync(new(0, 1, 0), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.github.com/repos/ForwardToDream/GenshinVideoHelper/releases/latest", handler.Url);
        Assert.True(handler.HasUserAgent);
        Assert.False(handler.HasCredentials);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task Check_ExitCancellation_StopsPendingRequest()
    {
        var handler = new WaitingHandler();
        using var checker = new GitHubUpdateChecker(handler);
        using var lifetime = new CancellationTokenSource();
        var pending = checker.CheckAsync(new(0, 1, 0), lifetime.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(handler.Cancelled);
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false) =>
        JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, html_url = "https://example.com/ignored", assets = new[] { new { browser_download_url = "https://example.com/never-requested" } } });

    private sealed class ReplyHandler(string contents, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool HasUserAgent { get; private set; }
        public bool HasCredentials { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Method = request.Method;
            HasUserAgent = request.Headers.UserAgent.Count > 0;
            HasCredentials = request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(contents) });
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(HttpStatusCode.OK);
        }
    }
}
