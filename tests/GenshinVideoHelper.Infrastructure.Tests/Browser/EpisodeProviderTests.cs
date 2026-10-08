using System.Net;
using System.Text.Json;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Tests.Support;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Browser;

public sealed class EpisodeProviderTests
{
    [Fact]
    public void Parse_MultipleParts_PreservesLongCidAndOrdering()
    {
        using var data = JsonDocument.Parse(MetadataFixture.EpisodeFixture);
        var info = BilibiliEpisodeService.ParseVideoData(data.RootElement, new("BV1hjgG6jEa6", 3));
        Assert.Equal(4, info.Episodes.Count);
        Assert.Equal(40835484789L, info.Episodes[2].Cid);
        Assert.Equal(604, info.Episodes[2].Duration);
        Assert.Equal(3, info.CurrentPart);
    }

    [Fact]
    public void Parse_MissingPart_ReportsCorrectionRequired()
    {
        using var data = JsonDocument.Parse(MetadataFixture.EpisodeFixture);
        Assert.Throws<InvalidOperationException>(() => BilibiliEpisodeService.ParseVideoData(data.RootElement, new("BV1hjgG6jEa6", 99)));
    }

    [Fact]
    public async Task ReadApi_ValidMetadata_UsesRequestedBvidAndPart()
    {
        var handler = new FixtureHandler("{\"code\":0,\"data\":" + MetadataFixture.EpisodeFixture + "}");
        using var provider = new BilibiliEpisodeService(handler);
        var info = await provider.ReadFromApiAsync(new("BV1hjgG6jEa6", 3), TestContext.Current.CancellationToken);
        Assert.Equal(3, info.CurrentPart);
        Assert.Contains("bvid=BV1hjgG6jEa6", handler.Url);
    }

    [Fact]
    public async Task ReadApi_ApiFailure_ReportsFailure()
    {
        using var provider = new BilibiliEpisodeService(new FixtureHandler("{\"code\":-404}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadFromApiAsync(new("BV1hjgG6jEa6", 1), TestContext.Current.CancellationToken));
    }

    private sealed class FixtureHandler(string contents) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(contents) });
        }
    }
}
