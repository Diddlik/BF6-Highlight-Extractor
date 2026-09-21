using OpenCvSharp;

namespace Bf6Highlights;

public sealed record FrameInspection(PixelRegion Region, double TimestampSeconds, int FrameNumber,
    string CropPath, IReadOnlyList<OcrLine> Lines, IReadOnlyList<KillCandidate> Candidates,
    IReadOnlyList<DetectionRejection> Rejections, TemplateMatch? Template);

/// <summary>
/// Runs the configured detection on a single frame so region and thresholds can be checked
/// (Python cli.py inspect-frame). The crop is saved for a visual check.
/// </summary>
public static class FrameInspector
{
    public static async Task<FrameInspection> InspectAsync(Configuration configuration, string source,
        double timestamp, string outputDirectory, Func<IOcrEngine> ocrEngineFactory,
        CancellationToken token = default)
    {
        var video = await new VideoService().ProbeAsync(source, token);
        var templateMode = configuration.Detection.Mode == "template";
        var region = (templateMode
            ? configuration.ResolveDetectionRegion(video.Width, video.Height)
            : configuration.ResolveKillfeedRegion(video.Width, video.Height)).ToPixelRegion();
        using var crop = await CropAsync(video, timestamp, region, token);
        var directory = Path.Combine(outputDirectory, "inspect");
        Directory.CreateDirectory(directory);
        var cropPath = ClipNaming.UniquePath(directory,
            $"{ClipNaming.TimestampForFileName(timestamp)}.png");
        await File.WriteAllBytesAsync(cropPath, crop.ToBytes(".png"), token);
        var frameNumber = (int)(timestamp * video.Fps);

        if (templateMode)
        {
            using var matcher = new TemplateMatcher(configuration.Detection, video.Width);
            return new(region, timestamp, frameNumber, cropPath, [], [], [],
                matcher.Match(crop, timestamp, frameNumber));
        }
        using var engine = ocrEngineFactory();
        var lines = await engine.ReadAsync(crop, region, token);
        var result = new KillfeedDetector(configuration.ToDetectionSettings())
            .Detect(lines, region, timestamp, frameNumber, Path.GetFileName(video.Path));
        return new(region, timestamp, frameNumber, cropPath, lines, result.Candidates,
            result.Rejections, null);
    }

    /// <summary>The configured region of the frame at that timestamp, decoded through FFmpeg.</summary>
    public static async Task<Mat> CropAsync(VideoMetadata video, double timestamp, PixelRegion region,
        CancellationToken token = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"bf6-inspect-{Guid.NewGuid():N}.png");
        try
        {
            await new VideoService().FrameAsync(video.Path, timestamp, temporary, token);
            using var frame = Cv2.ImDecode(await File.ReadAllBytesAsync(temporary, token),
                ImreadModes.Color);
            if (frame.Empty()) throw new IOException("Frame konnte nicht gelesen werden.");
            if (region.X + region.Width > frame.Width || region.Y + region.Height > frame.Height)
                throw new ConfigurationException("Der Bereich liegt außerhalb des Videos.");
            return new Mat(frame, new Rect(region.X, region.Y, region.Width, region.Height)).Clone();
        }
        finally { File.Delete(temporary); }
    }
}
