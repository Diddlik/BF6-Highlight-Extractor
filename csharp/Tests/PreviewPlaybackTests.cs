using System.Diagnostics;
using LibVLCSharp.Shared;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class PreviewPlaybackTests
{
    [Fact]
    public async Task BothSampleVideosSupportPlaybackSeekAndPause()
    {
        if (!OperatingSystem.IsWindows()) return;

        var samples = Path.Combine(RepositoryRoot(), "samples");
        string[] videos =
        [
            Path.Combine(samples, "2026-09-13 14-36-06.mp4"),
            Path.Combine(samples, "2026-09-13 14-54-35.mp4"),
        ];
        Assert.All(videos, video => Assert.True(File.Exists(video),
            $"Referenzvideo fehlt: '{video}'."));

        Core.Initialize();
        using var libVlc = new LibVLC("--no-video-title-show");
        foreach (var video in videos)
        {
            using var player = new MediaPlayer(libVlc);
            using var media = new Media(libVlc, video, FromType.FromPath);

            Assert.True(player.Play(media), $"LibVLC konnte '{video}' nicht starten.");
            Assert.True(await WaitUntilAsync(
                    () => player.State == VLCState.Playing && player.Length > 0,
                    TimeSpan.FromSeconds(10)),
                $"'{video}' wurde nicht innerhalb von 10 s abspielbereit: "
                + $"State={player.State}, Length={player.Length} ms, Time={player.Time} ms.");

            Assert.True(player.Length > 500,
                $"'{video}' meldet keine gültige Seek-Spanne: Length={player.Length} ms.");
            var target = Math.Clamp(player.Length / 3, 250, player.Length - 250);
            player.Time = target;
            Assert.True(await WaitUntilAsync(
                    () => player.Time >= target - 500 && player.Time <= target + 3_000,
                    TimeSpan.FromSeconds(10)),
                $"Seek in '{video}' erreichte {target} ms nicht: "
                + $"State={player.State}, Time={player.Time} ms, Length={player.Length} ms.");

            player.Pause();
            Assert.True(await WaitUntilAsync(
                    () => player.State == VLCState.Paused && !player.IsPlaying,
                    TimeSpan.FromSeconds(10)),
                $"'{video}' pausierte nicht: State={player.State}, IsPlaying={player.IsPlaying}, "
                + $"Time={player.Time} ms.");
            player.Stop();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "samples"))
                && Directory.Exists(Path.Combine(directory.FullName, "csharp")))
                return directory.FullName;
        throw new DirectoryNotFoundException(
            $"Repository-Wurzel oberhalb von '{AppContext.BaseDirectory}' nicht gefunden.");
    }
}
