using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Bf6Highlights.Ui;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class RegionWindowSmokeTests
{
    // Needs the recordings in the untracked samples folder, which CI does not have.
    [Fact, Trait("Category", "LocalSamples")]
    public async Task VideoEditingWindowsOpenWithoutANativeChildWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var video = Path.Combine(RepositoryRoot(), "samples", "2026-09-13 14-36-06.mp4");
        var metadata = await new VideoService().ProbeAsync(video);
        var videoItem = new VideoItem { Path = video };
        await videoItem.ProbeAsync(CancellationToken.None);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SmokeApp>().UsePlatformDetect().SetupWithoutStarting();
                var window = new RegionWindow(new RegionFrame(metadata, 1), null);
                window.Show();
                var frame = window.FindControl<Image>("Frame")!;
                var timeout = Stopwatch.StartNew();
                while (frame.Source is null && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }
                Assert.NotNull(frame.Source);
                var firstFrame = frame.Source;
                window.FindControl<Slider>("Seek")!.Value = 5;
                timeout.Restart();
                while (ReferenceEquals(frame.Source, firstFrame)
                       && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }
                Assert.NotSame(firstFrame, frame.Source);
                window.Close();

                var sample = new SampleWindow(videoItem, Path.Combine(Path.GetTempPath(), "bfhe-smoke-sample"),
                    detection: new()
                    {
                        Player = new() { Names = ["BulletWaltz"] },
                        Killfeed = new()
                        {
                            Region = new() { X = 0, Y = 0, Width = metadata.Width, Height = metadata.Height },
                        },
                    });
                sample.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(sample.FindControl<LibVLCSharp.Avalonia.VideoView>("Video")!.MediaPlayer);
                Assert.True(sample.FindControl<Slider>("Seek")!.Maximum > 0);
                Assert.True(sample.FindControl<Button>("NextPotential")!.IsEnabled);
                Assert.True(sample.FindControl<Button>("NextDeath")!.IsEnabled);
                sample.Close();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UI-Testthread wurde nicht beendet.");
    }

    private sealed class SmokeApp : Application;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "samples"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        throw new DirectoryNotFoundException(
            $"Repository-Wurzel oberhalb von '{AppContext.BaseDirectory}' nicht gefunden.");
    }
}
