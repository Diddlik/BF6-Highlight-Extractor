using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bf6Highlights;

public sealed record ClipSegment(double StartSeconds, double EndSeconds,
    IReadOnlyList<KillCandidate> Events, string ClipType)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}

public sealed record AnalysisSummary(string SourceVideo, double DurationSeconds, int FramesSampled,
    int OcrCalls, int TemplateChecks, int KillsDetected, int ClipsCreated,
    double ProcessingDurationSeconds, double AverageProcessingFps, bool Interrupted = false);

/// <summary>events.json, events.csv, segments.json and summary.json in the Python report format.</summary>
public static class Reports
{
    public static readonly string[] CsvColumns =
    [
        "timestamp_seconds", "timestamp_formatted", "player_name_detected", "player_name_configured",
        "opponent_name", "weapon_text", "raw_text", "ocr_confidence", "similarity_score",
        "frame_number", "source_video", "event_type", "detection_method",
    ];

    public static string FormatTimestamp(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentException("Ungültiger Zeitstempel.");
        var total = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.ToEven);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            total / 3_600_000, total / 60_000 % 60, total / 1000 % 60, total % 1000);
    }

    public static void WriteEventsJson(string path, IReadOnlyList<KillCandidate> events) =>
        Publish(path, Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in events) WriteEvent(writer, item);
            writer.WriteEndArray();
        }));

    public static void WriteSegmentsJson(string path, IReadOnlyList<ClipSegment> segments) =>
        Publish(path, Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var segment in segments)
            {
                writer.WriteStartObject();
                Number(writer, "start_seconds", segment.StartSeconds, 3);
                Number(writer, "end_seconds", segment.EndSeconds, 3);
                writer.WriteString("start_formatted", FormatTimestamp(segment.StartSeconds));
                writer.WriteString("end_formatted", FormatTimestamp(segment.EndSeconds));
                Number(writer, "duration_seconds", segment.DurationSeconds, 3);
                writer.WriteString("clip_type", segment.ClipType);
                writer.WriteNumber("kill_count", segment.Events.Count);
                writer.WriteStartArray("events");
                foreach (var item in segment.Events) WriteEvent(writer, item);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }));

    public static void WriteSummaryJson(string path, AnalysisSummary summary) =>
        Publish(path, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("source_video", summary.SourceVideo);
            Number(writer, "duration_seconds", summary.DurationSeconds, null);
            writer.WriteNumber("frames_sampled", summary.FramesSampled);
            writer.WriteNumber("ocr_calls", summary.OcrCalls);
            writer.WriteNumber("template_checks", summary.TemplateChecks);
            writer.WriteNumber("kills_detected", summary.KillsDetected);
            writer.WriteNumber("clips_created", summary.ClipsCreated);
            Number(writer, "processing_duration_seconds", summary.ProcessingDurationSeconds, null);
            Number(writer, "average_processing_fps", summary.AverageProcessingFps, null);
            writer.WriteBoolean("interrupted", summary.Interrupted);
            writer.WriteEndObject();
        }));

    /// <summary>Progress of an interrupted run, so an analysis can be continued later.</summary>
    public static void WriteCheckpointJson(string path, string video, double lastTimestamp, int eventsDetected) =>
        Publish(path, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("video", video);
            Number(writer, "last_processed_timestamp", lastTimestamp, 3);
            writer.WriteNumber("events_detected", eventsDetected);
            writer.WriteEndObject();
        }));

    public static void WriteEventsCsv(string path, IReadOnlyList<KillCandidate> events)
    {
        var text = new StringBuilder(string.Join(',', CsvColumns)).Append("\r\n");
        foreach (var item in events)
            text.AppendJoin(',', new[]
            {
                Decimal(item.TimestampSeconds, 3), FormatTimestamp(item.TimestampSeconds),
                item.PlayerNameDetected, item.PlayerNameConfigured, item.OpponentName ?? "",
                item.WeaponText ?? "", item.RawText, Decimal(item.Confidence, 4),
                Decimal(item.SimilarityScore, 2), item.FrameNumber.ToString(CultureInfo.InvariantCulture),
                item.SourceVideo, item.EventType, item.DetectionMethod,
            }.Select(Quote)).Append("\r\n");
        Publish(path, new UTF8Encoding(false).GetBytes(text.ToString()));
    }

    public static IReadOnlyList<KillCandidate> ReadEventsJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new IOException("Ereignisdatei enthält keine Liste: " + path);
        try
        {
            return document.RootElement.EnumerateArray().Select(item => new KillCandidate(
                Double(item, "timestamp_seconds"),
                Text(item, "player_name_detected") ?? "", Text(item, "player_name_configured") ?? "",
                Text(item, "opponent_name"), Text(item, "weapon_text"), Text(item, "raw_text") ?? "",
                Double(item, "confidence"), Double(item, "similarity_score"),
                item.TryGetProperty("frame_number", out var frame) ? frame.GetInt32() : 0,
                Text(item, "source_video") ?? "", 0,
                Text(item, "event_type") ?? "kill", Text(item, "detection_method") ?? "ocr")).ToArray();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            throw new IOException("Ereignisdatei enthält ungültige Felder: " + path, error);
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;

    private static double Double(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble() : 0;

    private static void WriteEvent(Utf8JsonWriter writer, KillCandidate item)
    {
        writer.WriteStartObject();
        Number(writer, "timestamp_seconds", item.TimestampSeconds, 3);
        writer.WriteString("timestamp_formatted", FormatTimestamp(item.TimestampSeconds));
        writer.WriteString("player_name_detected", item.PlayerNameDetected);
        writer.WriteString("player_name_configured", item.PlayerNameConfigured);
        writer.WriteString("opponent_name", item.OpponentName);
        writer.WriteString("weapon_text", item.WeaponText);
        writer.WriteString("raw_text", item.RawText);
        Number(writer, "confidence", item.Confidence, 4);
        Number(writer, "similarity_score", item.SimilarityScore, 2);
        writer.WriteNumber("frame_number", item.FrameNumber);
        writer.WriteString("source_video", item.SourceVideo);
        writer.WriteString("event_type", item.EventType);
        writer.WriteString("detection_method", item.DetectionMethod);
        writer.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter writer, string name, double value, int? digits)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(Decimal(value, digits), skipInputValidation: true);
    }

    /// <summary>Python repr of the rounded value, so 6 stays "6.0" and 0.92035 becomes "0.9204".</summary>
    private static string Decimal(double value, int? digits)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Ungültiger Zahlenwert im Bericht.");
        var rounded = digits is null ? value : Math.Round(value, digits.Value, MidpointRounding.ToEven);
        var text = rounded.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'e') < 0 ? text + ".0" : text;
    }

    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })) body(writer);
        return buffer.ToArray();
    }

    private static void Publish(string path, byte[] payload)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".bf6-report-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, full, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
