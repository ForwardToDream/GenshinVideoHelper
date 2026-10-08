using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class Fixtures
{
    public const string EpisodeFixture = """
        {"bvid":"BV1hjgG6jEa6","title":"分集测试","pages":[
          {"page":1,"cid":40841121301,"part":"说明","duration":24},
          {"page":2,"cid":40925269960,"part":"任务说明","duration":32},
          {"page":3,"cid":40835484789,"part":"古兽冰原1-7 · 全宝箱、神瞳、任务、成就、观景点、长标题布局验证","duration":604},
          {"page":4,"cid":40868972670,"part":"古兽冰原8-9","duration":963}]}
        """;

    public sealed class FixtureHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    public sealed class PreviewHandler : HttpMessageHandler
    {
        public List<(string Bvid, CancellationToken Token)> Requests { get; } = [];
        public Func<string, CancellationToken, Task<string>>? Reply { get; set; }
        public static string Metadata(string bvid, bool single = false) => single
            ? "{\"code\":0,\"data\":{\"bvid\":\"" + bvid + "\",\"pages\":[{\"page\":1,\"cid\":40841121301,\"part\":\"单集\",\"duration\":3601}]}}"
            : "{\"code\":0,\"data\":" + EpisodeFixture.Replace("BV1hjgG6jEa6", bvid) + "}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bvid = request.RequestUri!.Query.Split("bvid=")[1];
            Requests.Add((bvid, cancellationToken));
            var json = Reply is null ? Metadata(bvid) : await Reply(bvid, cancellationToken);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    public static BilibiliEpisodeService PreviewService() => new(new PreviewHandler());
}
