using System.Security.Cryptography;
using System.Text.Json;

namespace Bf6Highlights;

public sealed class PreparedSampleSource
{
    private readonly long length;
    private readonly DateTime lastWriteTimeUtc;

    internal PreparedSampleSource(VideoMetadata metadata, string sha256, long length,
        DateTime lastWriteTimeUtc) =>
        (Metadata, Sha256, this.length, this.lastWriteTimeUtc) =
        (metadata, sha256, length, lastWriteTimeUtc);

    public VideoMetadata Metadata { get; }
    public string Sha256 { get; }

    internal void EnsureUnchanged()
    {
        var file = new FileInfo(Metadata.Path);
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != lastWriteTimeUtc)
            throw new IOException("Quellvideo wurde seit der Vorbereitung geändert.");
    }
}

public static class SampleExporter
{
    public static readonly string[] Labels =
        ["own_kill", "own_death", "headshot", "foreign_kill", "no_event", "no_event_marker", "multiple_kills"];

    public static async Task ExportAsync(string source, double start, double end, double timestamp,
        string label, string destination, CancellationToken token = default,
        string player = "", string split = "unassigned")
    {
        Validate(start, end, timestamp, label, split);
        await ExportAsync(await PrepareSourceAsync(source, token), start, end, timestamp, label,
            destination, token, player, split);
    }

    public static async Task<PreparedSampleSource> PrepareSourceAsync(string source,
        CancellationToken token = default)
    {
        var metadata = await new VideoService().ProbeAsync(source, token);
        var file = new FileInfo(metadata.Path);
        var length = file.Length;
        var lastWriteTimeUtc = file.LastWriteTimeUtc;
        await using var input = File.OpenRead(metadata.Path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != lastWriteTimeUtc)
            throw new IOException("Quellvideo wurde während der Vorbereitung geändert.");
        return new(metadata, hash, length, lastWriteTimeUtc);
    }

    public static async Task ExportAsync(PreparedSampleSource source, double start, double end,
        double timestamp, string label, string destination, CancellationToken token = default,
        string player = "", string split = "unassigned")
    {
        Validate(start, end, timestamp, label, split);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Sample-Ziel existiert bereits.");
        source.EnsureUnchanged();
        var service = new VideoService();
        var metadata = source.Metadata;
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".bf6-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            await service.ClipAsync(metadata.Path, start, end, Path.Combine(temporary, "clip.mp4"), token);
            await service.FrameAsync(metadata.Path, timestamp, Path.Combine(temporary, "frame.png"), token);
            source.EnsureUnchanged();
            var manifest = new
            {
                schema_version = 1, id = Guid.NewGuid().ToString("N"),
                source = Path.GetFileName(metadata.Path), source_sha256 = source.Sha256,
                source_width = metadata.Width, source_height = metadata.Height,
                source_fps = metadata.Fps, source_video_codec = metadata.VideoCodec,
                source_audio_codec = metadata.AudioCodec,
                window_start_seconds = start, window_end_seconds = end,
                timestamp_hint_seconds = timestamp, frame_in_clip_seconds = timestamp - start,
                label_hint = label, status = "needs_review", split,
                expected_events = (object?)null, player = player.Trim(), reviewer = "", reviewed_at = (string?)null,
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

    private static void Validate(double start, double end, double timestamp, string label, string split)
    {
        if (!Labels.Contains(label)) throw new ArgumentException("Unbekanntes Label: " + label);
        if (split is not ("unassigned" or "development" or "holdout"))
            throw new ArgumentException("Split muss unassigned, development oder holdout sein.");
        if (!double.IsFinite(timestamp) || timestamp < start || timestamp >= end)
            throw new ArgumentException("Zeitmarke muss innerhalb des Ausschnitts liegen.");
    }
}
