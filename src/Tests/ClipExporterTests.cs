using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class ClipNamingTests
{
    private static ClipSegment Segment(double start, double end, int kills, string type) =>
        new(start, end, [.. Enumerable.Range(0, kills).Select(index => new KillCandidate(
            start + index, "BulletWaltz", "BulletWaltz", null, null, "x", 0.9, 100, 0, "clip.mp4", 0))],
            type);

    [Theory]
    [InlineData("stream", "stream")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData(" trailing. ", "trailing")]
    [InlineData("con", "_con")]
    [InlineData("...", "clip")]
    public void IllegalAndReservedNamesAreReplaced(string input, string expected) =>
        Assert.Equal(expected, ClipNaming.Sanitize(input));

    [Theory]
    [InlineData(0, "00-00-00")]
    [InlineData(942.6, "00-15-42")]
    [InlineData(3771.9, "01-02-51")]
    public void TimestampsUseTheFileNameFormat(double seconds, string expected) =>
        Assert.Equal(expected, ClipNaming.TimestampForFileName(seconds));

    [Fact]
    public void ClipNamesFollowThePythonScheme()
    {
        Assert.Equal("stream_00-15-42_single-kill.mp4",
            ClipNaming.ClipFileName(@"D:\Streams\stream.mkv", Segment(942.6, 947.6, 1, "single_kill")));
        Assert.Equal("stream_01-02-51_multi-kill_4.mp4",
            ClipNaming.ClipFileName("stream.mkv", Segment(3771.9, 3781.9, 4, "multi_kill")));
        Assert.Equal("stream_00-00-10_double-kill.mp4",
            ClipNaming.ClipFileName("stream.mkv", Segment(10.0, 15.0, 2, "double_kill")));
    }

    [Fact]
    public void ExistingClipsAreNeverOverwritten()
    {
        var folder = Directory.CreateTempSubdirectory("bf6-naming-").FullName;
        try
        {
            Assert.Equal(Path.Combine(folder, "a.mp4"), ClipNaming.UniquePath(folder, "a.mp4"));
            File.WriteAllBytes(Path.Combine(folder, "a.mp4"), [1]);
            Assert.Equal(Path.Combine(folder, "a_2.mp4"), ClipNaming.UniquePath(folder, "a.mp4"));
            File.WriteAllBytes(Path.Combine(folder, "a_2.mp4"), [1]);
            Assert.Equal(Path.Combine(folder, "a_3.mp4"), ClipNaming.UniquePath(folder, "a.mp4"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AccurateModeReencodesWithAudio()
    {
        var command = ClipExporter.BuildArguments("in.mkv", Segment(97.0, 105.0, 2, "double_kill"),
            "out.mp4", new ClipSettings());
        Assert.Equal("97.000", command[Array.IndexOf(command, "-ss") + 1]);
        Assert.Equal("8.000", command[Array.IndexOf(command, "-t") + 1]);
        Assert.Equal("libx264", command[Array.IndexOf(command, "-c:v") + 1]);
        Assert.Equal("aac", command[Array.IndexOf(command, "-c:a") + 1]);
        Assert.Equal("out.mp4", command[^1]);
    }

    [Fact]
    public void FastModeUsesStreamCopy()
    {
        var command = ClipExporter.BuildArguments("in.mkv", Segment(97.0, 105.0, 1, "single_kill"),
            "out.mp4", new ClipSettings { ExportMode = "fast" });
        Assert.Equal("copy", command[Array.IndexOf(command, "-c") + 1]);
        Assert.DoesNotContain("-crf", command);
    }

    [Fact]
    public void HardwareEncodersArePassedThrough()
    {
        var command = ClipExporter.BuildArguments("in.mkv", Segment(97.0, 105.0, 1, "single_kill"),
            "out.mp4", new ClipSettings { VideoCodec = "h264_nvenc", Preset = "p5" });
        Assert.Equal("h264_nvenc", command[Array.IndexOf(command, "-c:v") + 1]);
        Assert.Equal("p5", command[Array.IndexOf(command, "-preset") + 1]);
    }
}

public sealed class ClipExporterTests : IAsyncLifetime
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "bf6-export-" + Guid.NewGuid().ToString("N"));
    private string video = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        video = Path.Combine(directory, "match.mp4");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error",
            "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=30", "-f", "lavfi", "-i",
            "sine=frequency=440:sample_rate=48000", "-t", "10", "-c:v", "libx264", "-preset",
            "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video],
            TimeSpan.FromSeconds(120));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static ClipSegment Segment(double start, double end, string type = "single_kill") =>
        new(start, end, [new(start + 1, "BulletWaltz", "BulletWaltz", "Gegner", null, "x", 0.9, 100,
            0, "match.mp4", 0)], type);

    private string Clips => Path.Combine(directory, "clips");

    [Theory]
    [InlineData("accurate")]
    [InlineData("fast")]
    public async Task ClipsArePlayableAndKeepTheirAudio(string mode)
    {
        var result = await new ClipExporter(new ClipSettings { ExportMode = mode, Preset = "ultrafast" })
            .ExportAsync(video, [Segment(2.0, 5.0)], Path.Combine(Clips, mode));
        var clip = Assert.Single(result.Written);
        Assert.Empty(result.Failures);
        var exported = await new VideoService().ProbeAsync(clip);
        Assert.NotNull(exported.AudioCodec);
        Assert.InRange(exported.DurationSeconds, 2.5, 3.5);
        Assert.Equal("match_00-00-03_single-kill.mp4", Path.GetFileName(clip));
    }

    [Fact]
    public async Task ASelectionWithCorrectedBoundsIsExportedAsGiven()
    {
        var segments = new[] { Segment(1.0, 9.0), Segment(2.0, 4.0) };
        // Only the second candidate, and its start moved by half a second.
        var selected = segments[1] with { StartSeconds = 2.5 };
        var result = await new ClipExporter(new ClipSettings { Preset = "ultrafast" })
            .ExportAsync(video, [selected], Clips);
        var exported = await new VideoService().ProbeAsync(Assert.Single(result.Written));
        Assert.InRange(exported.DurationSeconds, 1.0, 2.0);
    }

    [Fact]
    public async Task ASecondExportDoesNotOverwriteTheFirstClip()
    {
        var exporter = new ClipExporter(new ClipSettings { Preset = "ultrafast" });
        var first = await exporter.ExportAsync(video, [Segment(2.0, 4.0)], Clips);
        var second = await exporter.ExportAsync(video, [Segment(2.0, 4.0)], Clips);
        Assert.EndsWith("_single-kill.mp4", first.Written[0]);
        Assert.EndsWith("_single-kill_2.mp4", second.Written[0]);
        Assert.Equal(2, Directory.GetFiles(Clips, "*.mp4").Length);
    }

    [Fact]
    public async Task BoundsOutsideTheVideoAreRejectedBeforeAnythingIsWritten()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ClipExporter(new ClipSettings())
            .ExportAsync(video, [Segment(2.0, 4.0), Segment(9.0, 30.0)], Clips));
        Assert.False(Directory.Exists(Clips));
    }

    [Fact]
    public async Task AFailedClipLeavesNoFileBehind()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => new ClipExporter(
            new ClipSettings { VideoCodec = "kein_codec" }).ExportAsync(video, [Segment(2.0, 4.0)], Clips));
        Assert.Contains("Kein Clip", error.Message);
        Assert.Empty(Directory.GetFiles(Clips));
    }

    [Fact]
    public async Task OneFailedClipDoesNotStopTheOthers()
    {
        var exporter = new ClipExporter(new ClipSettings { Preset = "ultrafast" });
        Directory.CreateDirectory(Clips);
        // A directory blocks exactly the name the second segment would use.
        Directory.CreateDirectory(Path.Combine(Clips, "match_00-00-06_single-kill.mp4"));
        var result = await exporter.ExportAsync(video, [Segment(2.0, 4.0), Segment(5.0, 7.0)], Clips);
        Assert.Single(result.Written);
        Assert.Single(result.Failures);
        Assert.Contains("match_00-00-06_single-kill.mp4", result.Failures[0]);
    }

    [Fact]
    public async Task NoSegmentsMeansNoClips()
    {
        var result = await new ClipExporter(new ClipSettings()).ExportAsync(video, [], Clips);
        Assert.Empty(result.Written);
        Assert.Empty(result.Failures);
    }
}
