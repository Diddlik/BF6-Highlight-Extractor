using System.Globalization;
using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>One reviewed sample together with the OCR result of its frame.</summary>
public sealed record PersonalCalibrationCase(PersonalTrainingSample Sample, PixelRegion Region,
    IReadOnlyList<OcrLine> Lines);

/// <summary>How well a configuration decided on a set of samples.</summary>
public sealed record PersonalMetrics(int TruePositives, int FalsePositives, int FalseNegatives,
    int TrueNegatives)
{
    public int Cases => TruePositives + FalsePositives + FalseNegatives + TrueNegatives;
    public double Precision => TruePositives + FalsePositives == 0
        ? 0 : (double)TruePositives / (TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0
        ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public string Summary => $"{Cases} Fälle · Präzision {Precision:P0} · Trefferquote {Recall:P0} "
        + $"· F1 {F1:0.00}";
}

/// <summary>
/// A calibration of the existing detection on the user's own material. It changes thresholds,
/// never the shipped OCR models, and is stored as a small versioned file.
/// </summary>
public sealed record PersonalProfile
{
    public const int FormatVersion = 1;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int Format { get; init; } = FormatVersion;
    public required string ApplicationVersion { get; init; }
    public required string OcrModels { get; init; }

    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required RegionSettings Region { get; init; }
    public required string Mode { get; init; }

    // Calibrated values.
    public required double MinimumConfidence { get; init; }
    public required double NameThreshold { get; init; }

    public required PersonalMetrics DevelopmentMetrics { get; init; }
    public required PersonalMetrics BaselineDevelopmentMetrics { get; init; }
    public required PersonalMetrics HoldoutMetrics { get; init; }
    public required PersonalMetrics BaselineHoldoutMetrics { get; init; }
    public required IReadOnlyDictionary<string, int> DevelopmentLabels { get; init; }
    public required IReadOnlyDictionary<string, int> HoldoutLabels { get; init; }
    public required IReadOnlyList<string> ManifestHashes { get; init; }

    /// <summary>The profile only fits recordings it was calibrated on.</summary>
    public bool Fits(int width, int height) => width == SourceWidth && height == SourceHeight;

    /// <summary>Applies the calibrated values; everything else stays as configured.</summary>
    public Configuration ApplyTo(Configuration configuration) => configuration with
    {
        Ocr = configuration.Ocr with
        {
            MinimumConfidence = MinimumConfidence,
            PlayerNameSimilarityThreshold = NameThreshold,
        },
    };

    public string Comparison =>
        $"Persönlich: {DevelopmentMetrics.Summary}\nStandard: {BaselineDevelopmentMetrics.Summary}";
}

public static partial class PersonalProfileTrainer
{
    // A small, fixed ladder around the shipped default keeps the search deterministic.
    private static readonly double[] NameThresholds = [70, 75, 78, 82, 85, 88, 92];

    /// <summary>
    /// Searches the allowed parameter combinations on the development cases and measures the
    /// result on the holdout cases afterwards. Holdout never influences the choice.
    /// </summary>
    public static PersonalProfile Calibrate(Configuration configuration,
        IReadOnlyList<PersonalCalibrationCase> cases, string name = "Persönliches Profil")
    {
        var development = cases.Where(item => item.Sample.Split == "development").ToArray();
        var holdout = cases.Where(item => item.Sample.Split == "holdout").ToArray();
        if (development.Length == 0)
            throw new ConfigurationException("Für die Kalibrierung fehlen Entwicklungsfälle.");
        if (!development.Any(item => item.Sample.ExpectedPositive))
            throw new ConfigurationException(
                "Für die Kalibrierung fehlen positive Beispiele, also bestätigte eigene Kills.");
        if (!development.Any(item => !item.Sample.ExpectedPositive))
            throw new ConfigurationException(
                "Für die Kalibrierung fehlen negative Beispiele, etwa eigene Tode oder fremde Kills.");

        // Candidate confidences come from the material itself, so a threshold always sits on a
        // value that actually occurred; the configured one stays in the race.
        var confidences = development
            .SelectMany(item => item.Lines.Select(line => Math.Round(line.Confidence, 4)))
            .Append(Math.Round(configuration.Ocr.MinimumConfidence, 4))
            .Where(value => value is >= 0 and <= 1)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var names = NameThresholds
            .Append(configuration.Ocr.PlayerNameSimilarityThreshold)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        (double Confidence, double Name, PersonalMetrics Metrics)? best = null;
        foreach (var confidence in confidences)
            foreach (var threshold in names)
            {
                var metrics = Evaluate(configuration, development, confidence, threshold);
                if (best is null || Better(metrics, confidence, threshold, best.Value))
                    best = (confidence, threshold, metrics);
            }

        var chosen = best!.Value;
        var baselineConfidence = configuration.Ocr.MinimumConfidence;
        var baselineName = configuration.Ocr.PlayerNameSimilarityThreshold;
        var first = development[0].Sample;
        return new PersonalProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            ApplicationVersion = typeof(PersonalProfile).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            OcrModels = "PP-OCRv5 (RapidOcrNet 4.2.0)",
            SourceWidth = first.Width,
            SourceHeight = first.Height,
            Region = new()
            {
                X = development[0].Region.X, Y = development[0].Region.Y,
                Width = development[0].Region.Width, Height = development[0].Region.Height,
            },
            Mode = configuration.Detection.Mode,
            MinimumConfidence = chosen.Confidence,
            NameThreshold = chosen.Name,
            DevelopmentMetrics = chosen.Metrics,
            BaselineDevelopmentMetrics = Evaluate(configuration, development, baselineConfidence, baselineName),
            HoldoutMetrics = Evaluate(configuration, holdout, chosen.Confidence, chosen.Name),
            BaselineHoldoutMetrics = Evaluate(configuration, holdout, baselineConfidence, baselineName),
            DevelopmentLabels = Count(development),
            HoldoutLabels = Count(holdout),
            ManifestHashes = [.. cases.Select(item => item.Sample.ManifestHash).Distinct().Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Higher F1 wins, then higher precision, then the stricter setting: of two equally good
    /// combinations the one that demands more is the safer choice.
    /// </summary>
    private static bool Better(PersonalMetrics metrics, double confidence, double name,
        (double Confidence, double Name, PersonalMetrics Metrics) current)
    {
        const double epsilon = 1e-9;
        if (metrics.F1 > current.Metrics.F1 + epsilon) return true;
        if (metrics.F1 < current.Metrics.F1 - epsilon) return false;
        if (metrics.Precision > current.Metrics.Precision + epsilon) return true;
        if (metrics.Precision < current.Metrics.Precision - epsilon) return false;
        if (confidence > current.Confidence + epsilon) return true;
        if (confidence < current.Confidence - epsilon) return false;
        return name > current.Name + epsilon;
    }

    private static PersonalMetrics Evaluate(Configuration configuration,
        IReadOnlyList<PersonalCalibrationCase> cases, double confidence, double nameThreshold)
    {
        var settings = configuration.ToDetectionSettings() with
        {
            MinimumConfidence = confidence,
            NameThreshold = nameThreshold,
        };
        var detector = new KillfeedDetector(settings);
        int truePositive = 0, falsePositive = 0, falseNegative = 0, trueNegative = 0;
        foreach (var item in cases)
        {
            var detected = detector.Detect(item.Lines, item.Region, item.Sample.Timestamp,
                0, item.Sample.Id).Candidates.Count > 0;
            if (item.Sample.ExpectedPositive && detected) truePositive++;
            else if (item.Sample.ExpectedPositive) falseNegative++;
            else if (detected) falsePositive++;
            else trueNegative++;
        }
        return new(truePositive, falsePositive, falseNegative, trueNegative);
    }

    private static IReadOnlyDictionary<string, int> Count(IReadOnlyList<PersonalCalibrationCase> cases) =>
        cases.GroupBy(item => item.Sample.Label)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>
    /// Runs the OCR of the analysis over the frame of every sample, so the calibration sees
    /// exactly what a real run would see.
    /// </summary>
    public static async Task<IReadOnlyList<PersonalCalibrationCase>> BuildCasesAsync(
        Configuration configuration, IEnumerable<PersonalTrainingSample> samples,
        Func<IOcrEngine> ocrEngineFactory, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var cases = new List<PersonalCalibrationCase>();
        using var engine = ocrEngineFactory();
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(sample.Id);
            var region = configuration.ResolveKillfeedRegion(sample.Width, sample.Height).ToPixelRegion();
            using var frame = Cv2.ImDecode(await File.ReadAllBytesAsync(sample.FramePath, token),
                ImreadModes.Color);
            if (frame.Empty()) throw new IOException("Bild konnte nicht gelesen werden: " + sample.FramePath);
            if (frame.Width != sample.Width || frame.Height != sample.Height)
                throw new ConfigurationException(
                    $"{sample.Id}: Das Bild ist {frame.Width}x{frame.Height}, das Manifest nennt "
                    + $"{sample.Width}x{sample.Height}.");
            using var crop = new Mat(frame, new Rect(region.X, region.Y, region.Width, region.Height));
            cases.Add(new(sample, region, await engine.ReadAsync(crop, region, token)));
        }
        return cases;
    }

    internal static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
