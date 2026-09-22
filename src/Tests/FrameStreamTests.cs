using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class FrameStreamTests : IAsyncLifetime
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "bf6-stream-" + Guid.NewGuid().ToString("N"));
    private string video = "";
    private VideoMetadata metadata = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        // 10 fps, 2 s, lossless: every frame has different content, so a frame is identifiable.
        video = Path.Combine(directory, "counter.mkv");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=64x48:rate=10", "-t", "2", "-c:v", "ffv1", video], TimeSpan.FromSeconds(60));
        metadata = await new VideoService().ProbeAsync(video);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<List<(int Number, double Timestamp)>> Read(PixelRegion region,
        int samplesPerSecond, CancellationToken token = default)
    {
        var frames = new List<(int, double)>();
        await foreach (var frame in FrameStream.ReadAsync(metadata, region, samplesPerSecond, token))
            using (frame)
            {
                Assert.Equal(region.Width, frame.Image.Cols);
                Assert.Equal(region.Height, frame.Image.Rows);
                frames.Add((frame.Number, frame.TimestampSeconds));
            }
        return frames;
    }

    /// <summary>The frame FFmpeg writes for that index, cropped to the region.</summary>
    private async Task<Mat> Reference(int index, PixelRegion region)
    {
        var file = Path.Combine(directory, $"reference-{index}.png");
        if (!File.Exists(file))
            await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-i", video, "-vf",
                $"select=eq(n\\,{index})", "-fps_mode", "vfr", "-frames:v", "1", file],
                TimeSpan.FromSeconds(30));
        using var full = Cv2.ImRead(file);
        return new Mat(full, new Rect(region.X, region.Y, region.Width, region.Height)).Clone();
    }

    private async Task AssertFramesMatchTheirSource(PixelRegion region, int samplesPerSecond, int[] expected)
    {
        var numbers = new List<int>();
        await foreach (var frame in FrameStream.ReadAsync(metadata, region, samplesPerSecond))
            using (frame)
            {
                using var reference = await Reference(frame.Number, region);
                using var difference = new Mat();
                Cv2.Absdiff(reference, frame.Image, difference);
                Cv2.MinMaxLoc(difference.Reshape(1), out double _, out double largest);
                Assert.True(largest <= 1, $"Frame {frame.Number} weicht um {largest} vom "
                    + "FFmpeg-Referenzbild ab.");
                numbers.Add(frame.Number);
            }
        Assert.Equal(expected, numbers);
    }

    [Fact]
    public async Task EveryStepFrameArrivesWithItsNumberAndTime()
    {
        Assert.Equal(10, metadata.Fps);
        var frames = await Read(new(0, 0, 64, 48), samplesPerSecond: 5);
        Assert.Equal([0, 2, 4, 6, 8, 10, 12, 14, 16, 18], frames.Select(f => f.Number));
        Assert.Equal([0.0, 0.2, 0.4, 0.6, 0.8, 1.0, 1.2, 1.4, 1.6, 1.8], frames.Select(f => f.Timestamp));
    }

    [Fact]
    public Task SampledFramesCarryTheContentOfTheirOwnFrame() =>
        AssertFramesMatchTheirSource(new(0, 0, 64, 48), samplesPerSecond: 2, [0, 5, 10, 15]);

    [Fact]
    public Task TheRegionIsCroppedByFfmpeg() =>
        AssertFramesMatchTheirSource(new(16, 8, 32, 24), samplesPerSecond: 1, [0, 10]);

    /// <summary>A subsampled source must not shrink an odd region; crop runs with exact=1.</summary>
    [Fact]
    public async Task OddRegionsOnSubsampledVideoStayComplete()
    {
        var subsampled = Path.Combine(directory, "subsampled.mkv");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=128x96:rate=10", "-t", "1", "-c:v", "ffv1", "-pix_fmt", "yuv420p", subsampled],
            TimeSpan.FromSeconds(60));
        var meta = await new VideoService().ProbeAsync(subsampled);
        var frames = 0;
        await foreach (var frame in FrameStream.ReadAsync(meta, new(5, 3, 33, 21), 5))
            using (frame)
            {
                Assert.Equal(33, frame.Image.Cols);
                Assert.Equal(21, frame.Image.Rows);
                frames++;
            }
        Assert.Equal(5, frames);
    }

    [Fact]
    public async Task AStepAndAStopPositionLimitTheStream()
    {
        var frames = new List<(int Number, double Timestamp)>();
        await foreach (var frame in FrameStream.ReadEveryAsync(metadata, new(0, 0, 64, 48), step: 5,
                           startSeconds: 0.4, stopSeconds: 1.2))
            using (frame)
                frames.Add((frame.Number, frame.TimestampSeconds));

        Assert.Equal([(5, 0.5), (10, 1.0)], frames);
    }

    [Fact]
    public async Task KeyframesAreStreamedWithTheirRealTimestamps()
    {
        var keyed = Path.Combine(directory, "keyed.mp4");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=64x48:rate=10", "-t", "3", "-c:v", "libx264", "-g", "10",
            "-pix_fmt", "yuv420p", keyed], TimeSpan.FromSeconds(60));
        var service = new VideoService();
        var keyframes = (await service.KeyframesAsync(keyed)).Select(Round).ToArray();

        var streamed = new List<double>();
        var sought = new List<double>();
        var probed = await service.ProbeAsync(keyed);
        await foreach (var frame in FrameStream.ReadKeyframesAsync(probed, new(0, 0, 64, 48)))
            using (frame)
                streamed.Add(Round(frame.TimestampSeconds));
        await foreach (var frame in FrameStream.ReadKeyframesAsync(probed, new(0, 0, 64, 48),
                           startSeconds: 0.5))
            using (frame)
                sought.Add(Round(frame.TimestampSeconds));

        Assert.Equal([0.0, 1.0, 2.0], streamed);
        // A seek must not rebase the timestamps to zero.
        Assert.Equal([1.0, 2.0], sought);
        // ffprobe sometimes answers an interval read with nothing; the search then simply works
        // without the keyframe positions, so only the values themselves are checked here.
        Assert.All(keyframes, position => Assert.Contains(position, streamed));
        Assert.Equal(keyframes, keyframes.Order());
    }

    private static double Round(double seconds) => Math.Round(seconds, 3);

    [Fact]
    public async Task RegionsOutsideTheVideoAreRejected() =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Read(new(0, 0, 65, 48), samplesPerSecond: 1));

    [Fact]
    public async Task CancellationStopsTheStreamAndTheProcess()
    {
        using var cancel = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var frame in FrameStream.ReadAsync(metadata, new(0, 0, 64, 48), 10, cancel.Token))
                using (frame)
                {
                    seen++;
                    cancel.Cancel();
                }
        });
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task AMissingFileIsReported()
    {
        var missing = metadata with { Path = Path.Combine(directory, "absent.mkv") };
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var frame in FrameStream.ReadAsync(missing, new(0, 0, 64, 48), 1)) frame.Dispose();
        });
    }
}
