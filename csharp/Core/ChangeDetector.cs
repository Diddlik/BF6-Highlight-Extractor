using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>
/// Stage one of the pipeline (Python vision/change_detection.py): the fraction of pixels that
/// changed against the previously analysed crop, so OCR only runs on new killfeed content.
/// </summary>
public sealed class ChangeDetector : IDisposable
{
    private const int WorkWidth = 160;
    private const int PixelDelta = 25;

    private readonly double threshold;
    private Mat? previous;

    public ChangeDetector(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentException("Ungültiger Schwellwert für die Änderungserkennung.");
        this.threshold = threshold;
    }

    public (bool Changed, double Ratio) Changed(Mat crop)
    {
        if (crop.Empty()) throw new ArgumentException("Leerer Bildausschnitt.");
        var current = Signature(crop);
        if (previous is null || previous.Size() != current.Size())
        {
            previous?.Dispose();
            previous = current;
            return (true, 1.0);
        }
        using var difference = new Mat();
        Cv2.Absdiff(previous, current, difference);
        using var above = new Mat();
        Cv2.Threshold(difference, above, PixelDelta, 1, ThresholdTypes.Binary);
        var ratio = Cv2.CountNonZero(above) / (double)(difference.Rows * difference.Cols);
        if (ratio < threshold)
        {
            current.Dispose();
            return (false, ratio);
        }
        previous.Dispose();
        previous = current;
        return (true, ratio);
    }

    private static Mat Signature(Mat crop)
    {
        var gray = new Mat();
        if (crop.Channels() == 3) Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        else crop.CopyTo(gray);
        if (gray.Cols > WorkWidth)
        {
            var scaled = new Mat();
            Cv2.Resize(gray, scaled,
                new Size(WorkWidth, Math.Max(1, (int)((double)WorkWidth / gray.Cols * gray.Rows))),
                interpolation: InterpolationFlags.Area);
            gray.Dispose();
            gray = scaled;
        }
        var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        gray.Dispose();
        return blurred;
    }

    public void Dispose() => previous?.Dispose();

    /// <summary>Frame distance between two analysed frames, e.g. 60 fps at 3/s gives every 20th.</summary>
    public static int SampleStep(double fps, int samplesPerSecond)
    {
        if (!double.IsFinite(fps) || fps <= 0) throw new ArgumentException("Ungültige Bildrate.");
        return Math.Max(1, (int)Math.Round(fps / Math.Max(1, samplesPerSecond), MidpointRounding.ToEven));
    }
}
