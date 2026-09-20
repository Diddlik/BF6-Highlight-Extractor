using System.Diagnostics;
using System.Text.Json;

namespace Bf6Highlights;

public static class ReferenceDetection
{
    public static async Task<bool> RunAsync(string samples, DetectionSettings settings, PixelRegion region,
        string destination, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Prüfbericht existiert bereits.");
        var inputs = Directory.GetDirectories(samples).Select(folder =>
        {
            try
            {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "sample.json")));
            var data = manifest.RootElement;
            var time = data.GetProperty("timestamp_hint_seconds").GetDouble();
            var fps = data.GetProperty("source_fps").GetDouble();
            var source = data.GetProperty("source").GetString();
            var hash = data.GetProperty("source_sha256").GetString();
            if (data.GetProperty("schema_version").GetInt32() != 1 ||
                string.IsNullOrWhiteSpace(source) || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new IOException("Ungültiges Sample-Schema oder Quellidentität: " + folder);
            if (!double.IsFinite(time) || time < 0 || !double.IsFinite(fps) || fps <= 0 || time * fps > int.MaxValue)
                throw new IOException("Ungültiger Zeitbezug im Sample: " + folder);
            return new
            {
                Folder = folder, Time = time, Fps = fps,
                Source = source + "#" + hash,
                Label = data.GetProperty("label_hint").GetString(),
                Status = data.GetProperty("status").GetString(),
            };
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new IOException("Ungültiges Sample-Manifest: " + folder, error);
            }
        }).OrderBy(input => input.Time).ToArray();
        if (inputs.Length == 0) throw new IOException("Keine Samples gefunden.");
        var detector = new KillfeedDetector(settings);
        var dedup = new EventDeduplicator(settings);
        var cases = new List<object>();
        var interrupted = false;
        using var engine = new OnnxOcrEngine();
        try
        {
            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                var lines = await engine.ReadAsync(Path.Combine(input.Folder, "frame.png"), region, token);
                var result = detector.Detect(lines, region, input.Time,
                    checked((int)(input.Time * input.Fps)), input.Source);
                var accepted = result.Candidates.Where(dedup.Accept).ToArray();
                cases.Add(new
                {
                    Case = Path.GetFileName(input.Folder), LabelHint = input.Label, ReviewStatus = input.Status,
                    ElapsedMs = watch.Elapsed.TotalMilliseconds, OcrLines = lines,
                    Candidates = accepted, Duplicates = result.Candidates.Count - accepted.Length,
                    result.Rejections,
                });
            }
        }
        catch (OperationCanceledException) { interrupted = true; }
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var report = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Interrupted = interrupted, Settings = settings, Region = region,
            Note = "Single-frame candidate comparison; labels unverified. Not final kills, no full-video analysis.",
            Cases = cases,
        }, options);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".bf6-report-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporary, report, CancellationToken.None);
            File.Move(temporary, destination, overwrite: false);
        }
        finally { File.Delete(temporary); }
        return interrupted;
    }
}
