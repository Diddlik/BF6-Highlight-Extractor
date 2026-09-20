using System.Security.Cryptography;
using System.Text.Json;

namespace Bf6Highlights;

public static class SampleExporter
{
    public static readonly string[] Labels =
        ["own_kill", "own_death", "headshot", "foreign_kill", "no_event", "no_event_marker", "multiple_kills"];

    public static async Task ExportAsync(string source, double start, double end, double timestamp,
        string label, string destination, CancellationToken token = default)
    {
        if (!Labels.Contains(label)) throw new ArgumentException("Unbekanntes Label: " + label);
        if (!double.IsFinite(timestamp) || timestamp < start || timestamp >= end)
            throw new ArgumentException("Zeitmarke muss innerhalb des Ausschnitts liegen.");
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Sample-Ziel existiert bereits.");
        var service = new VideoService();
        var metadata = await service.ProbeAsync(source, token);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".bf6-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            await service.ClipAsync(source, start, end, Path.Combine(temporary, "clip.mp4"), token);
            await service.FrameAsync(source, timestamp, Path.Combine(temporary, "frame.png"), token);
            await using var input = File.OpenRead(metadata.Path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
            var manifest = new
            {
                schema_version = 1, id = Guid.NewGuid().ToString("N"),
                source = Path.GetFileName(metadata.Path), source_sha256 = hash,
                source_width = metadata.Width, source_height = metadata.Height,
                source_fps = metadata.Fps, source_video_codec = metadata.VideoCodec,
                source_audio_codec = metadata.AudioCodec,
                window_start_seconds = start, window_end_seconds = end,
                timestamp_hint_seconds = timestamp, frame_in_clip_seconds = timestamp - start,
                label_hint = label, status = "needs_review", split = "unassigned",
                expected_events = (object?)null, player = "", reviewer = "", reviewed_at = (string?)null,
                clip = "clip.mp4", frame = "frame.png",
            };
            await File.WriteAllTextAsync(Path.Combine(temporary, "sample.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
        }
        finally
        {
            // Only this invocation's random staging directory is removed.
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
