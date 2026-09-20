using System.Text.Json;
using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class VideoTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bf6-tests-" + Guid.NewGuid().ToString("N"));
    private readonly VideoService service = new();
    private string Source => Path.Combine(directory, "Video mit Ümlaut.mp4");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        await MediaProcess.RunAsync("ffmpeg",
            ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=30",
             "-f", "lavfi", "-i", "sine=frequency=440", "-t", "3", "-c:v", "libx264",
             "-c:a", "aac", Source], TimeSpan.FromSeconds(30));
    }

    public Task DisposeAsync() { Directory.Delete(directory, recursive: true); return Task.CompletedTask; }

    [Fact]
    public async Task ProbeFrameAndClipRetainAudioAndTiming()
    {
        var metadata = await service.ProbeAsync(Source);
        Assert.Equal(160, metadata.Width);
        Assert.Equal(30, metadata.Fps);
        Assert.Equal("aac", metadata.AudioCodec);
        var frame = Path.Combine(directory, "Frame ä.png");
        await service.FrameAsync(Source, 1, frame);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, File.ReadAllBytes(frame)[..4]);
        var clip = Path.Combine(directory, "Clip ü.mp4");
        await service.ClipAsync(Source, 0.5, 1.5, clip);
        var exported = await service.ProbeAsync(clip);
        Assert.Equal("aac", exported.AudioCodec);
        Assert.InRange(exported.DurationSeconds, 1 - 1d / 30, 1 + 1d / 30);
    }

    [Fact]
    public async Task SilentVideoExportsWithoutInventingAudio()
    {
        var silent = Path.Combine(directory, "silent.mp4");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-i", Source, "-an", "-c:v", "copy", silent],
            TimeSpan.FromSeconds(30));
        var clip = Path.Combine(directory, "silent-clip.mp4");
        await service.ClipAsync(silent, 0, 1, clip);
        Assert.Null((await service.ProbeAsync(clip)).AudioCodec);
    }

    [Fact]
    public async Task ExistingFilesAndSourceAreNeverOverwritten()
    {
        var before = await File.ReadAllBytesAsync(Source);
        await Assert.ThrowsAsync<IOException>(() => service.ClipAsync(Source, 0, 1, Source));
        Assert.Equal(before, await File.ReadAllBytesAsync(Source));
        var output = Path.Combine(directory, "existing.mp4");
        await File.WriteAllTextAsync(output, "keep");
        await Assert.ThrowsAsync<IOException>(() => service.ClipAsync(Source, 0, 1, output));
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(directory, ".bf6-*"));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 20)]
    [InlineData(double.NaN, 1)]
    public async Task InvalidRangesProduceNoClip(double start, double end)
    {
        var output = Path.Combine(directory, "invalid.mp4");
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClipAsync(Source, start, end, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task MissingAndCorruptInputsAreReported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ProbeAsync("missing-" + Guid.NewGuid()));
        var corrupt = Path.Combine(directory, "bad.mp4");
        await File.WriteAllTextAsync(corrupt, "not a video");
        await Assert.ThrowsAsync<IOException>(() => service.ProbeAsync(corrupt));
    }

    [Fact]
    public async Task SampleContainsUnreviewedProvenanceAndRefusesOverwrite()
    {
        var target = Path.Combine(directory, "sample");
        await SampleExporter.ExportAsync(Source, 0.5, 1.5, 1, "own_death", target);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "sample.json")));
        Assert.Equal("needs_review", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("expected_events").ValueKind);
        Assert.Equal(64, manifest.RootElement.GetProperty("source_sha256").GetString()!.Length);
        Assert.Equal(0.5, manifest.RootElement.GetProperty("frame_in_clip_seconds").GetDouble());
        Assert.True(File.Exists(Path.Combine(target, "clip.mp4")));
        Assert.True(File.Exists(Path.Combine(target, "frame.png")));
        await Assert.ThrowsAsync<IOException>(() => SampleExporter.ExportAsync(Source, 0, 1, 0.5, "own_kill", target));
    }

    [Fact]
    public async Task FailedSampleLeavesNoFinalOrTemporaryFolder()
    {
        var target = Path.Combine(directory, "failed");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SampleExporter.ExportAsync(Source, 0, 100, 1, "own_kill", target));
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(directory, ".bf6-sample-*"));
    }

    [Fact]
    public async Task CancellationAndTimeoutStopRunningProcess()
    {
        string[] args = ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i",
            "testsrc2=size=160x120:rate=30", "-t", "60", "-f", "null", "-"];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaProcess.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(10), cancellation.Token));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            MediaProcess.RunAsync("ffmpeg", args, TimeSpan.FromMilliseconds(500)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var target = Path.Combine(directory, "cancelled.mp4");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ClipAsync(Source, 0, 1, target, cancelled.Token));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task CancellingAnActiveExportRemovesOnlyItsTemporaryOutput()
    {
        var longSource = Path.Combine(directory, "long.mp4");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-stream_loop", "19", "-i", Source,
            "-c", "copy", longSource], TimeSpan.FromSeconds(30));
        var target = Path.Combine(directory, "cancel-active.mp4");
        var unrelated = Path.Combine(directory, "keep.mp4");
        await File.WriteAllTextAsync(unrelated, "keep");
        using var cancellation = new CancellationTokenSource();
        var export = service.ClipAsync(longSource, 0, 59, target, cancellation.Token);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!export.IsCompleted && Directory.GetFiles(directory, ".bf6-*").Length == 0
               && DateTime.UtcNow < until)
            await Task.Delay(1);
        Assert.NotEmpty(Directory.GetFiles(directory, ".bf6-*"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(directory, ".bf6-*"));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task BothPipesAreDrainedUnderHeavyOutput()
    {
        var result = await MediaProcess.RunAsync("ffmpeg", ["-loglevel", "debug", "-i", Source,
            "-f", "framecrc", "-"], TimeSpan.FromSeconds(30));
        Assert.Contains("#dimensions", result);
    }
}
