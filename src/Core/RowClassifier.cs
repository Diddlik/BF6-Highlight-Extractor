using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>
/// Second opinion on a killfeed row from its picture. The OCR has to read a row to judge it; the
/// classifier sees what the OCR loses: the weapon icon, a sentence instead of a victim, the skull.
/// Trained by src/Training/train.py; the preprocessing here must match that script.
/// </summary>
public sealed class RowClassifier : IDisposable
{
    private sealed record Spec(string[] Classes, int Height, int Width, string[] Players);

    private readonly InferenceSession session;
    private readonly Spec spec;

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "models", "row-classifier.onnx");

    private RowClassifier(string path, Spec spec, bool trained)
    {
        this.spec = spec;
        Trained = trained;
        using var options = new SessionOptions { IntraOpNumThreads = 1 };
        session = new InferenceSession(path, options);
    }

    /// <summary>
    /// True when a configured player is one the model was trained on. Only then does it tell kills
    /// from deaths: it knows which name is "own" by having seen it. A ping looks the same for every
    /// name, so pings are recognised for everyone.
    /// </summary>
    public bool Trained { get; }

    // The classifier only vetoes, it never adds a kill, and only when it is sure: the OCR rules
    // stay in charge wherever the picture is ambiguous.
    private const float VetoConfidence = 0.9f;

    /// <summary>True when the picture of the row clearly shows something other than an own kill.</summary>
    public bool Vetoes(Mat killfeed, int rowY) => Vetoes(Classify(killfeed, rowY), Trained);

    public static bool Vetoes(IReadOnlyDictionary<string, float> probabilities, bool trained)
    {
        var (label, probability) = probabilities.MaxBy(pair => pair.Value);
        return probability >= VetoConfidence && (label == "ping" || trained && label != "kill");
    }

    /// <summary>The classifier, or null when the application ships without the model.</summary>
    public static RowClassifier? Load(IEnumerable<string> playerNames, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        var spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(Path.ChangeExtension(path, ".json")),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new IOException("Beschreibung des Zeilenmodells ist leer.");
        var trained = playerNames.Any(name =>
            (spec.Players ?? []).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase));
        return new RowClassifier(path, spec, trained);
    }

    /// <summary>Probability of each class for the row around <paramref name="rowY"/> in the killfeed crop.</summary>
    public IReadOnlyDictionary<string, float> Classify(Mat killfeed, int rowY)
    {
        var band = RowExporter.Band(killfeed.Height, rowY);
        using var row = new Mat(killfeed, new Rect(0, band.Top, killfeed.Width, band.Height));
        return Classify(row);
    }

    /// <summary>Probability of each class for a row already cut like <see cref="RowExporter"/> does.</summary>
    public IReadOnlyDictionary<string, float> Classify(Mat row)
    {
        using var rgb = row.CvtColor(ColorConversionCodes.BGR2RGB);
        using var small = rgb.Resize(new Size(spec.Width, spec.Height), interpolation: InterpolationFlags.Area);
        var input = new DenseTensor<float>([1, 3, spec.Height, spec.Width]);
        for (var y = 0; y < spec.Height; y++)
            for (var x = 0; x < spec.Width; x++)
            {
                var pixel = small.At<Vec3b>(y, x);
                for (var channel = 0; channel < 3; channel++) input[0, channel, y, x] = pixel[channel] / 255f;
            }
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("row", input)]);
        var logits = results[0].AsEnumerable<float>().ToArray();
        var peak = logits.Max();
        var exp = logits.Select(value => MathF.Exp(value - peak)).ToArray();
        var sum = exp.Sum();
        return spec.Classes.Select((name, index) => (name, exp[index] / sum)).ToDictionary();
    }

    public void Dispose() => session.Dispose();
}
