using System.Globalization;
using System.Text.RegularExpressions;

namespace Bf6Highlights;

/// <summary>Windows-safe clip file names (Python clips/naming.py).</summary>
public static partial class ClipNaming
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        .. Enumerable.Range(1, 9).Select(index => "COM" + index),
        .. Enumerable.Range(1, 9).Select(index => "LPT" + index),
    ];

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex Illegal();

    public static string Sanitize(string name)
    {
        var cleaned = Illegal().Replace(name, "_").Trim(' ', '.');
        if (Reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) cleaned = "_" + cleaned;
        return cleaned.Length == 0 ? "clip" : cleaned;
    }

    /// <summary>Seconds to HH-MM-SS, safe for a file name.</summary>
    public static string TimestampForFileName(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentException("Ungültiger Zeitstempel.");
        var total = (long)Math.Max(0, seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}-{1:00}-{2:00}",
            total / 3600, total / 60 % 60, total % 60);
    }

    /// <summary>For example stream_00-15-42_single-kill.mp4 or stream_01-02-51_multi-kill_4.mp4.</summary>
    public static string ClipFileName(string source, ClipSegment segment, string extension = ".mp4")
    {
        var first = segment.Events.Count > 0 ? segment.Events[0].TimestampSeconds : segment.StartSeconds;
        var suffix = segment.ClipType == "multi_kill" ? "_" + segment.Events.Count : "";
        return $"{Sanitize(Path.GetFileNameWithoutExtension(source))}_{TimestampForFileName(first)}"
            + $"_{segment.ClipType.Replace('_', '-')}{suffix}{extension}";
    }

    /// <summary>Never overwrites an existing clip; appends _2, _3, … instead.</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var counter = 2; ; counter++)
        {
            candidate = Path.Combine(directory, $"{stem}_{counter}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

public sealed record ClipExportResult(IReadOnlyList<string> Written, IReadOnlyList<string> Failures);

/// <summary>
/// Clip export through FFmpeg (Python clips/ffmpeg_exporter.py): accurate re-encoding or fast
/// stream copy. Segments arrive as given, so a caller may export a selection with corrected bounds.
/// </summary>
public sealed class ClipExporter(ClipSettings settings)
{
    public static string[] BuildArguments(string source, ClipSegment segment, string output,
        ClipSettings settings)
    {
        string[] mode = settings.ExportMode == "fast" ? ["-c", "copy"]
            : ["-c:v", settings.VideoCodec, "-preset", settings.Preset,
               "-crf", settings.Crf.ToString(CultureInfo.InvariantCulture),
               "-c:a", settings.AudioCodec, "-b:a", settings.AudioBitrate];
        return ["-hide_banner", "-loglevel", "error", "-y",
                "-ss", Seconds(segment.StartSeconds), "-i", source,
                "-t", Seconds(segment.DurationSeconds), .. mode, output];
    }

    private static string Seconds(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Exports every segment into the directory. A single failed clip is reported and skipped;
    /// only a run without any written clip fails. Existing clips and the source stay untouched.
    /// </summary>
    public async Task<ClipExportResult> ExportAsync(string source, IReadOnlyList<ClipSegment> segments,
        string directory, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var video = await new VideoService().ProbeAsync(source, token);
        foreach (var segment in segments)
            if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds)
                || segment.StartSeconds < 0 || segment.EndSeconds <= segment.StartSeconds
                || segment.EndSeconds > video.DurationSeconds + 0.001)
                throw new ArgumentException("Clip-Grenzen liegen außerhalb des Videos oder sind vertauscht.");
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        var failures = new List<string>();
        foreach (var segment in segments)
        {
            token.ThrowIfCancellationRequested();
            var output = ClipNaming.UniquePath(directory, ClipNaming.ClipFileName(video.Path, segment));
            if (Path.GetFullPath(output) == video.Path)
                throw new IOException("Das Clip-Ziel darf nicht die Quelldatei sein.");
            var temporary = Path.Combine(directory, $".bf6-clip-{Guid.NewGuid():N}.mp4");
            try
            {
                await MediaProcess.RunAsync(VideoService.Tool("ffmpeg"),
                    BuildArguments(video.Path, segment, temporary, settings),
                    TimeSpan.FromMinutes(10), token);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                    throw new IOException("FFmpeg hat keine Ausgabe erzeugt.");
                File.Move(temporary, output, overwrite: false);
                written.Add(output);
                progress?.Report(Path.GetFileName(output));
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                failures.Add($"{Path.GetFileName(output)} "
                    + $"({segment.StartSeconds:0.0}-{segment.EndSeconds:0.0} s): {error.Message}");
            }
            finally { File.Delete(temporary); }
        }
        if (segments.Count > 0 && written.Count == 0)
            throw new IOException("Kein Clip konnte exportiert werden:\n  - "
                + string.Join("\n  - ", failures));
        return new(written, failures);
    }
}
