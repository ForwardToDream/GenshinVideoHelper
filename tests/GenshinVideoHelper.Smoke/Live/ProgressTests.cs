using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Progress;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Infrastructure.Progress;

namespace GenshinVideoHelper.Smoke;

/// <summary>Watch progress against a real video across two runs of the application. Needs Chrome and network;
/// sends no global input and takes no focus.</summary>
internal static class ProgressTests
{
    private const string Bvid = "BV1hjgG6jEa6";

    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var task = app.Dispatcher.InvokeAsync(() => RunAsync(root)).Task.Unwrap();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string root)
    {
        using var temporary = TestArtifacts.CreateTemp(root, "progress");
        var profile = Path.Combine(temporary.Path, "profile");
        var file = Path.Combine(temporary.Path, "progress.json");
        double resumePoint;

        var window = Create(profile, file, part: 3);
        try
        {
            var progress = window.Services.Progress;
            EpisodeProgress? Episode(int part) => progress.Get(Bvid)?.FindPart(part);
            await FollowAsync(window);
            await WaitAsync(() => Episode(3) is { Watched: >= 5 }, 40, "Real playback is recorded as watched");
            var first = Episode(3)!;
            Check(first is { Segments.Count: 1, Status: EpisodeStatus.InProgress } && progress.Get(Bvid)!.Episodes.Where(episode => episode.Part != 3).All(episode => !episode.HasActivity),
                "Only the part that played has progress, as one continuous span");
            Console.WriteLine($"Progress: recorded {first.Watched:0.0}s of real playback, resume point {TimeText.Format(first.Position)}.");

            var target = Math.Round(first.Segments[0].End) + 240;
            await window.Services.Follow.ExecuteAsync(new("seek", target, Absolute: true));
            await WaitAsync(() => Episode(3) is { Segments.Count: 2 } episode && episode.Segments[1].Length >= 4, 40, "Playback after a seek becomes a second span");
            var jumped = Episode(3)!;
            Check(jumped.Segments[1].Start >= target - 2 && jumped.Segments[0].End < target - 200 && jumped is { Status: EpisodeStatus.InProgress, Coverage: < 0.2 },
                "Seeking forward leaves the skipped part unwatched and completes nothing");
            Check(jumped.Position >= target, "Resume point follows settled playback");
            Console.WriteLine("Progress: spans after seek " + string.Join(" ", jumped.Segments.Select(span => $"[{span.Start:0.0}-{span.End:0.0}]")) + ".");

            Check(progress.CompleteBefore(Bvid, 3) && progress.FirstUnfinishedPart(Bvid, out _) == 3, "Earlier parts can be marked completed in bulk");
            await WaitAsync(() => ((TextBlock)window.FindName("WatchProgressText")).Text.Contains("本集已看"), 10, "Start page shows the current part's progress");
            resumePoint = Episode(3)!.Position;
        }
        finally { await CloseAsync(window); }
        Check(File.Exists(file) && File.ReadAllText(file).Contains(Bvid), "Progress is written on exit");

        // A second run over the same files, started on P1: it should move to P3 and continue where the first run stopped.
        window = Create(profile, file, part: 1);
        try
        {
            Check(VideoIdentity.Parse(((TextBox)window.FindName("UrlInput")).Text) == new VideoIdentity(Bvid, 3), "Startup continues at the first unfinished part");
            var saved = window.Services.Progress.Get(Bvid)!.FindPart(3)!;
            Check(saved.Segments.Count == 2 && saved.Position >= resumePoint, "Reloaded progress keeps spans and resume point");
            await FollowAsync(window);
            var state = window.Services.Follow.Current;
            Check(state.Video!.CurrentTime >= resumePoint - 3 && state.Message!.Contains("继续"), "Following resumes at the saved position");
            Console.WriteLine($"Progress: second run resumed P3 at {TimeText.Format(state.Video.CurrentTime)} (saved {TimeText.Format(resumePoint)}).");
        }
        finally { await CloseAsync(window); }
    }

    private static MainWindow Create(string profile, string file, int part) =>
        new(new AppServices(new AppSettings { VideoUrl = new VideoIdentity(Bvid, part).Url, SelectedVideoLibraryId = null, HotkeysEnabled = false },
            profile, progressStore: new JsonProgressStore(file)))
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };

    private static async Task FollowAsync(MainWindow window)
    {
        // The video would otherwise play aloud for the length of the test.
        var muting = false;
        window.Services.Follow.Changed += async snapshot =>
        {
            if (muting || snapshot.Video is not { Muted: false }) return;
            muting = true;
            await window.Services.Follow.ExecuteAsync(new("mute"));
            muting = window.Services.Follow.Current.Video is { Muted: true };
        };
        window.Show();
        await WaitAsync(() => !window.Services.Preview.Current.Loading, 15, "Episode preview finishes loading");
        ((Button)window.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => window.Services.Follow.Current is { Phase: FollowPhase.Following, Video.Paused: false } ||
                              window.Services.Follow.Current is { Phase: FollowPhase.Failed, CanRetry: true }, 60, "Automatic follow reaches playback");
        Check(window.Services.Follow.Current.Phase == FollowPhase.Following, "Automatic follow succeeds: " + window.Services.Follow.Current.Message);
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task WaitAsync(Func<bool> ready, int seconds, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Check(timer.Elapsed < TimeSpan.FromSeconds(seconds), label);
            await Task.Delay(100);
        }
    }
}
