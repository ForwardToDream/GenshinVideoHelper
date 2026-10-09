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

internal sealed class ChromeFixture : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TestArtifacts.TempDirectory _directory;
        public BrowserPage Page { get; }
        public Uri BrowserSocket { get; }
        public string ProfileDirectory { get; }
        private ChromeFixture(Process process, TestArtifacts.TempDirectory directory, BrowserPage page, Uri browserSocket, string profileDirectory)
        { _process = process; _directory = directory; Page = page; BrowserSocket = browserSocket; ProfileDirectory = profileDirectory; }

        public static async Task<ChromeFixture> StartAsync(string root, bool headed)
        {
            var directory = TestArtifacts.CreateTemp(root, "browser-test");
            var testDirectory = directory.Path;
            var media = Path.Combine(root, "artifacts", "test-media", "sample.mp4");
            if (!File.Exists(media)) { directory.Dispose(); throw new FileNotFoundException("Generate artifacts/test-media/sample.mp4 first."); }
            var fixture = Path.Combine(testDirectory, "fixture.html");
            await File.WriteAllTextAsync(fixture, $"""
                <!doctype html><html><title>GenshinVideoHelper media test</title><body>
                <video muted controls width="640" height="360" src="{new Uri(media).AbsoluteUri}"></video>
                <video muted width="100" height="56" src="{new Uri(media).AbsoluteUri}"></video>
                </body></html>
                """);
            var start = new ProcessStartInfo(@"C:\Program Files\Google\Chrome\Application\chrome.exe")
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--user-data-dir=" + Path.Combine(testDirectory, "profile"));
            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            if (!headed) start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add(new Uri(fixture).AbsoluteUri);
            var process = Process.Start(start)!;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(200);
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(Path.Combine(testDirectory, "profile", "DevToolsActivePort"));
                        var address = "http://127.0.0.1:" + lines[0];
                        using var version = JsonDocument.Parse(await http.GetStringAsync(address + "/json/version"));
                        using var pages = JsonDocument.Parse(await http.GetStringAsync(address + "/json/list"));
                        var page = pages.RootElement.EnumerateArray().FirstOrDefault(item =>
                            item.GetProperty("type").GetString() == "page" && item.GetProperty("url").GetString()!.Contains("fixture.html"));
                        if (page.ValueKind != JsonValueKind.Object) continue;
                        return new ChromeFixture(process, directory, new BrowserPage(page.GetProperty("id").GetString()!,
                            page.GetProperty("title").GetString()!, page.GetProperty("url").GetString()!,
                            page.GetProperty("webSocketDebuggerUrl").GetString()!),
                            new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!), Path.Combine(testDirectory, "profile"));
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TaskCanceledException) { }
                }
                throw new TimeoutException("Test Chrome did not start.");
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); directory.Dispose(); throw; }
        }

        public async Task CloseTabAsync() => await new CdpClient().SendAsync(BrowserSocket, "Target.closeTarget", new { targetId = Page.Id });

        public async Task<Uri> GetCurrentBrowserSocketAsync()
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(ProfileDirectory, "DevToolsActivePort"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var version = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:" + lines[0] + "/json/version"));
            var socket = new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
            Check(socket.AbsolutePath == lines[1].Trim(), "Cleanup socket belongs to isolated test profile");
            return socket;
        }

        public async Task ShutdownBrowserAsync()
        {
            await new CdpClient().SendAsync(await GetCurrentBrowserSocketAsync(), "Browser.close", new { });
            if (!await Task.Run(() => _process.WaitForExit(3000))) _process.Kill(entireProcessTree: true);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var socket = await GetCurrentBrowserSocketAsync();
                var reply = await new CdpClient().SendAsync(socket, "SystemInfo.getProcessInfo", new { });
                var pid = (int)reply.GetProperty("processInfo").EnumerateArray().First(p => p.GetProperty("type").GetString() == "browser").GetProperty("id").GetDouble();
                using var current = Process.GetProcessById(pid);
                await new CdpClient().SendAsync(socket, "Browser.close", new { });
                if (!await Task.Run(() => current.WaitForExit(3000))) current.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or HttpRequestException or TaskCanceledException) { }
            if (!await Task.Run(() => _process.WaitForExit(3000))) _process.Kill(entireProcessTree: true);
            _process.Dispose();
            _directory.Dispose();
        }
    }
