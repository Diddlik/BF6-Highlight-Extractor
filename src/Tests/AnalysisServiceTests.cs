using System.Text.Json;
using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>
/// End to end without real footage: the video is generated with FFmpeg and the OCR engine is a fake,
/// exactly as the Python integration tests do it. Frame n has the luma value 4*n, so the fake can
/// tell from the pixels which frame it was handed.
/// </summary>
public sealed class AnalysisServiceTests : IAsyncLifetime
{
    private const double Fps = 10;
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "bf6-analysis-" + Guid.NewGuid().ToString("N"));
    private string video = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        video = Path.Combine(directory, "match.mkv");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            $"color=c=black:size=128x64:rate={Fps}", "-t", "6",
            "-vf", "format=gray,geq=lum='clip(4*N,0,255)'", "-c:v", "ffv1", "-pix_fmt", "gray", video],
            TimeSpan.FromSeconds(60));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Answers with the killfeed row configured for a frame number, otherwise with nothing.</summary>
    private sealed class FakeOcr(Dictionary<int, (string Text, int X)> rows) : IOcrEngine
    {
        private int calls;
        public int Calls => calls;

        public Task<IReadOnlyList<OcrLine>> ReadAsync(Mat crop, PixelRegion origin,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref calls);
            var frame = crop.At<Vec3b>(0, 0).Item0 / 4;
            IReadOnlyList<OcrLine> lines = rows.TryGetValue(frame, out var row)
                ? [new(row.Text, 0.95, new(origin.X + row.X, origin.Y + 10, 120, 16))] : [];
            return Task.FromResult(lines);
        }

        public void Dispose() { }
    }

    private static Configuration Config(int workers = 1, bool changeDetection = false) => new()
    {
        Player = new() { Names = ["BulletWaltz"] },
        Killfeed = new() { Region = new() { X = 0, Y = 0, Width = 128, Height = 64 } },
        Analysis = new()
        {
            SamplesPerSecond = 5, MaxWorkers = workers, EnableChangeDetection = changeDetection,
            ChangeThreshold = 0.08,
        },
        Clips = new() { SecondsBefore = 1.0, SecondsAfter = 1.0, MergeGapSeconds = 1.5 },
    };

    private async Task<(AnalysisResult Result, string Output, FakeOcr Ocr)> Run(Configuration configuration,
        Dictionary<int, (string, int)> rows)
    {
        var output = Path.Combine(directory, "out-" + Guid.NewGuid().ToString("N"));
        var ocr = new FakeOcr(rows);
        var result = await new AnalysisService(configuration, () => ocr).RunAsync(video, output);
        return (result, output, ocr);
    }

    [Fact]
    public async Task AKillOnTheKillerSideBecomesOneEventAndOneClipSegment()
    {
        var (result, output, ocr) = await Run(Config(), new() { [20] = ("BulletWaltz Gegner1", 4) });
        Assert.False(result.Interrupted);
        Assert.Equal(30, result.Summary.FramesSampled);
        Assert.Equal(30, ocr.Calls);
        var events = Reports.ReadEventsJson(Path.Combine(output, "events.json"));
        Assert.Single(events);
        Assert.Equal(2.0, events[0].TimestampSeconds);
        Assert.Equal(20, events[0].FrameNumber);
        Assert.Equal("BulletWaltz", events[0].PlayerNameDetected);
        Assert.Equal("Gegner1", events[0].OpponentName);
        Assert.Equal("match.mkv", events[0].SourceVideo);
        Assert.Equal([(1.0, 3.0)], result.Segments.Select(s => (s.StartSeconds, s.EndSeconds)));
        Assert.Equal("single_kill", result.Segments[0].ClipType);
        Assert.True(File.Exists(Path.Combine(output, "events.csv")));
        Assert.True(File.Exists(Path.Combine(output, "segments.json")));
    }

    /// <summary>A killfeed entry stays on screen, here from the given frame on for two seconds.</summary>
    private static Dictionary<int, (string, int)> VisibleFrom(int first) =>
        Enumerable.Range(first, 19).ToDictionary(frame => frame, _ => ("BulletWaltz Gegner1", 4));

    private static Configuration FinderConfig() => Config() with
    {
        Deduplication = new() { DuplicateWindowSeconds = 2.0 },
    };

    [Fact]
    public async Task TheNextPotentialFrameIsTheFirstFrameOfTheNextDetectedKill()
    {
        var metadata = await new VideoService().ProbeAsync(video);
        var ocr = new FakeOcr(VisibleFrom(20));

        var timestamp = await PotentialFrameFinder.FindNextAsync(FinderConfig(), metadata,
            afterSeconds: 0.5, () => ocr);

        Assert.Equal(2.0, timestamp);
        // A coarse pass plus the window before the hit, not every sampled frame of the video.
        Assert.InRange(ocr.Calls, 1, 15);
    }

    [Fact]
    public async Task TheNextPotentialDeathIsTheRowWithThePlayerOnTheVictimSide()
    {
        var metadata = await new VideoService().ProbeAsync(video);
        var rows = Enumerable.Range(20, 19).ToDictionary(frame => frame, _ => ("Gegner1 BulletWaltz", 4));

        var death = await PotentialFrameFinder.FindNextAsync(FinderConfig(), metadata,
            afterSeconds: 0.5, () => new FakeOcr(rows), ownDeath: true);
        var kill = await PotentialFrameFinder.FindNextAsync(FinderConfig(), metadata,
            afterSeconds: 0.5, () => new FakeOcr(rows));

        Assert.Equal(2.0, death);
        Assert.Null(kill);
    }

    /// <summary>A killfeed entry that is already on screen at the start position is not a new event.</summary>
    [Fact]
    public async Task AnEntryVisibleAtTheStartPositionIsNotProposed()
    {
        var metadata = await new VideoService().ProbeAsync(video);

        var timestamp = await PotentialFrameFinder.FindNextAsync(FinderConfig(), metadata,
            afterSeconds: 2.0, () => new FakeOcr(VisibleFrom(20)));

        Assert.Null(timestamp);
    }

    [Fact]
    public async Task ADeathIsNotCountedAsAKill()
    {
        var (result, _, _) = await Run(Config(), new() { [20] = ("Gegner1 BulletWaltz", 4) });
        Assert.Empty(result.Events);
        Assert.Equal(0, result.Summary.KillsDetected);
    }

    [Fact]
    public async Task RepeatedRowsBecomeOneEvent()
    {
        var (result, _, _) = await Run(Config(), new()
        {
            [20] = ("BulletWaltz Gegner1", 4),
            [22] = ("BulletWaltz Gegner1", 4),
            [24] = ("BulletWaltz Gegner1", 4),
        });
        Assert.Single(result.Events);
        Assert.Equal(2.0, result.Events[0].TimestampSeconds);
    }

    [Fact]
    public async Task ParallelOcrKeepsTheOrderOfEvents()
    {
        var (result, _, _) = await Run(Config(workers: 4), new()
        {
            // Clearly different opponents; similar names would be merged by the deduplicator.
            [10] = ("BulletWaltz Alpha", 4),
            [30] = ("BulletWaltz Bravo", 4),
            [50] = ("BulletWaltz Charlie", 4),
        });
        Assert.Equal(["Alpha", "Bravo", "Charlie"], result.Events.Select(e => e.OpponentName));
        Assert.Equal([1.0, 3.0, 5.0], result.Events.Select(e => e.TimestampSeconds));
    }

    /// <summary>
    /// Consecutive sampled frames differ by 8 of 255 here, so OCR only runs once the difference
    /// against the last analysed crop has accumulated past the threshold.
    /// </summary>
    [Fact]
    public async Task ChangeDetectionReducesTheNumberOfOcrCalls()
    {
        var (result, _, ocr) = await Run(Config(changeDetection: true), []);
        Assert.Equal(30, result.Summary.FramesSampled);
        Assert.InRange(ocr.Calls, 1, 15);
        Assert.Equal(ocr.Calls, result.Summary.OcrCalls);
    }

    [Fact]
    public async Task SummaryDescribesTheRun()
    {
        var (_, output, _) = await Run(Config(), new() { [20] = ("BulletWaltz Gegner1", 4) });
        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "summary.json")));
        var data = summary.RootElement;
        Assert.Equal("match.mkv", data.GetProperty("source_video").GetString());
        Assert.Equal(6.0, data.GetProperty("duration_seconds").GetDouble(), 3);
        Assert.Equal(30, data.GetProperty("frames_sampled").GetInt32());
        Assert.Equal(1, data.GetProperty("kills_detected").GetInt32());
        Assert.Equal(0, data.GetProperty("clips_created").GetInt32());
        Assert.Equal(0, data.GetProperty("template_checks").GetInt32());
        Assert.False(data.GetProperty("interrupted").GetBoolean());
    }

    [Fact]
    public async Task CancellationKeepsTheEventsFoundSoFar()
    {
        using var cancel = new CancellationTokenSource();
        var output = Path.Combine(directory, "cancelled");
        var ocr = new FakeOcr(new() { [0] = ("BulletWaltz Gegner1", 4) });
        var progress = new Progress<AnalysisProgress>(update =>
        {
            if (update.TimestampSeconds >= 1.0) cancel.Cancel();
        });
        var result = await new AnalysisService(Config(), () => ocr)
            .RunAsync(video, output, progress, cancel.Token);
        Assert.True(result.Interrupted);
        Assert.Single(result.Events);
        Assert.True(result.Summary.Interrupted);
        Assert.True(File.Exists(Path.Combine(output, "checkpoint.json")));
        Assert.Single(Reports.ReadEventsJson(Path.Combine(output, "events.json")));
    }

    [Fact]
    public async Task ClipsAreOnlyWrittenWhenTheExportIsRequested()
    {
        var output = Path.Combine(directory, "with-clips");
        var ocr = new FakeOcr(new() { [20] = ("BulletWaltz Gegner1", 4) });
        var configuration = Config() with { Clips = Config().Clips with { Preset = "ultrafast" } };
        var reports = await new AnalysisService(configuration, () => ocr)
            .RunAsync(video, Path.Combine(directory, "no-clips"));
        Assert.Empty(reports.Clips);
        Assert.False(Directory.Exists(Path.Combine(directory, "no-clips", "clips")));

        var exported = await new AnalysisService(configuration, () => new FakeOcr(
            new() { [20] = ("BulletWaltz Gegner1", 4) })).RunAsync(video, output, exportClips: true);
        Assert.Single(exported.Clips);
        Assert.Equal(1, exported.Summary.ClipsCreated);
        Assert.Single(Directory.GetFiles(Path.Combine(output, "clips"), "*.mp4"));
    }

    [Fact]
    public async Task WithoutARegionTheBf6DefaultIsAnalysed()
    {
        var configuration = Config() with { Killfeed = new() };
        var result = await new AnalysisService(configuration, () => new FakeOcr([]))
            .RunAsync(video, Path.Combine(directory, "default-region"));
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task TemplateModeGroupsAMarkerBurstIntoOneEvent()
    {
        var marker = Path.Combine(directory, "marker.png");
        using (var image = new Mat(16, 16, MatType.CV_8UC1, Scalar.White))
        {
            image.Rectangle(new Rect(7, 0, 2, 16), Scalar.Black, -1);
            image.Rectangle(new Rect(0, 7, 16, 2), Scalar.Black, -1);
            File.WriteAllBytes(marker, image.ToBytes(".png"));
        }
        // The marker is overlaid on frames 20 to 26 only.
        var overlay = Path.Combine(directory, "marker.mkv");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=128x96:rate=10", "-i", marker, "-filter_complex",
            "[0:v][1:v]overlay=20:20:enable='between(n,20,26)'", "-t", "4", "-c:v", "ffv1", overlay],
            TimeSpan.FromSeconds(60));
        var configuration = Config() with
        {
            Detection = new()
            {
                Mode = "template", Region = new() { X = 0, Y = 0, Width = 128, Height = 96 },
                Templates = [new() { Path = marker, Label = "kill", Threshold = 0.8 }],
                GroupingGapSeconds = 0.8, MinConfirmations = 2,
            },
            Analysis = Config().Analysis with { SamplesPerSecond = 10 },
        };
        var result = await new AnalysisService(configuration, () => new FakeOcr([]))
            .RunAsync(overlay, Path.Combine(directory, "template-mode"));
        var kill = Assert.Single(result.Events);
        Assert.Equal("template", kill.DetectionMethod);
        Assert.Equal("kill", kill.EventType);
        Assert.Equal(2.0, kill.TimestampSeconds);
        Assert.Equal(40, result.Summary.TemplateChecks);
        Assert.Equal(0, result.Summary.OcrCalls);
    }

    [Fact]
    public async Task TemplateModeNeedsItsOwnRegion()
    {
        var configuration = Config() with
        {
            Detection = new()
            {
                Mode = "template",
                Templates = [new() { Path = Path.Combine(directory, "marker.png") }],
            },
        };
        Assert.Contains("Kein Erkennungsbereich", (await Assert.ThrowsAsync<ConfigurationException>(
            () => new AnalysisService(configuration, () => new FakeOcr([]))
                .RunAsync(video, Path.Combine(directory, "template-region")))).Message);
    }
}
