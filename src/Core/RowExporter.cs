using System.Globalization;
using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>
/// Cuts every killfeed row that carries the player's name out of a recording, labelled the way the
/// rule-based detection sees it. The rows are the raw material for the row classifier; the labels
/// are hints until someone has reviewed them.
/// </summary>
public static class RowExporter
{
    public const string Kill = "kill";
    public const string Ping = "ping";
    public const string Death = "death";

    public static async Task<int> RunAsync(Configuration configuration, VideoMetadata video,
        string destination, Func<IOcrEngine> ocrEngineFactory, string? profilePath = null,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        var index = Path.Combine(destination, "rows.csv");
        if (File.Exists(index)) throw new IOException("Zielordner enthält schon einen Export: " + destination);
        var (active, _) = PersonalProfileStore.Apply(configuration, profilePath, video.Width, video.Height);
        var region = active.ResolveKillfeedRegion(video.Width, video.Height).ToPixelRegion();
        var detector = new KillfeedDetector(active.ToDetectionSettings());
        var step = ChangeDetector.SampleStep(video.Fps, active.Analysis.SamplesPerSecond);
        var half = Band(region.Height, 0).Height / 2;
        var name = Path.GetFileName(video.Path);
        var stem = Path.GetFileNameWithoutExtension(video.Path);
        Directory.CreateDirectory(Path.Combine(destination, "rows"));
        var recent = new List<(string Label, int Y, double Time)>();
        var written = 0;
        using var engine = ocrEngineFactory();
        await using var writer = new StreamWriter(index, append: false);
        await writer.WriteLineAsync("file,source,time,label,row_text");
        await foreach (var frame in FrameStream.ReadEveryAsync(video, region, step, token))
            using (frame)
            {
                progress?.Report(frame.TimestampSeconds);
                var lines = await engine.ReadAsync(frame.Image, region, token);
                var detected = detector.Detect(lines, region, frame.TimestampSeconds, frame.Number, name);
                var rows = detected.Candidates.Select(c => (Label: Kill, c.RowY, c.RawText))
                    .Concat(detected.Rejections
                        .Where(r => r.Reason is KillfeedDetector.PingMessage or KillfeedDetector.VictimSide)
                        .Select(r => (Label: r.Reason == KillfeedDetector.PingMessage ? Ping : Death,
                            r.RowY, RawText: r.RowText)));
                foreach (var (label, rowY, text) in rows)
                {
                    // A row stays for seconds; one picture per row and second is enough variety.
                    var now = frame.TimestampSeconds;
                    if (recent.Any(r => r.Label == label && Math.Abs(r.Y - rowY) <= half && now - r.Time < 1))
                        continue;
                    recent.RemoveAll(r => now - r.Time >= 1);
                    recent.Add((label, rowY, now));
                    var band = Band(frame.Image.Height, rowY);
                    using var crop = new Mat(frame.Image, new Rect(0, band.Top, frame.Image.Width, band.Height));
                    var file = $"{stem}_{now.ToString("0000.00", CultureInfo.InvariantCulture)}_{rowY:000}.png";
                    await File.WriteAllBytesAsync(Path.Combine(destination, "rows", file), crop.ToBytes(".png"), token);
                    await writer.WriteLineAsync(string.Join(',', file, Csv(name),
                        now.ToString("0.00", CultureInfo.InvariantCulture), label, Csv(text)));
                    written++;
                }
            }
        return written;
    }

    /// <summary>
    /// The horizontal band of one killfeed row around its centre, in the killfeed crop. A row is about
    /// a seventh of the region high; export and classifier must cut the same band.
    /// </summary>
    public static (int Top, int Height) Band(int killfeedHeight, int rowY)
    {
        var half = Math.Max(12, (int)Math.Round(killfeedHeight * 0.075));
        var height = Math.Min(2 * half, killfeedHeight);
        return (Math.Clamp(rowY - half, 0, killfeedHeight - height), height);
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
