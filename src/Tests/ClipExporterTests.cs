using System.Globalization;
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
    public void ClipNamesFollowTheScheme()
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
    public void ClipsAreStreamCopied()
    {
        var command = ClipExporter.BuildArguments("in.mkv", 97.0, 105.0, "out.mp4");
        Assert.Equal(97.001, double.Parse(command[Array.IndexOf(command, "-ss") + 1],
            CultureInfo.InvariantCulture), 6);
        Assert.Equal(7.999, double.Parse(command[Array.IndexOf(command, "-t") + 1],
            CultureInfo.InvariantCulture), 6);
        Assert.Equal("copy", command[Array.IndexOf(command, "-c") + 1]);
        Assert.Equal("out.mp4", command[^1]);
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
            "ultrafast", "-g", "30", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video],
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

    [Fact]
    public async Task ClipsArePlayableAndKeepTheirAudio()
    {
        var result = await new ClipExporter().ExportAsync(video, [Segment(2.0, 5.0)], Clips);
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
        // Only the second candidate, and its start moved by a keyframe interval.
        var selected = segments[1] with { StartSeconds = 3.0 };
        var result = await new ClipExporter().ExportAsync(video, [selected], Clips);
        var exported = await new VideoService().ProbeAsync(Assert.Single(result.Written));
        Assert.InRange(exported.DurationSeconds, 0.95, 1.05);
    }

    [Fact]
    public async Task BoundsBetweenKeyframesWidenToTheSurroundingKeyframes()
    {
        var result = await new ClipExporter().ExportAsync(video, [Segment(2.4, 4.6)], Clips);
        var exported = await new VideoService().ProbeAsync(Assert.Single(result.Written));
        Assert.InRange(exported.DurationSeconds, 2.95, 3.05);
    }

    [Fact]
    public async Task ASecondExportDoesNotOverwriteTheFirstClip()
    {
        var exporter = new ClipExporter();
        var first = await exporter.ExportAsync(video, [Segment(2.0, 4.0)], Clips);
        var second = await exporter.ExportAsync(video, [Segment(2.0, 4.0)], Clips);
        Assert.EndsWith("_single-kill.mp4", first.Written[0]);
        Assert.EndsWith("_single-kill_2.mp4", second.Written[0]);
        Assert.Equal(2, Directory.GetFiles(Clips, "*.mp4").Length);
    }

    [Fact]
    public async Task BoundsOutsideTheVideoAreRejectedBeforeAnythingIsWritten()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ClipExporter()
            .ExportAsync(video, [Segment(2.0, 4.0), Segment(9.0, 30.0)], Clips));
        Assert.False(Directory.Exists(Clips));
    }

    [Fact]
    public async Task AFailedClipLeavesNoFileBehind()
    {
        // A directory blocks the only clip's name, so FFmpeg's output cannot be published.
        Directory.CreateDirectory(Path.Combine(Clips, "match_00-00-03_single-kill.mp4"));
        var error = await Assert.ThrowsAsync<IOException>(() => new ClipExporter()
            .ExportAsync(video, [Segment(2.0, 4.0)], Clips));
        Assert.Contains("Kein Clip", error.Message);
        Assert.Empty(Directory.GetFiles(Clips));
    }

    [Fact]
    public async Task OneFailedClipDoesNotStopTheOthers()
    {
        var exporter = new ClipExporter();
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
        var result = await new ClipExporter().ExportAsync(video, [], Clips);
        Assert.Empty(result.Written);
        Assert.Empty(result.Failures);
    }
}
