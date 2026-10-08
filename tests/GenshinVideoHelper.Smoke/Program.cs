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
using GenshinVideoHelper.Core.Browser;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
            if (args.Contains("--render-ui")) RenderUi(root);
            else if (args.Contains("--browser")) BrowserTestAsync(root, headed: false).GetAwaiter().GetResult();
            else if (args.Contains("--pip")) PipTest(root);
            else if (args.Contains("--bilibili")) BilibiliTestAsync(root).GetAwaiter().GetResult();
            else if (args.Contains("--bilibili-pip")) PipTest(root, example: true);
            else if (args.Contains("--preview")) PreviewUiTest(root);
            else if (args.Contains("--lifecycle")) LifecycleUiTest(root);
            else if (args.Contains("--follow")) FollowUiTest(root);
            else { TestUrls(); TestEpisodes(); TestSettings(root); TestLibraries(); }
            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestUrls()
    {
        Assert(ChromeBrowser.ValidateVideoUrl(" BV1hjgG6jEa6 ").AbsoluteUri ==
               "https://www.bilibili.com/video/BV1hjgG6jEa6", "BV normalization");
        foreach (var bad in new[] { "https://bilibili.com.evil.test/video/a", "http://www.bilibili.com/", "javascript:alert(1)", "https://www.bilibili.com@evil.test/" })
        {
            var rejected = false;
            try { ChromeBrowser.ValidateVideoUrl(bad); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, $"Reject off-site input: {bad}");
        }
        Console.WriteLine("URL validation: BV input, HTTPS and host boundaries passed.");
    }

    private const string EpisodeFixture = """
        {"bvid":"BV1hjgG6jEa6","title":"分集测试","pages":[
          {"page":1,"cid":40841121301,"part":"说明","duration":24},
          {"page":2,"cid":40925269960,"part":"任务说明","duration":32},
          {"page":3,"cid":40835484789,"part":"古兽冰原1-7 · 全宝箱、神瞳、任务、成就、观景点、长标题布局验证","duration":604},
          {"page":4,"cid":40868972670,"part":"古兽冰原8-9","duration":963}]}
        """;

    private sealed class FixtureHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class PreviewHandler : HttpMessageHandler
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

    private static BilibiliEpisodeService PreviewService() => new(new PreviewHandler());

    private static void PreviewUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => PreviewUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    private static async Task PreviewUiTestAsync(string root)
    {
        var library = VideoLibraryCatalog.LoadBuiltIn().Libraries.Single();
        var handler = new PreviewHandler();
        var settings = new AppSettings { HotkeysEnabled = false };
        var window = new GenshinVideoHelper.App.MainWindow(settings, Path.Combine(root, "artifacts", "preview-test-profile"), false, episodeService: new(handler));
        var url = (TextBox)window.FindName("UrlInput");
        var maps = (ComboBox)window.FindName("GuideVideoSelector");
        var parts = (ComboBox)window.FindName("EpisodeSelector");
        var retry = (Button)window.FindName("EpisodeRetryButton");
        var episodeStatus = (TextBlock)window.FindName("EpisodeStatusText");
        try
        {
            Assert(url.Text == library.Videos[0].Url && maps.SelectedIndex == 0 && maps.Items.Count == 22, "Startup selects first real map without synthetic input item");
            await Until(() => parts.Items.Count == 4, "Startup automatically reads episodes before Chrome opens");
            Assert(episodeStatus.Text == "" && episodeStatus.Visibility == Visibility.Collapsed, "Ready episodes do not show a helper line or reserve its space");
            Assert(handler.Requests.Count == 1 && GetField<BrowserPage>(window, "_page") is null && GetField<FollowSession>(window, "_follow")!.Current is null, "Metadata preview does not create a browser session");
            Capture(window, root, "ui-follow-preloaded.png", 1020, 730);
            Capture(window, root, "ui-follow-preloaded-compact.png", 860, 600);
            Assert(!((Button)window.FindName("PreviousEpisodeButton")).IsEnabled, "First preview part boundary");
            parts.SelectedIndex = 3;
            Assert(url.Text.EndsWith("?p=4") && !((Button)window.FindName("NextEpisodeButton")).IsEnabled && handler.Requests.Count == 1, "Preselect P4 only edits link and reuses metadata");
            Assert(settings.VideoUrl == "", "Preview does not persist an unfollowed URL");
            parts.SelectedIndex = 3;
            Assert(handler.Requests.Count == 1, "Repeated selection does not reload");
            url.Clear();
            Assert(maps.SelectedIndex == -1 && parts.Items.Count == 0 && !parts.IsEnabled, "Clearing URL enables manual entry without a mode choice");
            url.Text = "BV1hjgG6jEa6";
            url.Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=episodes&p=3";
            await Until(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == "BV1hjgG6jEa6", "Typed link debounces into one read");
            Assert(handler.Requests.Count == 2 && ((EpisodeInfo)parts.SelectedItem).Number == 3 && maps.SelectedIndex == -1, "Manual P3 and tracking parameters resolved before following");
            url.Text = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=99";
            Assert(parts.Items.Count == 4 && parts.SelectedIndex == -1 && episodeStatus.Text.Contains("没有 P99"), "Invalid P preserves list for correction");
            parts.SelectedIndex = 0;
            Assert(url.Text.EndsWith("?p=1"), "Invalid P can be corrected using preview selector");

            var held = new TaskCompletionSource<string>();
            handler.Reply = (bvid, token) => bvid == library.Videos[1].Bvid ? held.Task : Task.FromResult(PreviewHandler.Metadata(bvid, single: true));
            maps.SelectedIndex = 1;
            await Until(() => handler.Requests.Count == 3, "Selected map loads without Start");
            var operations = GetField<SemaphoreSlim>(window, "_operations")!;
            Assert(operations.Wait(0), "Pending preview leaves operation lock available");
            operations.Release();
            maps.SelectedIndex = 2;
            await Until(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == library.Videos[2].Bvid, "New selection overtakes slow metadata request");
            Assert(handler.Requests[2].Token.IsCancellationRequested && parts.Items.Count == 1 && !((Button)window.FindName("PreviousEpisodeButton")).IsEnabled && !((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Old request canceled and single episode boundaries disabled");
            held.SetResult(PreviewHandler.Metadata(library.Videos[1].Bvid));
            await Task.Delay(100);
            Assert(GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == library.Videos[2].Bvid && retry.Visibility == Visibility.Collapsed, "Late old result does not replace newer episodes or show failure");

            handler.Reply = (_, _) => Task.FromResult("{\"code\":-404}");
            maps.SelectedIndex = 3;
            await Until(() => retry.Visibility == Visibility.Visible, "Metadata failure exposes retry before following");
            Assert(GetField<BrowserPage>(window, "_page") is null && episodeStatus.Text.Contains("可重试") && episodeStatus.Visibility == Visibility.Visible, "Failure still shows feedback without starting browser or blocking manual start");
            Capture(window, root, "ui-follow-preview-error-compact.png", 860, 600);
            handler.Reply = null;
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => parts.Items.Count == 4 && retry.Visibility == Visibility.Collapsed, "Retry recovers episode preview");

            using var data = JsonDocument.Parse(EpisodeFixture);
            var activeInfo = BilibiliEpisodeService.ParseVideoData(data.RootElement, new("BV1hjgG6jEa6", 3));
            SetField(window, "_info", activeInfo);
            var follow = GetField<FollowSession>(window, "_follow")!;
            var activeRequest = follow.Begin("active-fixture", new("BV1hjgG6jEa6", 3));
            follow.Complete(activeRequest);
            SetField(window, "_page", new BrowserPage("active-fixture", "Active", activeRequest.Identity.Url, "ws://127.0.0.1:1/unused"));
            maps.SelectedIndex = 4;
            await Until(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == library.Videos[4].Bvid, "Prepare other map while a session exists");
            parts.SelectedIndex = 2;
            Assert(follow.Current == activeRequest && GetField<BilibiliVideoInfo>(window, "_info") == activeInfo && GetField<BrowserPage>(window, "_page")!.Id == "active-fixture", "Candidate map/P preview does not change active session or active episodes");
            handler.Reply = (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith<string>(_ => throw new OperationCanceledException(token));
            maps.SelectedIndex = 5;
            await Until(() => handler.Requests.Last().Bvid == library.Videos[5].Bvid, "Last pending request started");
            window.Close();
            Assert(handler.Requests.Last().Token.IsCancellationRequested, "Closing window cancels preview");
        }
        finally { window.Close(); }
        var restored = new GenshinVideoHelper.App.MainWindow(new AppSettings { HotkeysEnabled = false, VideoUrl = library.Videos[8].Url.Replace("?p=1", "?p=3") }, Path.Combine(root, "artifacts", "preview-test-profile"), false, episodeService: PreviewService());
        try
        {
            await Until(() => ((ComboBox)restored.FindName("EpisodeSelector")).Items.Count == 4, "Restore preloads saved in-library part");
            Assert(((ComboBox)restored.FindName("GuideVideoSelector")).SelectedIndex == 8 && ((EpisodeInfo)((ComboBox)restored.FindName("EpisodeSelector")).SelectedItem).Number == 3, "Restore keeps saved map and P3");
        }
        finally { restored.Close(); }
        Console.WriteLine("Preview: startup/default map, manual/debounced P, cache, single/multiple parts, invalid P correction, retry, cancellation, stale results and active-session isolation passed.");

        static async Task Until(Func<bool> condition, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert(condition(), label);
        }
    }

    private static void TestEpisodes()
    {
        var identity = VideoIdentity.Parse("https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=333.788.videopod.episodes&p=3");
        Assert(identity.Part == 3 && identity.Url.EndsWith("?p=3"), "Part query excludes source tracking");
        Assert(VideoIdentity.Parse("BV1hjgG6jEa6").Part == 1, "Default part one");
        foreach (var bad in new[] { "?p=0", "?p=-1", "?p=no", "?p=" })
            Assert(!VideoIdentity.TryParse("https://www.bilibili.com/video/BV1hjgG6jEa6/" + bad, out _), "Invalid part " + bad);
        using var json = JsonDocument.Parse(EpisodeFixture);
        var info = BilibiliEpisodeService.ParseVideoData(json.RootElement, identity);
        Assert(info.Episodes.Count == 4 && info.Episodes[2].Cid == 40835484789L, "Episode ordering and 64-bit CID");
        Assert(info.Episodes[2].DisplayText.Contains("10:04"), "Episode duration");
        var rejected = false;
        try { BilibiliEpisodeService.ParseVideoData(json.RootElement, identity with { Part = 99 }); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Out-of-range part");
        using var single = JsonDocument.Parse("""{"bvid":"BV1hjgG6jEa6","pages":[{"page":1,"cid":40841121301,"part":"单集","duration":3601}]}""");
        Assert(BilibiliEpisodeService.ParseVideoData(single.RootElement, identity with { Part = 1 }).Episodes.Single().DisplayText.EndsWith("1:00:01"), "Single part and hour duration");
        using var fallback = new BilibiliEpisodeService(new FixtureHandler("{\"code\":0,\"data\":" + EpisodeFixture + "}"));
        Assert(fallback.ReadFromApiAsync(identity).GetAwaiter().GetResult().CurrentPart == 3, "API fallback");
        using var failed = new BilibiliEpisodeService(new FixtureHandler("{\"code\":-404}"));
        rejected = false;
        try { failed.ReadFromApiAsync(identity).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Metadata failure is reported");
        using var follow = new FollowSession();
        var first = follow.Begin("tab-a", identity);
        var second = follow.Begin("tab-a", identity with { Part = 4 });
        Assert(first.Token.IsCancellationRequested && !follow.IsCurrent(first), "New part cancels stale request");
        follow.Complete(first);
        Assert(follow.AutomaticPending, "Stale completion cannot clear new intent");
        follow.Complete(second);
        Assert(!follow.AutomaticPending, "Completed intent stays completed during polling");
        follow.Suppress();
        Assert(!follow.AutomaticPending, "Manual close remains suppressed");
        var third = follow.Begin("tab-b", identity);
        Assert(follow.AutomaticPending && follow.Attach(third, "tab-c").TargetId == "tab-c", "New video resumes auto follow and attaches exact target");
        Console.WriteLine("Episodes: URL, single/multiple parts, long CID, API fallback/failure and canceled follow intent passed.");
    }

    private static void TestSettings(string root)
    {
        var folder = Path.Combine(root, "artifacts", "settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "GenshinVideoHelper.settings.json");
        var settings = AppSettings.Load(path);
        Assert(File.Exists(path) && settings.VideoUrl == "", "First load creates fresh settings at the current product path");
        Assert(Path.GetFileName(AppSettings.DataDirectory) == "GenshinVideoHelper" && Path.GetFileName(AppSettings.SettingsPath) == "GenshinVideoHelper.settings.json", "Profile and configuration paths use current product name");
        settings.VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3";
        settings.SeekSeconds = 10;
        settings.PipWidth = 480;
        settings.HotkeysEnabled = false;
        settings.Save();
        settings = AppSettings.Load(path);
        Assert(settings.VideoUrl.EndsWith("?p=3") && settings.SeekSeconds == 10 && settings.PipWidth == 480 && !settings.HotkeysEnabled, "Root settings reload saved preferences");
        settings.Hotkeys[HotkeyAction.NextEpisode] = " ctrl + shift + f8 ";
        settings.Hotkeys = HotkeyBindings.Validate(settings.Hotkeys);
        settings.VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=4";
        settings.SelectedVideoLibraryId = null;
        settings.Save();
        var restored = AppSettings.Load(path);
        Assert(restored.Hotkeys[HotkeyAction.NextEpisode] == "Ctrl+Shift+F8" && restored.VideoUrl.EndsWith("?p=4"), "Round-trip custom keys and last followed part");
        Assert(restored.SelectedVideoLibraryId is null, "Manual library choice survives restart");
        Assert(File.ReadAllText(path).Contains("NextEpisode") && File.ReadAllText(path).Contains("Ctrl+Shift+F8"), "Readable action names and literal plus signs in JSON");
        Assert(HotkeyGesture.TryParse("~", out var tilde, allowUnmodified: true) && tilde.Key == 0xC0 && tilde.Modifiers == 0, "Physical tilde key does not require Shift");
        Assert(HotkeyGesture.TryParse("`", out var backtick, allowUnmodified: true) && backtick == tilde, "Backtick alias normalizes to tilde");
        Assert(HotkeyGesture.TryParse("Ctrl+~", out var chord, allowUnmodified: true) && chord.Modifiers == 2, "Modified tilde gesture parses");
        settings.Hotkeys[HotkeyAction.ReversePipVisibility] = "F8";
        settings.Save();
        Assert(AppSettings.Load(path).Hotkeys[HotkeyAction.ReversePipVisibility] == "F8", "Custom single hold key persists");
        var reverseDuplicate = HotkeyBindings.Defaults();
        reverseDuplicate[HotkeyAction.ReversePipVisibility] = "Alt+2";
        try { HotkeyBindings.Validate(reverseDuplicate); throw new Exception("FAIL: reversal duplicate accepted"); }
        catch (ArgumentException) { }
        foreach (var bad in new[] { "Alt", "1", "Alt+Alt+1", "Alt+", "Ctrl+Potato", "Alt+F25" })
            Assert(!HotkeyGesture.TryParse(bad, out _), "Reject invalid gesture " + bad);
        Assert(HotkeyGesture.TryParse("Alt+Left", out var arrow) && arrow.Modifiers == 1 && arrow.Key == 0x25, "Left-arrow native registration values");
        var duplicates = HotkeyBindings.Defaults();
        duplicates[HotkeyAction.NextEpisode] = "alt+2";
        var rejected = false;
        try { HotkeyBindings.Validate(duplicates); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Reject duplicate normalized gestures");
        File.WriteAllText(path, "broken JSON");
        var damaged = AppSettings.Load(path);
        Assert(damaged.LoadWarning is not null && File.ReadAllText(path) == "broken JSON", "Malformed existing file remains intact and reports warning");
        damaged.Save();
        Assert(Directory.GetFiles(folder, "*.bak").Length == 1, "Saving fallback preserves original invalid configuration in backup");
        var fresh = AppSettings.Load(Path.Combine(folder, "fresh.json"));
        Assert(fresh.Hotkeys.Count == 9 && fresh.Hotkeys[HotkeyAction.ReversePipVisibility] == "~", "New file has nine bindings including default hold reversal");
        Assert(fresh.VideoUrl == "" && !File.ReadAllText(Path.Combine(folder, "fresh.json")).Contains("DefaultVideoUrl"), "Fresh configuration has no preset video URL or obsolete default field");
        File.WriteAllText(Path.Combine(folder, "old-default.json"), """{"DefaultVideoUrl":"https://www.bilibili.com/video/BV1MXfEY4EQ2/","VideoUrl":"https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3","SeekSeconds":10}""");
        var oldDefault = AppSettings.Load(Path.Combine(folder, "old-default.json"));
        Assert(oldDefault.VideoUrl.EndsWith("?p=3") && oldDefault.SeekSeconds == 10, "Obsolete default URL ignored while last part and preferences survive");
        oldDefault.Save();
        Assert(!File.ReadAllText(Path.Combine(folder, "old-default.json")).Contains("DefaultVideoUrl"), "Saving removes obsolete default URL field");
        Assert(fresh.SelectedVideoLibraryId == VideoLibraryCatalog.DefaultLibraryId, "Old and fresh settings enable first built-in library by default");
        Assert(AppSettings.FindApplicationRoot(Path.Combine(root, "artifacts", "GenshinVideoHelper")) == root &&
               AppSettings.FindApplicationRoot(AppContext.BaseDirectory) == root, "Source and published executables locate root independent of working directory");
        Console.WriteLine("Configuration: root resolution, current product paths, creation, round-trip, invalid file backup, modifier parsing and duplicate rejection passed.");
    }

    private static void TestLibraries()
    {
        var catalog = VideoLibraryCatalog.LoadBuiltIn();
        Assert(catalog.Errors.Count == 0 && catalog.Libraries.Count > 0, "Built-in library resources are valid");
        var library = catalog.Libraries.Single(library => library.Id == VideoLibraryCatalog.DefaultLibraryId);
        Assert(library.CreatorName == "汉卿导航" && library.CreatorUrl == "https://space.bilibili.com/3546597013064142/" && library.Name == "每张地图完全从零开始", "Library name and creator match supplied list");
        var expected = new[] { "BV1MXfEY4EQ2", "BV1zTjAzAETy", "BV1y2gfzHE2M", "BV1uGuBzwE3n", "BV1STtJz4EMc", "BV1fKntzyEjv", "BV1AvWdzgEG8", "BV1ExCjBQEcX", "BV1krUDBTEyx", "BV1SkqaBeEHf", "BV1KUBLBJEsq", "BV18Li3BBEPU", "BV1yJi8BmEk5", "BV1HU62BqEfW", "BV1hRznBCEfH", "BV18L6PBFEsE", "BV15aFQzpEhW", "BV1E9duBYEBV", "BV1KL5Y6GEFa", "BV1TT7S6YETR", "BV1CATe69EKH", "BV1hCuC6NE47" };
        Assert(library.Videos.Select(video => video.Bvid).SequenceEqual(expected) && library.Videos.Select(video => video.Number).SequenceEqual(Enumerable.Range(1, 22)), "All 22 supplied BV IDs and map ordering preserved");
        Assert(library.Videos.All(video => VideoIdentity.Parse(video.Url) == new VideoIdentity(video.Bvid, 1)), "Preset links start at first part");
        var valid = JsonSerializer.Serialize(library);
        foreach (var invalid in new[] { valid.Replace(library.CreatorUrl, "https://space.bilibili.com.evil.test/123"), valid.Replace(expected[1], expected[0]), valid.Replace(expected[0], "broken-bvid"), "{}", "null" })
        {
            var rejected = false;
            try { VideoLibraryCatalog.Parse(invalid); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException) { rejected = true; }
            Assert(rejected, "Reject invalid library data");
        }
        Assert(VideoLibraryCatalog.Parse(valid.Replace(library.Id, "second-library")).Id == "second-library", "Schema supports additional libraries independent of first ID");
        Console.WriteLine("Video libraries: creator, all 22 supplied links/order, offline resources, malformed data and extensible IDs passed.");
    }

    private static void TestLibraryUi(GenshinVideoHelper.App.MainWindow window)
    {
        var settings = GetField<AppSettings>(window, "_settings")!;
        var guideSelector = (ComboBox)window.FindName("GuideVideoSelector");
        var url = (TextBox)window.FindName("UrlInput");
        var beforeUrl = url.Text;
        var beforeState = GetField<VideoState>(window, "_state");
        var request = GetField<FollowSession>(window, "_follow")!.Current;
        Assert(window.FindName("LibrarySelector") is null && window.FindName("DefaultUrlInput") is null, "Settings no longer contains library or default URL controls");
        Assert(guideSelector.Items.Count == 22 && guideSelector.SelectedIndex == 0, "Library contains only maps and starts on first video");
        guideSelector.SelectedIndex = 12;
        Assert(VideoIdentity.Parse(url.Text).Bvid == "BV1yJi8BmEk5" && beforeState == GetField<VideoState>(window, "_state") && request == GetField<FollowSession>(window, "_follow")!.Current, "Selecting map fills URL without navigating active video");
        url.Text = "https://www.bilibili.com/video/BV1MXfEY4EQ2/?p=3";
        Assert(guideSelector.SelectedIndex == 0 && url.Text.EndsWith("?p=3"), "Manual link matches library by BV and preserves requested part");
        url.Text = "BV1hjgG6jEa6";
        Assert(guideSelector.SelectedIndex == -1, "Custom video remains supported without a synthetic item");
        Invoke(window, "SelectLibrary", (object)null!);
        Assert(settings.SelectedVideoLibraryId is null && guideSelector.Items.Count == 0 && url.Text == "BV1hjgG6jEa6", "Disabling library keeps manual URL intact");
        Invoke(window, "SelectLibrary", VideoLibraryCatalog.LoadBuiltIn().Libraries.Single());
        Assert(settings.SelectedVideoLibraryId == VideoLibraryCatalog.DefaultLibraryId && guideSelector.Items.Count == 22 && url.Text == VideoLibraryCatalog.LoadBuiltIn().Libraries.Single().Videos[0].Url, "Changing library fills first map without navigating playback");
        url.Text = beforeUrl;
        Assert(beforeState == GetField<VideoState>(window, "_state"), "Library changes preserve playback state");
    }
    private static void TestLibraryPicker(string root, VideoLibrary library, VideoLibrary second)
    {
        var preview = new GenshinVideoHelper.App.LibraryPickerWindow(new VideoLibraryCatalog([library], []), library.Id);
        Capture(preview, root, "ui-library-picker.png", 570, 490);
        Capture(preview, root, "ui-library-picker-compact.png", 460, 380);
        preview.Close();
        second = second with { CreatorName = "测试 UP" };
        var catalog = new VideoLibraryCatalog([library, second], []);
        var picker = new GenshinVideoHelper.App.LibraryPickerWindow(catalog, library.Id);
        var search = (TextBox)picker.FindName("SearchInput");
        var results = (ListBox)picker.FindName("LibraryResults");
        var apply = (Button)picker.FindName("UseLibraryButton");
        Assert(results.Items.Count == 3 && results.SelectedIndex == 1 && apply.IsEnabled, "Picker shows libraries and selects current ID");
        Capture(picker, root, "ui-library-picker-multiple.png", 570, 490);
        Capture(picker, root, "ui-library-picker-multiple-compact.png", 460, 380);
        search.Text = "汉卿";
        Assert(results.Items.Count == 1, "Search by UP name");
        search.Text = "测试 up 长标题";
        Assert(results.Items.Count == 1 && results.SelectedItem is null, "Case-insensitive multiword search finds matching author and library without committing choice");
        results.SelectedIndex = 0;
        Capture(picker, root, "ui-library-picker-long.png", 460, 380);
        search.Text = "沉玉谷";
        Assert(results.Items.Count == 1, "Search by map name inside library");
        search.Text = "bv1tt7s6yetr";
        Assert(results.Items.Count == 1, "Search by BV ID case-insensitively");
        search.Text = "不存在的库-438625";
        Assert(results.Items.Count == 0 && !apply.IsEnabled && ((TextBlock)picker.FindName("EmptyStateText")).Visibility == Visibility.Visible, "No results shows empty state and prevents confirmation");
        Capture(picker, root, "ui-library-picker-empty.png", 570, 490);
        Capture(picker, root, "ui-library-picker-empty-compact.png", 460, 380);
        search.Text = "手动输入";
        Assert(results.Items.Count == 1, "Manual mode can be searched");
        results.SelectedIndex = 0;
        Assert((string)apply.Content == "手动输入", "Manual choice has matching confirmation label");
        Invoke(picker, "ClearSearch_Click", picker, new RoutedEventArgs());
        Assert(results.Items.Count == 3 && search.Text == "", "Clear button restores full list");
        picker.Close();
    }

    private static void TestLibraryPickerFlow(GenshinVideoHelper.App.MainWindow window)
    {
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        var settings = GetField<AppSettings>(window, "_settings")!;
        var beforePage = GetField<BrowserPage>(window, "_page");
        var beforeRequest = GetField<FollowSession>(window, "_follow")!.Current;
        var beforeUrl = ((TextBox)window.FindName("UrlInput")).Text;
        var beforeHeading = ((TextBlock)window.FindName("PageHeading")).Text;
        Exception? error = null;
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try
            {
                Assert(picker.Owner == window, "Change-library button opens owned picker without navigating settings");
                ((TextBox)picker.FindName("SearchInput")).Text = "手动输入";
                ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0;
                ((Button)picker.FindName("UseLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { error = ex; }
            finally { if (picker.IsVisible) picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Assert(settings.SelectedVideoLibraryId is null && ((ComboBox)window.FindName("GuideVideoSelector")).Items.Count == 0, "Confirming picker applies manual mode");
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try { ((TextBox)picker.FindName("SearchInput")).Text = "汉卿"; ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0; }
            catch (Exception ex) { error = ex; }
            finally { picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Assert(settings.SelectedVideoLibraryId is null && GetField<BrowserPage>(window, "_page") == beforePage &&
               GetField<FollowSession>(window, "_follow")!.Current == beforeRequest &&
               ((TextBox)window.FindName("UrlInput")).Text == beforeUrl && ((TextBlock)window.FindName("PageHeading")).Text == beforeHeading, "Cancel preserves selection, current page, session and URL");
        window.Dispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
            try
            {
                ((TextBox)picker.FindName("SearchInput")).Text = "汉卿";
                ((ListBox)picker.FindName("LibraryResults")).SelectedIndex = 0;
                ((Button)picker.FindName("UseLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { error = ex; }
            finally { if (picker.IsVisible) picker.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
        if (error is not null) throw error;
        Assert(settings.SelectedVideoLibraryId == VideoLibraryCatalog.DefaultLibraryId && ((ComboBox)window.FindName("GuideVideoSelector")).Items.Count == 22, "Confirming searched library restores its map list");
    }

    private static void TestBindingEditor(GenshinVideoHelper.App.MainWindow window)
    {
        var rows = ((ItemsControl)window.FindName("HotkeyRows")).ItemsSource.Cast<object>().ToArray();
        var row = rows.First(item => (HotkeyAction)item.GetType().GetProperty("Action")!.GetValue(item)! == HotkeyAction.NextEpisode);
        row.GetType().GetProperty("Keys")!.SetValue(row, "Alt+2");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Assert(((TextBlock)window.FindName("BindingStatus")).Text.Contains("重复"), "Binding editor reports duplicates");
        var settings = GetField<AppSettings>(window, "_settings")!;
        Assert(settings.Hotkeys[HotkeyAction.NextEpisode] == "Alt+Right", "Invalid draft leaves active bindings unchanged");
        row.GetType().GetProperty("Keys")!.SetValue(row, "Ctrl+Shift+F8");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Assert(settings.Hotkeys[HotkeyAction.NextEpisode] == "Ctrl+Shift+F8" && (string)((Button)window.FindName("NextEpisodeButton")).ToolTip == "Ctrl+Shift+F8", "Valid binding saves and updates tooltip without resetting video");
        var reverseRow = rows.Single(item => (HotkeyAction)item.GetType().GetProperty("Action")!.GetValue(item)! == HotkeyAction.ReversePipVisibility);
        reverseRow.GetType().GetProperty("Keys")!.SetValue(reverseRow, "F8");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Assert(settings.Hotkeys[HotkeyAction.ReversePipVisibility] == "F8", "Reversal row saves a single key through normal binding editor");
        Invoke(window, "ResetBindings_Click", window, new RoutedEventArgs());
        Assert(settings.Hotkeys[HotkeyAction.NextEpisode] == "Ctrl+Shift+F8", "Filling defaults does not apply until saved");
        Invoke(window, "SaveBindings_Click", window, new RoutedEventArgs());
        Assert(settings.Hotkeys[HotkeyAction.NextEpisode] == "Alt+Right" && settings.Hotkeys[HotkeyAction.ReversePipVisibility] == "~", "Default reset is applied on save including hold key");
        ((TextBlock)window.FindName("BindingStatus")).Text = "下一分集：Alt+2 与另一项绑定重复。请修改后再保存。";
    }

    private static void RenderUi(string root)
    {
        var app = CreateTestApplication();
        var window = new GenshinVideoHelper.App.MainWindow(new AppSettings { HotkeysEnabled = false }, Path.Combine(root, "artifacts", "ui-test-profile"), false, episodeService: PreviewService());
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        Assert(window.FindName("NavConnection") is null && window.FindName("NavPip") is null &&
               ((Grid)window.FindName("PagesHost")).Children.Count == 3, "Start, hotkeys and settings pages");
        Assert(window.FindName("OtherPagesExpander") is null && window.FindName("PageSelector") is null, "Other-video-page section removed");
        foreach (var (key, title) in new[] { ("Follow", "启动"), ("Hotkeys", "快捷键"), ("Settings", "设置") })
        {
            ((RadioButton)window.FindName("Nav" + key)).IsChecked = true;
            Assert(((TextBlock)window.FindName("PageHeading")).Text == title, "Navigate " + key);
            Capture(window, root, "ui-" + key.ToLowerInvariant() + ".png", 1020, 730);
            Capture(window, root, "ui-" + key.ToLowerInvariant() + "-compact.png", 860, 600);
        }
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        ((Button)window.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(((TextBlock)window.FindName("StatusText")).Text.Contains("请先开始跟随"), "Playback error feedback");
        using var json = JsonDocument.Parse(EpisodeFixture);
        var info = BilibiliEpisodeService.ParseVideoData(json.RootElement, new("BV1hjgG6jEa6", 3));
        SetField(window, "_info", info); SetField(window, "_selectedInfo", info); SetField(window, "_selectedIdentity", new VideoIdentity(info.Bvid, 3));
        Invoke(window, "UpdateEpisodeControls");
        Assert(((Button)window.FindName("PreviousEpisodeButton")).IsEnabled && ((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Interior part buttons enabled");
        SetField(window, "_selectedInfo", info with { CurrentPart = 1 }); SetField(window, "_selectedIdentity", new VideoIdentity(info.Bvid, 1));
        Invoke(window, "UpdateEpisodeControls");
        Assert(!((Button)window.FindName("PreviousEpisodeButton")).IsEnabled, "First part previous disabled");
        SetField(window, "_selectedInfo", info with { CurrentPart = 4 }); SetField(window, "_selectedIdentity", new VideoIdentity(info.Bvid, 4));
        Invoke(window, "UpdateEpisodeControls");
        Assert(!((Button)window.FindName("NextEpisodeButton")).IsEnabled, "Last part next disabled");
        SetField(window, "_info", info); SetField(window, "_selectedInfo", info); SetField(window, "_selectedIdentity", new VideoIdentity(info.Bvid, 3));
        Invoke(window, "UpdateEpisodeControls");
        var state = new VideoState("【原神7.0一条龙】全宝箱 / 神瞳 / 任务 / 成就 / 观景点 · 长标题布局验证",
            "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3", false, 321, 604, false, 1.5, true, 1920, 1080);
        Invoke(window, "UpdateVideo", state);
        TestLibraryUi(window);
        Capture(window, root, "ui-follow-connected.png", 1020, 730);
        Capture(window, root, "ui-follow-connected-compact.png", 860, 600);
        ((ComboBox)window.FindName("EpisodeSelector")).IsDropDownOpen = true;
        ((ComboBox)window.FindName("EpisodeSelector")).IsDropDownOpen = false;
        var before = ((TextBlock)window.FindName("VideoTitle")).Text;
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        ((RadioButton)window.FindName("NavHotkeys")).IsChecked = true;
        TestBindingEditor(window);
        Capture(window, root, "ui-hotkeys-edited.png", 1020, 730);
        Capture(window, root, "ui-hotkeys-edited-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        Capture(window, root, "ui-settings-expanded.png", 1020, 730);
        Capture(window, root, "ui-settings-expanded-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavFollow")).IsChecked = true;
        Assert(((TextBlock)window.FindName("VideoTitle")).Text == before && GetField<VideoState>(window, "_state") == state, "Settings navigation preserves video state");
        ((Button)window.FindName("FollowRetryButton")).Visibility = Visibility.Visible;
        ((TextBlock)window.FindName("StatusText")).Text = "自动跟随未完成：浏览器请求被拒绝，请完成登录后重试。此处用长状态提示验证省略显示与完整悬停提示。";
        Capture(window, root, "ui-follow-error-compact.png", 860, 600);
        Capture(window, root, "ui-preview.png", 1020, 730);
        window.Left = -10000;
        window.Top = -10000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        CaptureEpisodeMenu(window, root, "ui-episode-menu.png");
        var guideSelector = (ComboBox)window.FindName("GuideVideoSelector");
        CaptureMenu(window, guideSelector, root, "ui-library-video-menu.png");
        TestLibraryPickerFlow(window);
        ((RadioButton)window.FindName("NavSettings")).IsChecked = true;
        ((ScrollViewer)window.FindName("SettingsPage")).ScrollToEnd();
        Capture(window, root, "ui-settings-bottom-compact.png", 860, 600);
        ((RadioButton)window.FindName("NavHotkeys")).IsChecked = true;
        window.UpdateLayout();
        var captureButton = Descendants<Button>((ItemsControl)window.FindName("HotkeyRows")).First(button => (string?)button.Content == "录入");
        captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var box = GetField<TextBox>(window, "_capturingBox")!;
        Assert(box.IsReadOnly, "Key recording starts without changing existing text");
        var original = box.Text;
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Invoke(window, "Binding_PreviewKeyDown", box, escape);
        Assert(escape.Handled && !box.IsReadOnly && box.Text == original && GetField<TextBox>(window, "_capturingBox") is null, "Escape cancels recording and preserves binding");
        var reverseCapture = Descendants<Button>((ItemsControl)window.FindName("HotkeyRows")).Single(button =>
            button.DataContext is { } row && (HotkeyAction)row.GetType().GetProperty("Action")!.GetValue(row)! == HotkeyAction.ReversePipVisibility);
        reverseCapture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reverseBox = GetField<TextBox>(window, "_capturingBox")!;
        var tildeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Oem3) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Invoke(window, "Binding_PreviewKeyDown", reverseBox, tildeKey);
        Assert(tildeKey.Handled && reverseBox.Text == "~" && !reverseBox.IsReadOnly && GetField<TextBox>(window, "_capturingBox") is null, "Physical tilde recording produces a single-key reversal binding");
        var hotkeysPage = (Grid)window.FindName("HotkeysPage");
        window.Width = 860;
        window.Height = 600;
        window.UpdateLayout();
        ((ScrollViewer)hotkeysPage.Children[0]).ScrollToEnd();
        Capture(window, root, "ui-hotkeys-bottom-compact.png", 860, 600);
        window.Close();
        var library = VideoLibraryCatalog.LoadBuiltIn().Libraries.Single();
        var second = library with { Id = "long-test-library", Name = "仅测试 · " + string.Concat(Enumerable.Repeat("长标题的一条龙地图导航视频库", 5)), Videos = [library.Videos[0] with { Title = string.Concat(Enumerable.Repeat("蒙德、璃月、龙脊雪山 · 长标题", 5)) }] };
        var multipleSettings = new AppSettings { HotkeysEnabled = false, SelectedVideoLibraryId = second.Id };
        var multipleWindow = new GenshinVideoHelper.App.MainWindow(multipleSettings, Path.Combine(root, "artifacts", "ui-test-profile"), false, new VideoLibraryCatalog([library, second], []), PreviewService());
        Assert(GetField<VideoLibrary>(multipleWindow, "_activeLibrary")!.Id == second.Id && ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).Items.Count == 1, "Multiple-library saved ID selects matching list instead of first library");
        ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).SelectedIndex = 0;
        Capture(multipleWindow, root, "ui-library-long-title-compact.png", 860, 600);
        Assert(((TextBlock)multipleWindow.FindName("LibrarySummaryText")).ActualHeight <= 24, "Long library summary remains one line and keeps playback controls visible");
        ((RadioButton)multipleWindow.FindName("NavSettings")).IsChecked = true;
        Capture(multipleWindow, root, "ui-library-long-settings-compact.png", 860, 600);
        Invoke(multipleWindow, "SelectLibrary", library);
        Assert(multipleSettings.SelectedVideoLibraryId == library.Id && ((ComboBox)multipleWindow.FindName("GuideVideoSelector")).Items.Count == 22, "Switching between two libraries reloads correct map list");
        multipleWindow.Close();
        TestLibraryPicker(root, library, second);
        var missingWindow = new GenshinVideoHelper.App.MainWindow(new AppSettings { HotkeysEnabled = false, SelectedVideoLibraryId = "missing-library" }, Path.Combine(root, "artifacts", "ui-test-profile"), false, episodeService: PreviewService());
        Assert(((TextBlock)missingWindow.FindName("StatusText")).Text.Contains("不存在") && ((ComboBox)missingWindow.FindName("GuideVideoSelector")).Items.Count == 0 && ((TextBox)missingWindow.FindName("UrlInput")).IsEnabled, "Missing library preserves manual input and reports error");
        Capture(missingWindow, root, "ui-library-missing-compact.png", 860, 600);
        missingWindow.Close();
        Console.WriteLine("WPF UI: three panels, default/minimum sizes, full-row Start below episodes, part boundaries, long title and state preservation passed.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string root, string name, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (window is GenshinVideoHelper.App.MainWindow main && ((ScrollViewer)main.FindName("FollowPage")).Visibility == Visibility.Visible)
        {
            var start = (Button)main.FindName("OpenButton");
            var url = (TextBox)main.FindName("UrlInput");
            var episodes = (ComboBox)main.FindName("EpisodeSelector");
            Assert(Math.Abs(start.ActualWidth - url.ActualWidth) < 1 && start.TranslatePoint(new Point(), content).Y >= episodes.TranslatePoint(new Point(0, episodes.ActualHeight), content).Y + 11, "Start spans full card width on its own row beneath episodes");
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, "artifacts", name));
        encoder.Save(file);
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(instance, args);
    private static T? GetField<T>(object instance, string field) =>
        (T?)instance.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string field, object value) =>
        instance.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void CaptureEpisodeMenu(GenshinVideoHelper.App.MainWindow window, string root, string name) => CaptureMenu(window, (ComboBox)window.FindName("EpisodeSelector"), root, name);

    private static void CaptureMenu(GenshinVideoHelper.App.MainWindow window, ComboBox combo, string root, string name)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        combo.IsDropDownOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var child = (FrameworkElement)popup.Child;
        child.UpdateLayout();
        Assert(child.ActualWidth > 0 && child.ActualWidth <= combo.ActualWidth + 1 && child.ActualHeight <= 300,
            "Episode dropdown stays within control width and scrolls at 300px");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "artifacts", name))) encoder.Save(file);
        combo.IsDropDownOpen = false;
    }

    private static void LifecycleUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => LifecycleUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    private static async Task LifecycleUiTestAsync(string root)
    {
        await using var owned = await TestSession.StartAsync(root, headed: true);
        await using var unrelated = await TestSession.StartAsync(root, headed: true);
        using var ownedBrowser = new ChromeBrowser(owned.ProfileDirectory);
        using var unrelatedBrowser = new ChromeBrowser(unrelated.ProfileDirectory);
        // GetBrowserProcessId is normally called after Open; establish only a profile-scoped connection here.
        await ownedBrowser.GetPagesAsync();
        await unrelatedBrowser.GetPagesAsync();
        var ownedPid = await ownedBrowser.GetBrowserProcessIdAsync();
        var unrelatedPid = await unrelatedBrowser.GetBrowserProcessIdAsync();
        using var ownedProcess = Process.GetProcessById(ownedPid);
        using var unrelatedProcess = Process.GetProcessById(unrelatedPid);
        _ = ownedProcess.Handle; _ = unrelatedProcess.Handle;
        var controller = new VideoController();
        for (var i = 0; i < 80; i++)
        {
            try { await controller.ExecuteAsync(owned.Page, new("ensurePip")); break; }
            catch (VideoNotReadyException) { await Task.Delay(200); }
        }
        var pip = PipWindowService.FindPip(ownedPid);
        Assert(pip != 0, "Lifecycle starts with real native PiP");
        var window = new GenshinVideoHelper.App.MainWindow(new AppSettings { VideoUrl = "", SelectedVideoLibraryId = null }, owned.ProfileDirectory, false, episodeService: PreviewService())
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var tray = GetField<TrayIconService>(window, "_tray")!;
            Assert(window.Icon is not null && tray.IsVisible, "Window/taskbar icon and tray share original icon asset");
            var notify = GetField<System.Windows.Forms.NotifyIcon>(tray, "_notify")!;
            Assert(notify.Icon is { Width: 32, Height: 32 }, "Tray loads 32px icon frame");
            window.WindowState = WindowState.Minimized;
            await WaitForAsync(() => !window.IsVisible && tray.IsVisible, "Minimize hides helper while tray remains", window);
            Assert(!ownedProcess.HasExited && PipWindowService.FindPip(ownedPid) != 0, "Minimizing retains browser and PiP");
            var menu = GetField<System.Windows.Forms.ContextMenuStrip>(tray, "_menu")!;
            ((System.Windows.Forms.ToolStripMenuItem)menu.Items[0]).PerformClick();
            await WaitForAsync(() => window.IsVisible && window.WindowState == WindowState.Normal, "Tray menu restores helper", window);
            window.WindowState = WindowState.Minimized;
            ((System.Windows.Forms.ToolStripMenuItem)menu.Items[2]).PerformClick();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert(ownedProcess.HasExited && PipWindowService.FindPip(ownedPid, includeHidden: true) == 0, "Tray exit closes dedicated browser and native PiP");
            Assert(!tray.IsVisible && !GetField<HotkeyService>(window, "_hotkeys")!.IsRegistered(HotkeyAction.TogglePlayback), "Exit removes tray and releases hotkeys");
            Assert(!unrelatedProcess.HasExited && (await new CdpClient().SendAsync(unrelated.BrowserSocket, "Browser.getVersion", new { })).ValueKind == JsonValueKind.Object,
                "Another Chrome profile remains alive and responsive");

            var staleProfile = Path.Combine(root, "artifacts", "stale-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staleProfile);
            var staleLines = await File.ReadAllLinesAsync(Path.Combine(unrelated.ProfileDirectory, "DevToolsActivePort"));
            await File.WriteAllLinesAsync(Path.Combine(staleProfile, "DevToolsActivePort"), [staleLines[0], "/devtools/browser/wrong-session"]);
            using var stale = new ChromeBrowser(staleProfile);
            await stale.CloseAsync();
            Assert(!unrelatedProcess.HasExited, "Stale port file cannot close a different browser session");
            var startupProfile = Path.Combine(root, "artifacts", "startup-exit-" + Guid.NewGuid().ToString("N"));
            var startupWindow = new GenshinVideoHelper.App.MainWindow(new AppSettings { VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3", SelectedVideoLibraryId = null }, startupProfile, false, episodeService: PreviewService())
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                startupWindow.Show();
                var startupBrowser = GetField<ChromeBrowser>(startupWindow, "_browser")!;
                var open = (Task)Invoke(startupWindow, "OpenVideoAsync")!;
                await WaitForAsync(() => GetField<List<Process>>(startupBrowser, "_launchedProcesses")!.Count > 0, "Chrome launch enters owned-process tracking", startupWindow);
                var launched = GetField<List<Process>>(startupBrowser, "_launchedProcesses")!.ToArray();
                var identities = launched.Select(process => Process.GetProcessById(process.Id)).ToArray();
                try
                {
                    foreach (var process in identities) _ = process.Handle;
                    startupWindow.Close();
                    await startupWindow.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
                    await open.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert(identities.All(process => process.HasExited), "Exit during launch leaves no owned Chrome process");
                    Assert(!unrelatedProcess.HasExited, "Startup cancellation still preserves another Chrome session");
                }
                finally { foreach (var process in identities) process.Dispose(); }
            }
            finally { startupWindow.Close(); await startupWindow.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
            Console.WriteLine("Lifecycle: tray icon/menu, minimize/restore, menu exit, owned browser/PiP close, hotkey release, other-profile isolation and stale-port safety and exit during launch passed.");
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
    private static void FollowUiTest(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => FollowUiTestAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
    }

    private static Application CreateTestApplication()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GenshinVideoHelper;component/Resources/PclTheme.xaml") });
        return app;
    }

    private static async Task FollowUiTestAsync(string root)
    {
        await using var session = await TestSession.StartAsync(root, headed: true);
        var settings = new AppSettings { SelectedVideoLibraryId = null, VideoUrl = "https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=333.788.videopod.episodes&p=3" };
        var window = new GenshinVideoHelper.App.MainWindow(settings, session.ProfileDirectory, false)
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        using var browser = new ChromeBrowser(session.ProfileDirectory);
        var controller = new VideoController();
        try
        {
            await WaitForAsync(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo") is { Bvid: "BV1hjgG6jEa6", CurrentPart: 3 }, "Real P3 metadata preloads before follow", window);
            Assert(GetField<BrowserPage>(window, "_page") is null && ((ComboBox)window.FindName("EpisodeSelector")).Items.Count > 4, "Real metadata preview is independent of Chrome playback");
            Console.WriteLine("Real Bilibili P3 episode list preloaded before Start.");
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Initial P3 automatic playback and PiP", window);
            Assert(GetField<VideoState>(window, "_state")!.Duration is > 603 and < 605, "P3 duration matches episode");
            var info = GetField<BilibiliVideoInfo>(window, "_info")!;
            Assert(info.Episodes.Count > 4 && info.CurrentPart == 3, "Real episode list and current P3");
            var originalPage = GetField<BrowserPage>(window, "_page")!;
            var pageCount = (await browser.GetPagesAsync()).Count;
            Console.WriteLine($"Auto follow: P3 ready; {info.Episodes.Count} parts; target={originalPage.Id}.");
            await TestAutomaticMouseVisibilityAsync(window);
            TestLibraryPickerFlow(window);
            await WaitForAsync(() => Ready(3), "Library picker confirm/cancel preserves live P3 and PiP", window);
            await WaitForAsync(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == "BV1MXfEY4EQ2", "Library first map metadata preloads while live P3 continues", window);
            Assert(GetField<BilibiliVideoInfo>(window, "_info")!.Bvid == "BV1hjgG6jEa6", "Preview preserves real active video episode list");
            Exception? pickerError = null;
            _ = window.Dispatcher.BeginInvoke(async () =>
            {
                var picker = Application.Current.Windows.OfType<GenshinVideoHelper.App.LibraryPickerWindow>().Single();
                try
                {
                    SendAltNumber(0x32);
                    await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: true }, "Global hotkey pauses while picker is open", window);
                    SendAltNumber(0x32);
                    await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: false }, "Global hotkey resumes while picker is open", window);
                }
                catch (Exception ex) { pickerError = ex; }
                finally { picker.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            Invoke(window, "ChangeLibrary_Click", window, new RoutedEventArgs());
            if (pickerError is not null) throw pickerError;
            Assert(GetField<BrowserPage>(window, "_page")!.Id == originalPage.Id, "Open library picker keeps exact live target");
            Console.WriteLine("Library picker search/confirm/cancel retains live session; Alt+2 remains active while open.");
            CaptureEpisodeMenu(window, root, "ui-live-episode-menu.png");
            var episodeHotkeys = GetField<HotkeyService>(window, "_hotkeys")!;
            Assert(episodeHotkeys.IsRegistered(HotkeyAction.PreviousEpisode) && episodeHotkeys.IsRegistered(HotkeyAction.NextEpisode), "Episode hotkeys registered");
            SendAltNumber(0x27);
            await WaitForAsync(() => Ready(4), "P4 automatic playback and PiP", window);
            Assert(GetField<BrowserPage>(window, "_page")!.Id == originalPage.Id &&
                   (await browser.GetPagesAsync()).Count == pageCount, "Switch reuses tab");
            Assert(GetField<VideoState>(window, "_state")!.Duration is > 962 and < 964, "P4 duration matches episode");
            Console.WriteLine("Alt+Right P3 -> P4: same tab, playback and restored PiP passed.");
            SendAltNumber(0x25);
            await WaitForAsync(() => Ready(3), "Alt+Left restores P3 and PiP", window);
            SendAltNumber(0x27);
            await WaitForAsync(() => Ready(4), "Alt+Right returns to P4", window);
            var one = (Task)Invoke(window, "SwitchEpisodeAsync", 5)!;
            var two = (Task)Invoke(window, "SwitchEpisodeAsync", 6)!;
            var three = (Task)Invoke(window, "SwitchEpisodeAsync", 4)!;
            await Task.WhenAll(one, two, three);
            await WaitForAsync(() => Ready(4), "Rapid switching settles on last P4 intent", window);
            Console.WriteLine("Rapid P5 -> P6 -> P4 cancellation passed.");

            await controller.ExecuteAsync(GetField<BrowserPage>(window, "_page")!, new("ensurePipClosed"));
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { PictureInPicture: false }, "Browser PiP close is observed", window);
            var mouseVisibility = GetField<PipMouseVisibilityService>(window, "_pipMouseVisibility")!;
            Assert(mouseVisibility.BrowserProcessId == 0 && mouseVisibility.WindowHandle == 0, "Manual close stops automatic mouse/Alt service");
            keybd_event(0xC0, 0, 0, 0);
            try { await Task.Delay(400); }
            finally { keybd_event(0xC0, 0, 2, 0); }
            await Task.Delay(3500);
            Assert(GetField<VideoState>(window, "_state") is { PictureInPicture: false } &&
                   !GetField<FollowSession>(window, "_follow")!.AutomaticPending, "Manual browser PiP close remains closed");
            await browser.NavigateAsync(GetField<BrowserPage>(window, "_page")!, new("BV1hjgG6jEa6", 3));
            await WaitForAsync(() => Ready(3), "Browser-side part change synchronizes and resumes follow", window);
            Console.WriteLine("Browser part synchronization and manual-close suppression passed.");

            var page = GetField<BrowserPage>(window, "_page")!;
            await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "window.__gvhHeldVideos=[...document.querySelectorAll('video')].map(v=>({v,parent:v.parentNode,next:v.nextSibling}));window.__gvhHeldVideos.forEach(x=>x.v.remove());"
            });
            Invoke(window, "FollowRetry_Click", window, new RoutedEventArgs());
            await WaitForAsync(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("等待视频就绪"), "Video loading feedback", window);
            Assert(GetField<FollowSession>(window, "_follow")!.AutomaticPending, "Loading retains automatic intent");
            await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "window.__gvhHeldVideos.forEach(x=>x.parent.insertBefore(x.v,x.next?.parentNode===x.parent?x.next:null));delete window.__gvhHeldVideos;"
            });
            await WaitForAsync(() => Ready(3), "Loading completion resumes automatic follow", window);
            Console.WriteLine("Delayed readiness and retry without blocking the UI passed.");

            var hotkeys = GetField<HotkeyService>(window, "_hotkeys")!;
            Console.WriteLine("Hotkeys: " + ((TextBlock)window.FindName("HotkeyStatus")).Text);
            Assert(hotkeys.IsRegistered(HotkeyAction.TogglePlayback) && hotkeys.IsRegistered(HotkeyAction.SeekBackward) &&
                   hotkeys.IsRegistered(HotkeyAction.SeekForward), "All three Alt-number hotkeys registered");
            SendAltNumber(0x32);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: true }, "Alt+2 pauses actual Bilibili video", window);
            await controller.ExecuteAsync(page, new("seek", 200, Absolute: true));
            await WaitForAsync(() => Math.Abs(GetField<VideoState>(window, "_state")!.CurrentTime - 200) < 0.5, "Seek test setup", window);
            SendAltNumber(0x31);
            await WaitForAsync(() => Math.Abs(GetField<VideoState>(window, "_state")!.CurrentTime - 195) < 0.5, "Alt+1 rewinds five seconds", window);
            SendAltNumber(0x33);
            await WaitForAsync(() => Math.Abs(GetField<VideoState>(window, "_state")!.CurrentTime - 200) < 0.5, "Alt+3 advances five seconds", window);
            SendAltNumber(0x32);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: false }, "Alt+2 resumes actual video", window);
            Console.WriteLine("Actual Alt+1 / Alt+2 / Alt+3 global keyboard actions passed.");
            settings.Hotkeys[HotkeyAction.TogglePlayback] = "Ctrl+Alt+F8";
            Invoke(window, "ApplyHotkeys");
            Assert(hotkeys.IsRegistered(HotkeyAction.TogglePlayback), "Custom playback binding registers");
            keybd_event(0x11, 0, 0, 0);
            SendAltNumber(0x77);
            keybd_event(0x11, 0, 2, 0);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: true }, "Custom Ctrl+Alt+F8 pauses video", window);
            settings.Hotkeys[HotkeyAction.TogglePlayback] = "Alt+2";
            Invoke(window, "ApplyHotkeys");
            SendAltNumber(0x32);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Paused: false }, "Restore default binding resumes video", window);
            Console.WriteLine("Editable modifier/key binding performs the real video action.");
            Assert(hotkeys.IsRegistered(HotkeyAction.TogglePip) && hotkeys.IsRegistered(HotkeyAction.PlacePip) && hotkeys.IsRegistered(HotkeyAction.ToggleMute), "Three Alt-only auxiliary bindings register");
            SendAltNumber(0x23);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Muted: true }, "Alt+End mutes actual video", window);
            SendAltNumber(0x23);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { Muted: false }, "Alt+End restores sound", window);
            SendAltNumber(0x50);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { PictureInPicture: false }, "Alt+P closes actual PiP", window);
            SendAltNumber(0x50);
            await WaitForAsync(() => GetField<VideoState>(window, "_state") is { PictureInPicture: true } && ((TextBlock)window.FindName("StatusText")).Text == "画中画已置顶在左下角。", "Alt+P opens and positions PiP", window);
            SendAltNumber(0x24);
            await WaitForAsync(() => ((TextBlock)window.FindName("StatusText")).Text == "画中画已放回本工具所在屏幕的左下角。", "Alt+Home places PiP", window);
            Console.WriteLine("Actual Alt+P / Alt+Home / Alt+End PiP toggle, placement and mute passed.");
            Capture(window, root, "ui-live-follow.png", 1020, 730);
            await new CdpClient().SendAsync(session.BrowserSocket, "Target.closeTarget", new { targetId = page.Id });
            await WaitForAsync(() => ((TextBlock)window.FindName("HeaderConnectionState")).Text == "连接已断开", "Closed page no longer displays connected state", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Restart recovers from closed page", window);
            Assert(GetField<BrowserPage>(window, "_page")!.Id != page.Id, "Restart selects new target");
            Console.WriteLine("Closed-page feedback and restart recovery passed.");
            await session.ShutdownBrowserAsync();
            await WaitForAsync(() => ((TextBlock)window.FindName("HeaderConnectionState")).Text == "连接已断开", "Entire browser close clears connection state", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(3), "Start follow relaunches closed Chrome", window);
            Console.WriteLine("Entire Chrome shutdown and relaunch recovery passed.");
            var libraryVideos = (ComboBox)window.FindName("GuideVideoSelector");
            libraryVideos.SelectedIndex = 0;
            Assert(GetField<BrowserPage>(window, "_page")!.Url.Contains("BV1hjgG6jEa6"), "Preset selection keeps current video running until start");
            await WaitForAsync(() => GetField<BilibiliVideoInfo>(window, "_selectedInfo")?.Bvid == "BV1MXfEY4EQ2", "Preset episodes available before starting selected map", window);
            await (Task)Invoke(window, "OpenVideoAsync")!;
            await WaitForAsync(() => Ready(1), "Selected built-in map opens and follows", window);
            Assert(VideoIdentity.Parse(GetField<BrowserPage>(window, "_page")!.Url).Bvid == "BV1MXfEY4EQ2" &&
                   GetField<BilibiliVideoInfo>(window, "_info")?.Bvid == "BV1MXfEY4EQ2", "Built-in preset reaches matching real video and episode list");
            Capture(window, root, "ui-live-library-follow.png", 1020, 730);
            Console.WriteLine("Built-in Hanqing map selection, real video metadata, automatic playback and PiP passed.");
        }
        catch
        {
            Console.WriteLine("Follow request: " + JsonSerializer.Serialize(GetField<FollowSession>(window, "_follow")!.Current?.Identity));
            Console.WriteLine("Video state: " + JsonSerializer.Serialize(GetField<VideoState>(window, "_state")));
            try
            {
                var diagnosticPage = GetField<BrowserPage>(window, "_page");
                if (diagnosticPage is not null)
                    Console.WriteLine("Media readiness: " + (await new CdpClient().SendAsync(new Uri(diagnosticPage.WebSocketDebuggerUrl), "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({cid:window.__INITIAL_STATE__?.cid,videos:[...document.querySelectorAll('video')].map(v=>({ready:v.readyState,network:v.networkState,duration:v.duration,paused:v.paused,error:v.error?.message,pip:v===document.pictureInPictureElement}))})"
                    })).GetRawText());
            }
            catch (Exception ex) { Console.WriteLine("Media diagnostics unavailable: " + ex.Message); }
            try { Console.WriteLine("Targets: " + (await new CdpClient().SendAsync(await session.GetCurrentBrowserSocketAsync(), "Target.getTargets", new { })).GetRawText()); }
            catch (Exception ex) { Console.WriteLine("Browser diagnostics unavailable: " + ex.Message); }
            throw;
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }

        bool Ready(int part) => GetField<FollowSession>(window, "_follow") is { AutomaticPending: false, Current.Identity.Part: var current } &&
            current == part && GetField<VideoState>(window, "_state") is { Paused: false, PictureInPicture: true } &&
            VideoIdentity.Parse(GetField<VideoState>(window, "_state")!.Url).Part == part;
    }

    private static async Task TestAutomaticMouseVisibilityAsync(GenshinVideoHelper.App.MainWindow window)
    {
        var visibility = GetField<PipMouseVisibilityService>(window, "_pipMouseVisibility")!;
        await WaitForAsync(() => visibility.WindowHandle != 0, "Open PiP automatically starts mouse service", window);
        var pip = visibility.WindowHandle;
        Assert(GetCursorPos(out var originalCursor) && GetWindowRect(pip, out _), "Automatic test obtains real cursor and PiP bounds");
        GetWindowRect(pip, out var bounds);
        var originalWindowState = window.WindowState;
        try
        {
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            await WaitForAsync(() => visibility.IsTemporarilyHidden && HasTransparentAppearance(pip), "Automatic timer hides on entering real PiP", window);
            Assert(GetField<VideoState>(window, "_state") is { PictureInPicture: true, Paused: false }, "Temporary hiding retains active playing PiP session");
            keybd_event(0xC0, 0, 0, 0);
            await WaitForAsync(() => !visibility.IsTemporarilyHidden && IsWindowVisible(pip) && !HasTransparentAppearance(pip), "Held tilde restores PiP through actual timer", window);
            keybd_event(0xC0, 0, 2, 0);
            await WaitForAsync(() => visibility.IsTemporarilyHidden && HasTransparentAppearance(pip), "Released tilde hides again through actual timer", window);
            window.WindowState = WindowState.Minimized;
            SetCursorPos(bounds.Right + (bounds.Right - bounds.Left), bounds.Top);
            await WaitForAsync(() => !visibility.IsTemporarilyHidden && IsWindowVisible(pip) && !HasTransparentAppearance(pip), "Mouse avoidance remains active while helper is minimized", window);
            Console.WriteLine("Actual Bilibili PiP automatic mouse/tilde detection and minimized-helper operation passed.");
        }
        finally
        {
            keybd_event(0xC0, 0, 2, 0);
            Invoke(window, "RestoreFromTray");
            window.WindowState = originalWindowState;
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }
    private static async Task WaitForAsync(Func<bool> predicate, string label, GenshinVideoHelper.App.MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(150);
        }
        throw new Exception("FAIL: " + label + "; UI: " + ((TextBlock)window.FindName("StatusText")).Text);
    }

    private static void SendAltNumber(byte key)
    {
        keybd_event(0x12, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        keybd_event(0x12, 0, 2, 0);
    }
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    private static async Task BrowserTestAsync(string root, bool headed)
    {
        await using var session = await TestSession.StartAsync(root, headed);
        var controller = new VideoController();
        VideoState? state = null;
        for (var i = 0; i < 40; i++)
        {
            try { state = await controller.ExecuteAsync(session.Page, new("status")); break; }
            catch (InvalidOperationException) { await Task.Delay(200); }
        }
        Assert(state is { Paused: true, Duration: > 19 }, "Metadata and finite duration");
        Assert(state!.VideoWidth == 640 && state.VideoHeight == 360, "Select main video instead of small secondary video");
        state = await controller.ExecuteAsync(session.Page, new("toggle"));
        Assert(!state.Paused, "Play");
        state = await controller.ExecuteAsync(session.Page, new("toggle"));
        Assert(state.Paused, "Pause");
        state = await controller.ExecuteAsync(session.Page, new("ensurePlay"));
        Assert(!state.Paused, "Ensure play starts playback");
        state = await controller.ExecuteAsync(session.Page, new("ensurePlay"));
        Assert(!state.Paused, "Repeated ensure play does not pause");
        await controller.ExecuteAsync(session.Page, new("toggle"));
        state = await controller.ExecuteAsync(session.Page, new("seek", 10, Absolute: true));
        Assert(Math.Abs(state.CurrentTime - 10) < 0.2, "Seek absolute");
        state = await controller.ExecuteAsync(session.Page, new("seek", -5));
        Assert(Math.Abs(state.CurrentTime - 5) < 0.2, "Seek backward five seconds");
        state = await controller.ExecuteAsync(session.Page, new("seek", -100));
        Assert(state.CurrentTime < 0.2, "Clamp seek at start");
        state = await controller.ExecuteAsync(session.Page, new("seek", 100));
        Assert(state.CurrentTime <= state.Duration && state.CurrentTime > 19, "Clamp seek at end");
        state = await controller.ExecuteAsync(session.Page, new("rate", 1.5));
        Assert(Math.Abs(state.PlaybackRate - 1.5) < 0.01, "Playback rate");
        state = await controller.ExecuteAsync(session.Page, new("mute"));
        Assert(!state.Muted, "Mute toggle");
        await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate",
            new { expression = "document.querySelectorAll('video').forEach(v => v.remove())" });
        var missingVideoReported = false;
        try { await controller.ExecuteAsync(session.Page, new("toggle")); }
        catch (InvalidOperationException ex) { missingVideoReported = ex.Message.Contains("视频尚未加载"); }
        Assert(missingVideoReported, "Report missing video");
        using var apiFallback = new BilibiliEpisodeService(new FixtureHandler("{\"code\":0,\"data\":" + EpisodeFixture + "}"));
        var fallbackInfo = await apiFallback.ReadAsync(session.Page with { Url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3" });
        Assert(fallbackInfo.Episodes.Count == 4, "Missing page metadata automatically falls back to API");
        await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate", new
        { expression = "window.__INITIAL_STATE__={videoData:" + EpisodeFixture + "}" });
        using var episodeReader = new BilibiliEpisodeService(new FixtureHandler("{\"code\":-404}"));
        var info = await episodeReader.ReadAsync(session.Page with { Url = "https://www.bilibili.com/video/BV1hjgG6jEa6/?p=3" });
        Assert(info.Episodes.Count == 4 && info.CurrentPart == 3, "Page episode metadata works before any video is present and does not require API");
        Console.WriteLine("Actual Chrome/CDP: media selection, play/pause, seek boundaries, rate, mute and missing video passed.");
        await session.CloseTabAsync();
        var closedReported = false;
        try { await controller.ExecuteAsync(session.Page, new("status")); }
        catch (IOException) { closedReported = true; }
        Assert(closedReported, "Report closed tab");
        Console.WriteLine("Closed-tab recovery error passed.");
    }

    private static void PipTest(string root, bool example = false)
    {
        var app = new Application();
        var helper = new Window { Width = 100, Height = 100, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        helper.Show();
        var handle = new WindowInteropHelper(helper).Handle;
        var hotkeys = new HotkeyService(handle);
        try
        {
            var blocker = new Window { Width = 50, Height = 50, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            blocker.Show();
            var blockerHandle = new WindowInteropHelper(blocker).Handle;
            var reserved = RegisterHotKey(blockerHandle, 0x99AA, 0x4001, 0x31);
            try
            {
                if (reserved)
                {
                    var blocked = hotkeys.Enable();
                    Assert(blocked.Any(label => label.Contains("Alt+1")) && !hotkeys.IsRegistered(HotkeyAction.SeekBackward) && hotkeys.RegisteredCount > 0,
                        "An occupied Alt+1 reports conflict while other hotkeys remain available");
                    Console.WriteLine("Partial hotkey conflict handling passed.");
                }
                else Console.WriteLine("Alt+1 is already occupied; reservation test skipped.");
            }
            finally
            {
                if (reserved) UnregisterHotKey(blockerHandle, 0x99AA);
                blocker.Close();
                hotkeys.Disable();
            }
            var conflicts = hotkeys.Enable();
            Assert(hotkeys.RegisteredCount > 0, "At least one hotkey registered");
            if (conflicts.Count > 0) Console.WriteLine("Unavailable hotkeys: " + string.Join("; ", conflicts));
            hotkeys.Disable();
            hotkeys.Enable();
            Console.WriteLine("Hotkey registration, release and re-registration passed.");
            // Async work does not depend on this off-screen WPF window's dispatcher.
            Task.Run(async () =>
            {
                if (example)
                {
                    await BilibiliTestAsync(root, headed: true, helperWindow: handle);
                    return;
                }
                await using var session = await TestSession.StartAsync(root, headed: true);
                var controller = new VideoController();
                VideoState? state = null;
                for (var i = 0; i < 80; i++)
                {
                    try { state = await controller.ExecuteAsync(session.Page, new("ensurePip")); break; }
                    catch (VideoNotReadyException) { await Task.Delay(200); }
                }
                if (state is null)
                    Console.WriteLine("Local media readiness: " + (await new CdpClient().SendAsync(new Uri(session.Page.WebSocketDebuggerUrl), "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({url:location.href,ready:document.readyState,text:document.body?.innerText,videos:[...document.querySelectorAll('video')].map(v=>({src:v.currentSrc,ready:v.readyState,network:v.networkState,error:v.error?.message}))})"
                    })).GetRawText());
                Assert(state?.PictureInPicture == true, "Native Chrome PiP entered after media readiness");
                state = await controller.ExecuteAsync(session.Page, new("ensurePip"));
                Assert(state.PictureInPicture, "Repeated ensure PiP does not close window");
                var reply = await new CdpClient().SendAsync(session.BrowserSocket, "SystemInfo.getProcessInfo", new { });
                var process = reply.GetProperty("processInfo").EnumerateArray()
                    .First(item => item.GetProperty("type").GetString() == "browser");
                var pid = (int)process.GetProperty("id").GetDouble();
                await PipWindowService.PlaceAsync(pid, handle, 420, 20, 16d / 9);
                nint pip = 0;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner != pid || !IsWindowVisible(window) || (GetWindowLong(window, -20) & 8) == 0) return true;
                    var title = new StringBuilder(512);
                    GetWindowText(window, title, title.Capacity);
                    Console.WriteLine($"Owned topmost window: {title}");
                    pip = window;
                    return false;
                }, 0);
                Assert(pip != 0, "Owned topmost PiP window");
                GetWindowRect(pip, out var bounds);
                var monitor = MonitorFromWindow(handle, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                GetMonitorInfo(monitor, ref info);
                var margin = (int)Math.Round(20 * GetDpiForWindow(handle) / 96d);
                Assert(Math.Abs(bounds.Left - info.Work.Left - margin) <= 3, "PiP left margin");
                Assert(Math.Abs(bounds.Bottom - info.Work.Bottom + margin) <= 3, "PiP bottom margin");
                await TestMouseVisibilityAsync(session, pid, handle, controller);
                state = await controller.ExecuteAsync(session.Page, new("pip"));
                Assert(!state.PictureInPicture, "Exit PiP");
                Console.WriteLine("Native PiP entry, topmost, bottom-left positioning and exit passed.");
            }).GetAwaiter().GetResult();
        }
        finally { hotkeys.Dispose(); helper.Close(); app.Shutdown(); }
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    private static bool HasTransparentAppearance(nint window) =>
        (GetWindowLong(window, -20) & 0x00000020) != 0 &&
        GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && alpha == 0 && (flags & 2) != 0;
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    private static async Task TestMouseVisibilityAsync(TestSession session, int pid, nint helper, VideoController controller)
    {
        Assert(GetCursorPos(out var originalCursor), "Read original cursor position");
        using var visibility = new PipMouseVisibilityService(pollAutomatically: false);
        try
        {
            var pip = PipWindowService.FindPip(pid);
            Assert(pip != 0 && GetWindowRect(pip, out _), "Discover only owned PiP window");
            var originalExtendedStyle = GetWindowLong(pip, -20);
            GetWindowRect(pip, out var bounds);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var centerY = (bounds.Top + bounds.Bottom) / 2;
            Assert(SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.25), centerY), "Move pointer outside expanded area");
            visibility.TrackBrowser(pid);
            await ExpectVisibility(true, "Outside shows PiP");
            var foreground = GetForegroundWindow();
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));
            await Task.Delay(700);
            var startTime = (await controller.ExecuteAsync(session.Page, new("status"))).CurrentTime;
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "20-percent margin hides before entering actual window");
            Assert(WindowFromPoint(new ScreenPoint { X = (bounds.Left + bounds.Right) / 2, Y = centerY }) != pip, "Hidden window no longer captures mouse hit testing");
            await Task.Delay(700);
            var hiddenState = await controller.ExecuteAsync(session.Page, new("status"));
            Assert(hiddenState.PictureInPicture && !hiddenState.Paused && hiddenState.CurrentTime > startTime + 0.2, "Hidden Chrome PiP session stays open and continues playback");
            var pausedHidden = await controller.ExecuteAsync(session.Page, new("toggle"));
            Assert(pausedHidden.PictureInPicture && pausedHidden.Paused, "User can pause while PiP is temporarily hidden");
            SetCursorPos(bounds.Right + width, centerY);
            await ExpectVisibility(true, "Leaving hiding region restores paused PiP");
            Assert((await controller.ExecuteAsync(session.Page, new("status"))).Paused, "Restoring opacity never resumes a user-paused video");
            SetCursorPos(bounds.Right + (int)(width * 0.1), centerY);
            await ExpectVisibility(false, "Paused PiP continues normal mouse avoidance");
            await controller.ExecuteAsync(session.Page, new("ensurePlay"));

            keybd_event(0xC0, 0, 0, 0);
            await ExpectVisibility(true, "Tilde held inside forces show");
            keybd_event(0xC0, 0, 2, 0);
            await ExpectVisibility(false, "Releasing tilde inside restores hide");
            SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.21), centerY);
            await ExpectVisibility(true, "Just beyond 20-percent horizontal boundary shows");
            keybd_event(0xC0, 0, 0, 0);
            await ExpectVisibility(false, "Tilde held outside forces hide");
            keybd_event(0xC0, 0, 2, 0);
            await ExpectVisibility(true, "Releasing tilde outside restores show");
            SetCursorPos((bounds.Left + bounds.Right) / 2, bounds.Top - (int)(height * 0.1));
            await ExpectVisibility(false, "Vertical expanded margin also hides");
            keybd_event(0x12, 0, 0, 0);
            await ExpectVisibility(false, "Old Alt key no longer reverses default hiding");
            keybd_event(0x12, 0, 2, 0);
            visibility.SetReversalBinding(new HotkeyGesture(2, 0x77, "Ctrl+F8"));
            keybd_event(0x77, 0, 0, 0);
            await ExpectVisibility(false, "Custom chord needs its modifier");
            keybd_event(0x11, 0, 0, 0);
            await ExpectVisibility(true, "Custom Ctrl+F8 held reverses hiding");
            keybd_event(0x11, 0, 2, 0);
            await ExpectVisibility(false, "Releasing chord modifier restores hiding");
            visibility.SetReversalBinding(new HotkeyGesture(0, 0x77, "F8"));
            await ExpectVisibility(true, "New single-key binding takes effect immediately");
            visibility.SetReversalBinding(null);
            await ExpectVisibility(false, "Disabled hold binding cannot reverse hiding");
            keybd_event(0x77, 0, 2, 0);
            visibility.SetReversalBinding(new HotkeyGesture(0, 0xC0, "~"));
            SetCursorPos(bounds.Right + (int)Math.Ceiling(width * 0.25), centerY);
            await ExpectVisibility(true, "Show after leaving area");
            Assert(GetForegroundWindow() == foreground, "Restoring native PiP does not steal focus");
            Assert((GetWindowLong(pip, -20) & 0x00080020) == (originalExtendedStyle & 0x00080020), "Restore original layering and hit-test styles");

            SetCursorPos((bounds.Left + bounds.Right) / 2, centerY);
            await ExpectVisibility(false, "Hide actual window interior");
            var monitor = MonitorFromWindow(helper, 2);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref monitorInfo);
            Assert(SetWindowPos(pip, new nint(-1), monitorInfo.Work.Left + 100, monitorInfo.Work.Top + 70, width + 80, height + 45, 0x0010), "Move and resize hidden window without showing");
            await ExpectVisibility(true, "Uses moved bounds instead of former hiding region");
            GetWindowRect(pip, out bounds);
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            await ExpectVisibility(false, "Resized current window region hides");
            visibility.TrackBrowser(pid + 100000);
            await Task.Delay(80);
            Assert(IsWindowVisible(pip) && !HasTransparentAppearance(pip) && visibility.WindowHandle == 0, "Changing tracked browser restores prior window and ignores other process IDs");
            visibility.TrackBrowser(pid);
            await ExpectVisibility(false, "Track owned window again");

            Assert(PostMessage(pip, 0x0010, 0, 0), "Close owned native PiP using its actual close message");
            for (var i = 0; i < 40; i++)
            {
                if (!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture) break;
                await Task.Delay(50);
            }
            Assert(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture, "Chrome observes native window close");
            SetCursorPos(bounds.Right + width, bounds.Top);
            for (var i = 0; i < 6; i++)
            {
                keybd_event(0xC0, 0, i % 2 == 0 ? 0u : 2u, 0);
                visibility.Refresh();
                await Task.Delay(40);
            }
            Assert(PipWindowService.FindPip(pid) == 0 && visibility.WindowHandle == 0, "Closed native PiP cannot be restored by pointer or Alt before browser poll");
            visibility.Suspend();
            Assert(!(await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture && visibility.BrowserProcessId == 0 && visibility.WindowHandle == 0, "Manual PiP close stops hiding; mouse/reversal key never reopen it");
            await controller.ExecuteAsync(session.Page, new("ensurePip"));
            await PipWindowService.PlaceAsync(pid, helper, 420, 20, 16d / 9);
            pip = PipWindowService.FindPip(pid, includeHidden: true);
            GetWindowRect(pip, out bounds);
            SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
            visibility.TrackBrowser(pid);
            await ExpectVisibility(false, "Reopened PiP resumes mouse avoidance");
            visibility.Dispose();
            for (var i = 0; i < 60 && !IsWindowVisible(pip); i++) await Task.Delay(20);
            Assert(IsWindowVisible(pip) && !HasTransparentAppearance(pip) && (await controller.ExecuteAsync(session.Page, new("status"))).PictureInPicture, "Stopping helper restores only its temporarily hidden window");
            Console.WriteLine("Mouse avoidance: 20-percent margins, playback/hit testing, tilde press/release, editable single/chord bindings and disabled reversal, focus, moved/resized bounds, owner isolation, manual-close suppression and disposal restoration passed.");
        }
        finally
        {
            keybd_event(0xC0, 0, 2, 0);
            keybd_event(0x12, 0, 2, 0);
            keybd_event(0x11, 0, 2, 0);
            keybd_event(0x77, 0, 2, 0);
            visibility.Dispose();
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }

        async Task ExpectVisibility(bool shown, string label)
        {
            for (var i = 0; i < 100; i++)
            {
                visibility.Refresh();
                if (visibility.WindowHandle != 0 && IsWindowVisible(visibility.WindowHandle) && HasTransparentAppearance(visibility.WindowHandle) == !shown && visibility.IsTemporarilyHidden == !shown) return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("ASSERT: " + label);
        }
    }

    private static async Task BilibiliTestAsync(string root, bool headed = false, nint helperWindow = default)
    {
        await using var session = await TestSession.StartAsync(root, headed);
        using var browser = new ChromeBrowser(session.ProfileDirectory);
        const string example = "https://www.bilibili.com/video/BV1hjgG6jEa6";
        await browser.OpenAsync(example);
        var controller = new VideoController();
        string? lastError = null;
        BrowserPage? page = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(500);
            page = (await browser.GetPagesAsync()).FirstOrDefault(item => item.Url.Contains("BV1hjgG6jEa6"));
            if (page is null) continue;
            try
            {
                var state = await controller.ExecuteAsync(page, new("status"));
                Console.WriteLine($"Example video: {state.Title}, duration={state.Duration}, size={state.VideoWidth}x{state.VideoHeight}");
                if (state.Paused) state = await controller.ExecuteAsync(page, new("toggle"));
                Assert(!state.Paused, "Example plays");
                state = await controller.ExecuteAsync(page, new("toggle"));
                Assert(state.Paused, "Example pauses");
                var original = state.CurrentTime;
                state = await controller.ExecuteAsync(page, new("seek", 5));
                Assert(state.CurrentTime >= original + 4.5, "Example seeks five seconds");
                await controller.ExecuteAsync(page, new("seek", original, Absolute: true));
                var metadata = await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
                {
                    expression = "JSON.stringify({part:window.__INITIAL_STATE__?.videoData?.pages?.[0],videos:[...document.querySelectorAll('video')].map(v=>({duration:v.duration,ready:v.readyState,width:v.videoWidth,height:v.videoHeight}))})",
                    returnByValue = true
                });
                Console.WriteLine("Page metadata: " + metadata.GetProperty("result").GetProperty("value").GetString());
                if (headed)
                {
                    state = await controller.ExecuteAsync(page, new("pip"));
                    Assert(state.PictureInPicture, "Bilibili enters native PiP");
                    await PipWindowService.PlaceAsync(await browser.GetBrowserProcessIdAsync(), helperWindow,
                        420, 20, (double)state.VideoWidth / state.VideoHeight);
                    state = await controller.ExecuteAsync(page, new("toggle"));
                    Assert(!state.Paused, "Bilibili PiP plays");
                    state = await controller.ExecuteAsync(page, new("toggle"));
                    Assert(state.Paused, "Bilibili PiP pauses");
                    state = await controller.ExecuteAsync(page, new("pip"));
                    Assert(!state.PictureInPicture, "Bilibili exits native PiP");
                    Console.WriteLine("Bilibili example native PiP, bottom-left positioning and playback control passed.");
                }
                Console.WriteLine("Bilibili example: browser reconnect, page selection, metadata, play/pause and seek passed.");
                return;
            }
            catch (InvalidOperationException ex) { lastError = ex.Message; }
        }
        if (page is not null)
        {
            var diagnostics = await new CdpClient().SendAsync(new Uri(page.WebSocketDebuggerUrl), "Runtime.evaluate", new
            {
                expression = "JSON.stringify({title:document.title,url:location.href,videos:document.querySelectorAll('video').length,message:document.body.innerText.slice(0,200)})",
                returnByValue = true
            });
            Console.WriteLine(diagnostics.GetProperty("result").GetProperty("value").GetString());
        }
        throw new InvalidOperationException("Example not verified: " + (lastError ?? "No Bilibili page connected."));
    }

    private static void Assert(bool passed, string label)
    {
        if (!passed) throw new Exception("FAIL: " + label);
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private readonly Process _process;
        public BrowserPage Page { get; }
        public Uri BrowserSocket { get; }
        public string ProfileDirectory { get; }
        private TestSession(Process process, BrowserPage page, Uri browserSocket, string profileDirectory)
        { _process = process; Page = page; BrowserSocket = browserSocket; ProfileDirectory = profileDirectory; }

        public static async Task<TestSession> StartAsync(string root, bool headed)
        {
            var testDirectory = Path.Combine(root, "artifacts", "browser-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            var media = Path.Combine(root, "artifacts", "test-media", "sample.mp4");
            if (!File.Exists(media)) throw new FileNotFoundException("Generate artifacts/test-media/sample.mp4 first.");
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
                        return new TestSession(process, new BrowserPage(page.GetProperty("id").GetString()!,
                            page.GetProperty("title").GetString()!, page.GetProperty("url").GetString()!,
                            page.GetProperty("webSocketDebuggerUrl").GetString()!),
                            new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!), Path.Combine(testDirectory, "profile"));
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TaskCanceledException) { }
                }
                throw new TimeoutException("Test Chrome did not start.");
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        }

        public async Task CloseTabAsync() => await new CdpClient().SendAsync(BrowserSocket, "Target.closeTarget", new { targetId = Page.Id });

        public async Task<Uri> GetCurrentBrowserSocketAsync()
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(ProfileDirectory, "DevToolsActivePort"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var version = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:" + lines[0] + "/json/version"));
            var socket = new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
            Assert(socket.AbsolutePath == lines[1].Trim(), "Cleanup socket belongs to isolated test profile");
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
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Bounds Monitor, Work; public uint Flags; }
    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Bounds bounds);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}




