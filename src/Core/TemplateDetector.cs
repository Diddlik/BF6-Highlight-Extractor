using OpenCvSharp;

namespace Bf6Highlights;

public sealed record TemplateMatch(string Label, double Confidence, double TimestampSeconds,
    int FrameNumber, PixelRegion BoundingBox);

/// <summary>
/// Personal kill-confirmation detection with OpenCV template matching
/// (Python detection/template_detector.py).
/// </summary>
public sealed class TemplateMatcher : IDisposable
{
    private sealed record Loaded(string Label, double Threshold, Mat Image, bool EdgeDetection);

    private readonly List<Loaded> templates = [];

    public TemplateMatcher(DetectionModeSettings detection, int sourceWidth)
    {
        if (sourceWidth <= 0) throw new ArgumentException("Ungültige Videobreite.");
        try
        {
            foreach (var specification in detection.Templates)
            {
                var image = ReadGrayscale(specification.Path);
                if (specification.ReferenceWidth is { } reference)
                {
                    var scale = (double)sourceWidth / reference;
                    var scaled = new Mat();
                    Cv2.Resize(image, scaled,
                        new Size(Math.Max(1, (int)Math.Round(image.Cols * scale, MidpointRounding.ToEven)),
                            Math.Max(1, (int)Math.Round(image.Rows * scale, MidpointRounding.ToEven))),
                        interpolation: scale < 1.0 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
                    image.Dispose();
                    image = scaled;
                }
                if (specification.EdgeDetection)
                {
                    var edges = new Mat();
                    Cv2.Canny(image, edges, 80, 160);
                    image.Dispose();
                    image = edges;
                }
                templates.Add(new(specification.Label, specification.Threshold, image,
                    specification.EdgeDetection));
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>The best template above its threshold, or null when nothing matches.</summary>
    public TemplateMatch? Match(Mat image, double timestamp, int frameNumber)
    {
        using var gray = new Mat();
        if (image.Channels() == 3) Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else image.CopyTo(gray);
        using var edges = new Mat();
        TemplateMatch? best = null;
        foreach (var template in templates)
        {
            if (template.Image.Rows > gray.Rows || template.Image.Cols > gray.Cols)
                throw new ConfigurationException(
                    $"Vorlage '{template.Label}' ({template.Image.Cols}x{template.Image.Rows}) ist größer "
                    + $"als der Erkennungsbereich ({gray.Cols}x{gray.Rows}).");
            if (template.EdgeDetection && edges.Empty()) Cv2.Canny(gray, edges, 80, 160);
            using var result = new Mat();
            Cv2.MatchTemplate(template.EdgeDetection ? edges : gray, template.Image, result,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out double _, out double confidence, out _, out Point location);
            // A constant region and a constant template leave the correlation undefined.
            if (!double.IsFinite(confidence) || confidence < template.Threshold) continue;
            if (best is null || confidence > best.Confidence)
                best = new(template.Label, confidence, timestamp, frameNumber,
                    new(location.X, location.Y, template.Image.Cols, template.Image.Rows));
        }
        return best;
    }

    private static Mat ReadGrayscale(string path)
    {
        if (!File.Exists(path)) throw new ConfigurationException("Vorlagenbild nicht gefunden: " + path);
        // Read with .NET: OpenCV's file access mangles non-ASCII paths on Windows.
        var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
        if (image.Empty())
        {
            image.Dispose();
            throw new ConfigurationException("Vorlagenbild konnte nicht gelesen werden: " + path);
        }
        return image;
    }

    public void Dispose()
    {
        foreach (var template in templates) template.Image.Dispose();
        templates.Clear();
    }
}

/// <summary>Turns a burst of matching sampled frames into one confirmed kill event.</summary>
public sealed class TemplateEventGrouper(DetectionModeSettings detection, string sourceVideo)
{
    private readonly List<TemplateMatch> matches = [];

    public KillCandidate? Feed(TemplateMatch? match, double now)
    {
        if (match is not null)
        {
            KillCandidate? flushed = null;
            if (matches.Count > 0
                && match.TimestampSeconds - matches[^1].TimestampSeconds > detection.GroupingGapSeconds)
                flushed = Flush();
            matches.Add(match);
            return flushed;
        }
        return matches.Count > 0 && now - matches[^1].TimestampSeconds > detection.GroupingGapSeconds
            ? Flush() : null;
    }

    public KillCandidate? Finish() => Flush();

    private KillCandidate? Flush()
    {
        if (matches.Count < detection.MinConfirmations)
        {
            matches.Clear();
            return null;
        }
        var best = matches.MaxBy(item => item.Confidence)!;
        var confidence = matches.Average(item => item.Confidence);
        var first = matches[0];
        matches.Clear();
        return new(first.TimestampSeconds, "", "", null, null, best.Label, confidence,
            confidence * 100.0, first.FrameNumber, sourceVideo, best.BoundingBox.Y,
            best.Label, "template");
    }
}
