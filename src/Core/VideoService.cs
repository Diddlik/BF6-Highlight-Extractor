using System.Globalization;
using System.Text.Json;

namespace Bf6Highlights;

public sealed record VideoMetadata(string Path, int Width, int Height, double Fps,
    double DurationSeconds, string VideoCodec, string? AudioCodec);

public sealed class VideoService
{
    internal static string Tool(string name)
    {
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        return File.Exists(bundled) ? bundled : name;
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    public async Task<VideoMetadata> ProbeAsync(string source, CancellationToken token = default)
    {
        source = System.IO.Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Video nicht gefunden.", source);
        var output = await MediaProcess.RunAsync(Tool("ffprobe"),
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", source],
            TimeSpan.FromSeconds(30), token);
        using var document = JsonDocument.Parse(output);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("Keine Videospur.");
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        var duration = document.RootElement.TryGetProperty("format", out var format)
            ? Parse(format, "duration") : 0;
        if (duration <= 0) duration = Parse(video, "duration");
        var fps = Rate(video, "avg_frame_rate");
        if (fps <= 0) fps = Rate(video, "r_frame_rate");
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(fps) || fps <= 0)
            throw new IOException("Ungültige Dauer oder Bildrate.");
        return new(source, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            fps, duration, video.GetProperty("codec_name").GetString() ?? "unknown",
            audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("codec_name").GetString());
    }

    private static double Parse(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && double.TryParse(field.ToString(),
            CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static double Rate(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var field)) return 0;
        var parts = (field.GetString() ?? "").Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 0;
    }

    /// <summary>
    /// The keyframe positions from <paramref name="startSeconds"/> on. Only the packets are read,
    /// nothing is decoded, so this stays cheap even for a long recording. An empty result means
    /// the positions are unknown, not that the recording has no keyframes.
    /// </summary>
    public async Task<IReadOnlyList<double>> KeyframesAsync(string source, double startSeconds = 0,
        CancellationToken token = default)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0)
            throw new ArgumentException("Startzeit liegt außerhalb des Videos.");
        var path = System.IO.Path.GetFullPath(source);
        // ffprobe's interval reading occasionally returns nothing at all for a file it read a
        // moment ago, so an empty answer is asked once more before it is believed.
        for (var attempt = 0; ; attempt++)
        {
            var output = await MediaProcess.RunAsync(Tool("ffprobe"),
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags",
                    "-of", "csv=p=0", "-read_intervals", Number(startSeconds) + "%", path],
                TimeSpan.FromMinutes(5), token);
            var keyframes = new List<double>();
            foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries
                         | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 2 || !parts[1].StartsWith('K')) continue;
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var position)
                    && position >= startSeconds && (keyframes.Count == 0 || position > keyframes[^1]))
                    keyframes.Add(position);
            }
            if (keyframes.Count > 0 || attempt > 0) return keyframes;
        }
    }

    public async Task FrameAsync(string source, double timestamp, string destination,
        CancellationToken token = default)
    {
        var video = await ProbeAsync(source, token);
        if (!double.IsFinite(timestamp) || timestamp < 0 || timestamp >= video.DurationSeconds)
            throw new ArgumentException("Frame-Zeit liegt außerhalb des Videos.");
        if (!destination.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Frame-Ziel muss .png sein.");
        await WriteAsync(destination,
            ["-ss", Number(timestamp), "-i", video.Path, "-map", "0:v:0", "-frames:v", "1"], token);
    }

    public async Task ClipAsync(string source, double start, double end, string destination,
        CancellationToken token = default)
    {
        var video = await ProbeAsync(source, token);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start
            || end > video.DurationSeconds)
            throw new ArgumentException("Clip-Grenzen liegen außerhalb des Videos oder sind vertauscht.");
        if (!destination.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Clip-Ziel muss .mp4 sein.");
        await WriteAsync(destination,
            ["-ss", Number(start), "-i", video.Path, "-t", Number(end - start),
             "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "fast",
             "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"], token);
    }

    private static async Task WriteAsync(string destination, string[] arguments, CancellationToken token)
    {
        destination = System.IO.Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Zieldatei existiert bereits.");
        var directory = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory,
            $".bf6-{Guid.NewGuid():N}{System.IO.Path.GetExtension(destination)}");
        try
        {
            await MediaProcess.RunAsync(Tool("ffmpeg"),
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-n", .. arguments, temporary],
                TimeSpan.FromMinutes(10), token);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("FFmpeg hat keine Ausgabe erzeugt.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }
}
