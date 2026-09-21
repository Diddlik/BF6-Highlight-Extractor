using System.Security.Cryptography;
using System.Text.Json;

namespace Bf6Highlights;

public sealed record PersonalTrainingSample(
    string Id, string Directory, string FramePath, string SourceHash, int Width, int Height,
    double Timestamp, string Label, string Split, bool ExpectedPositive, string ManifestHash);

public sealed record PersonalTrainingDataset(
    IReadOnlyList<PersonalTrainingSample> Development,
    IReadOnlyList<PersonalTrainingSample> Holdout,
    IReadOnlyList<string> Issues);

public static partial class PersonalProfileTrainer
{
    private static readonly HashSet<string> PositiveLabels =
        ["own_kill", "headshot", "multiple_kills"];

    public static PersonalTrainingDataset LoadDataset(string root, bool allowLabelHints)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Sample-Ordner nicht gefunden: " + root);

        var development = new List<PersonalTrainingSample>();
        var holdout = new List<PersonalTrainingSample>();
        var issues = new List<string>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "sample.json", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var value = document.RootElement;
                var id = RequiredString(value, "id");
                var label = RequiredString(value, "label_hint");
                var split = RequiredString(value, "split");
                var status = RequiredString(value, "status");
                if (!SampleExporter.Labels.Contains(label)) throw new InvalidDataException("unbekanntes Label");
                if (split is not ("development" or "holdout"))
                    throw new InvalidDataException("Split muss development oder holdout sein");

                bool positive;
                if (status == "reviewed" && value.TryGetProperty("expected_events", out var events)
                    && events.ValueKind == JsonValueKind.Array)
                    positive = events.GetArrayLength() > 0;
                else if (allowLabelHints)
                    positive = PositiveLabels.Contains(label);
                else
                    throw new InvalidDataException("Sample ist nicht geprüft");

                var directory = Path.GetDirectoryName(manifestPath)!;
                var frameName = value.TryGetProperty("frame", out var frameValue)
                    ? frameValue.GetString() : "frame.png";
                var framePath = Path.Combine(directory, frameName ?? "frame.png");
                if (!File.Exists(framePath)) throw new InvalidDataException("Frame fehlt");
                var bytes = File.ReadAllBytes(manifestPath);
                var sample = new PersonalTrainingSample(id, directory, framePath,
                    RequiredString(value, "source_sha256"), RequiredInt(value, "source_width"),
                    RequiredInt(value, "source_height"), RequiredDouble(value, "timestamp_hint_seconds"),
                    label, split, positive, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                (split == "development" ? development : holdout).Add(sample);
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException
                or KeyNotFoundException or InvalidOperationException)
            {
                issues.Add(Path.GetRelativePath(root, manifestPath) + ": " + error.Message);
            }
        }
        var leaked = development.Select(sample => sample.SourceHash)
            .Intersect(holdout.Select(sample => sample.SourceHash), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (leaked is not null)
            throw new ConfigurationException(
                "Dieselbe Quellaufnahme liegt in Development und Holdout. Splits pro Video trennen.");
        return new(development, holdout, issues);
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new InvalidDataException($"{name} fehlt");

    private static int RequiredInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) && result > 0
            ? result : throw new InvalidDataException($"{name} fehlt oder ist ungültig");

    private static double RequiredDouble(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var result)
            && double.IsFinite(result) && result >= 0
            ? result : throw new InvalidDataException($"{name} fehlt oder ist ungültig");
}
