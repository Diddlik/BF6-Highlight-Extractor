using OpenCvSharp;

namespace Bf6Highlights;

public sealed record FrameComparison(bool OpenCvOpened, bool OpenCvRead, double RequestedSeconds,
    double? OpenCvTimestampSeconds, double? MeanAbsolutePixelDifference, string? Error);

public static class CodecDiagnostics
{
    public static async Task<FrameComparison> CompareAsync(string source, double timestamp,
        CancellationToken token = default)
    {
        var reference = Path.Combine(Path.GetTempPath(), $"bf6-frame-{Guid.NewGuid():N}.png");
        try
        {
            await new VideoService().FrameAsync(source, timestamp, reference, token);
            using var capture = new VideoCapture(source, VideoCaptureAPIs.FFMPEG);
            if (!capture.IsOpened()) return new(false, false, timestamp, null, null, "Codec nicht geöffnet.");
            capture.Set(VideoCaptureProperties.PosMsec, timestamp * 1000);
            using var actual = new Mat();
            if (!capture.Read(actual) || actual.Empty())
                return new(true, false, timestamp, null, null, "Kein Frame dekodiert.");
            using var expected = Cv2.ImRead(reference);
            var difference = actual.Size() == expected.Size()
                ? Cv2.Norm(actual, expected, NormTypes.L1) / (actual.Total() * actual.Channels())
                : (double?)null;
            return new(true, true, timestamp,
                capture.Get(VideoCaptureProperties.PosMsec) / 1000, difference, null);
        }
        catch (OpenCVException exception)
        {
            return new(false, false, timestamp, null, null, exception.Message);
        }
        finally { File.Delete(reference); }
    }
}
