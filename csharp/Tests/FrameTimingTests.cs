using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class FrameTimingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bf6-timing-" + Guid.NewGuid().ToString("N"));
    public FrameTimingTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData(false, 0.11, 2)]
    [InlineData(false, 0.2, 2)]
    [InlineData(true, 0.1, 1)]
    [InlineData(true, 0.21, 2)]
    public async Task SeekReturnsFirstFrameAtOrAfterRequestedTime(bool variable, double time, int expectedIndex)
    {
        var video = Path.Combine(directory, "sequence.mkv");
        if (variable)
        {
            foreach (var (name, color) in new[] { ("red", Scalar.Red), ("green", Scalar.Green), ("blue", Scalar.Blue) })
            {
                using var image = new Mat(48, 64, MatType.CV_8UC3, color);
                image.SaveImage(Path.Combine(directory, name + ".png"));
            }
            var list = Path.Combine(directory, "frames.txt");
            await File.WriteAllTextAsync(list, "file red.png\nduration 0.2\nfile green.png\nduration 0.4\nfile blue.png\nduration 0.4\nfile blue.png\n");
            await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "concat", "-safe", "0",
                "-i", list, "-fps_mode", "vfr", "-c:v", "ffv1", video], TimeSpan.FromSeconds(30));
        }
        else
            await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
                "testsrc2=size=64x48:rate=10", "-t", "1", "-c:v", "ffv1", video], TimeSpan.FromSeconds(30));
        var expected = Path.Combine(directory, "expected.png");
        var actual = Path.Combine(directory, "actual.png");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-i", video,
            "-vf", $"select=eq(n\\,{expectedIndex})", "-fps_mode", "vfr", "-frames:v", "1", expected],
            TimeSpan.FromSeconds(30));
        await new VideoService().FrameAsync(video, time, actual);
        using var expectedImage = Cv2.ImRead(expected);
        using var actualImage = Cv2.ImRead(actual);
        Assert.Equal(0, Cv2.Norm(expectedImage, actualImage, NormTypes.L1));
        var diagnostic = await CodecDiagnostics.CompareAsync(video, time);
        Assert.True(diagnostic.OpenCvOpened);
        // OpenCV's VFR seek may fail; FFmpeg's pixel-exact result above is the contract.
        if (!variable) Assert.True(diagnostic.OpenCvRead);
    }
}
