using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>OCR on an already cropped frame; the origin maps boxes back to source coordinates.</summary>
public interface IOcrEngine : IDisposable
{
    Task<IReadOnlyList<OcrLine>> ReadAsync(Mat crop, PixelRegion origin, CancellationToken token = default);
}

public sealed record AnalysisProgress(double TimestampSeconds, double DurationSeconds,
    int FramesSampled, int OcrCalls, int TemplateChecks, int KillsDetected, string Stage)
{
    /// <summary>The event accepted in this step, so a caller can show a live list.</summary>
    public KillCandidate? LastEvent { get; init; }

    public double Percent => DurationSeconds <= 0
        ? 0 : Math.Min(100, 100 * TimestampSeconds / DurationSeconds);
}

public sealed record AnalysisResult(VideoMetadata Video, IReadOnlyList<KillCandidate> Events,
    IReadOnlyList<ClipSegment> Segments, AnalysisSummary Summary, bool Interrupted)
{
    public IReadOnlyList<string> Clips { get; init; } = [];
}

/// <summary>
/// The detection pipeline: sample, cheap change detection,
/// OCR only on changed crops or template matching, then deduplicate, group and report.
/// </summary>
public sealed class AnalysisService(Configuration configuration, Func<IOcrEngine> ocrEngineFactory)
{
    /// <summary>A personal profile to calibrate the detection with, or null for the standard.</summary>
    public string? ProfilePath { get; init; }
    /// <summary>Checks every OCR kill against the picture of its row; null keeps the OCR alone.</summary>
    public RowClassifier? RowClassifier { get; init; }

    /// <summary>Which detection the last run used; shown so nobody has to guess.</summary>
    public string DetectionNote { get; private set; } = "Standarderkennung";

    /// <summary>
    /// Analyses the recording and writes the reports. With <paramref name="exportClips"/> the
    /// segments are exported as well; without it the run stays a report-only pass.
    /// </summary>
    public async Task<AnalysisResult> RunAsync(string source, string outputDirectory,
        IProgress<AnalysisProgress>? progress = null, CancellationToken token = default,
        bool exportClips = false)
    {
        var started = Stopwatch.StartNew();
        var video = await new VideoService().ProbeAsync(source, token);
        // A personal profile only changes thresholds, and only when it fits this recording.
        var (active, detectionNote) = PersonalProfileStore.Apply(configuration, ProfilePath,
            video.Width, video.Height);
        DetectionNote = detectionNote;
        var templateMode = active.Detection.Mode == "template";
        var region = (templateMode
            ? active.ResolveDetectionRegion(video.Width, video.Height)
            : active.ResolveKillfeedRegion(video.Width, video.Height)).ToPixelRegion();
        var name = Path.GetFileName(video.Path);
        var events = new List<KillCandidate>();
        var sampled = 0;
        var calls = 0;
        var checks = 0;
        var timestamp = 0.0;
        var interrupted = false;

        KillCandidate? lastEvent = null;
        void Report(string stage) => progress?.Report(
            new(timestamp, video.DurationSeconds, sampled, calls, checks, events.Count, stage)
            { LastEvent = lastEvent });

        async Task AnalyzeWithOcr()
        {
            var settings = active.ToDetectionSettings();
            var detector = new KillfeedDetector(settings);
            var deduplicator = new EventDeduplicator(settings);
            // A BF6 jammer hides the killfeed; the kill notice below the crosshair stays visible.
            var notice = active.Game == "bf6" ? KillNotice.Region(video.Width, video.Height) : null;
            // One decode for both regions: the decode, not the OCR, is the bottleneck.
            var stream = notice is null ? region : Union(region, notice);
            var engines = new ConcurrentBag<IOcrEngine>();
            var workers = Math.Max(1, active.Analysis.MaxWorkers);
            using var slots = new SemaphoreSlim(workers);
            var pending = new Queue<Pending>();
            using var change = new ChangeDetector(active.Analysis.ChangeThreshold);
            using var noticeChange = new ChangeDetector(active.Analysis.ChangeThreshold);

            async Task<IReadOnlyList<OcrLine>> Read(Mat crop, PixelRegion origin)
            {
                // At most one engine per worker slot; native OCR sessions are expensive.
                await slots.WaitAsync(token);
                var engine = engines.TryTake(out var idle) ? idle : ocrEngineFactory();
                try { return await engine.ReadAsync(crop, origin, token); }
                finally { engines.Add(engine); slots.Release(); }
            }

            void Accept(KillCandidate candidate)
            {
                if (!deduplicator.Accept(candidate)) return;
                events.Add(candidate);
                lastEvent = candidate;
            }

            void Consume(Pending sample, IReadOnlyList<OcrLine> killfeed, IReadOnlyList<OcrLine> shown)
            {
                lastEvent = null;
                using (sample.Frame)
                using (sample.Notice)
                {
                    var detected = detector.Detect(killfeed, region,
                        sample.Frame.TimestampSeconds, sample.Frame.Number, name);
                    deduplicator.Observe(detected.Rejections, name);
                    foreach (var candidate in detected.Candidates)
                        if (RowClassifier?.Vetoes(sample.Frame.Image, candidate.RowY) != true)
                            Accept(candidate);
                    if (notice is not null && KillNotice.Detect(shown, notice, settings,
                            sample.Frame.TimestampSeconds, sample.Frame.Number, name) is { } kill)
                        Accept(kill);
                }
                Report("analyzing");
            }

            try
            {
                await foreach (var frame in FrameStream.ReadAsync(video, stream,
                    active.Analysis.SamplesPerSecond, token))
                {
                    sampled++;
                    timestamp = frame.TimestampSeconds;
                    Mat feed, shown;
                    using (frame)
                    {
                        feed = Crop(frame.Image, stream, region);
                        shown = notice is null ? new Mat() : Crop(frame.Image, stream, notice);
                    }
                    var check = active.Analysis.EnableChangeDetection;
                    var feedChanged = !check || change.Changed(feed).Changed;
                    var shownChanged = notice is not null && (!check || noticeChange.Changed(shown).Changed);
                    if (!feedChanged && !shownChanged)
                    {
                        feed.Dispose();
                        shown.Dispose();
                        Report("analyzing");
                        continue;
                    }
                    calls += (feedChanged ? 1 : 0) + (shownChanged ? 1 : 0);
                    pending.Enqueue(new(new(frame.Number, frame.TimestampSeconds, feed), shown,
                        Task.Run(async () => (
                            feedChanged ? await Read(feed, region) : [],
                            shownChanged ? await Read(shown, notice!) : (IReadOnlyList<OcrLine>)[]), token)));
                    while (pending.Count >= workers * 2) await Take(pending, Consume);
                }
                while (pending.Count > 0) await Take(pending, Consume);
            }
            finally
            {
                // Frames must outlive the OCR tasks that still read their pixels.
                var remaining = pending.ToArray();
                pending.Clear();
                try { await Task.WhenAll(remaining.Select(entry => entry.Ocr)); }
                catch (Exception error) when (error is OperationCanceledException or IOException
                    or ArgumentException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { }
                foreach (var entry in remaining)
                {
                    entry.Frame.Dispose();
                    entry.Notice.Dispose();
                }
                foreach (var engine in engines) engine.Dispose();
            }
        }

        async Task AnalyzeWithTemplates()
        {
            using var matcher = new TemplateMatcher(active.Detection, video.Width);
            var grouper = new TemplateEventGrouper(active.Detection, name);
            await foreach (var frame in FrameStream.ReadAsync(video, region,
                active.Analysis.SamplesPerSecond, token))
                using (frame)
                {
                    sampled++;
                    checks++;
                    timestamp = frame.TimestampSeconds;
                    var grouped = grouper.Feed(matcher.Match(frame.Image, frame.TimestampSeconds,
                        frame.Number), frame.TimestampSeconds);
                    lastEvent = grouped;
                    if (grouped is not null) events.Add(grouped);
                    Report("analyzing");
                }
            var last = grouper.Finish();
            if (last is not null) events.Add(last);
        }

        try
        {
            if (templateMode) await AnalyzeWithTemplates(); else await AnalyzeWithOcr();
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            Directory.CreateDirectory(outputDirectory);
            Reports.WriteCheckpointJson(Path.Combine(outputDirectory, "checkpoint.json"),
                name, timestamp, events.Count);
        }

        Report("reporting");
        Reports.WriteEventsJson(Path.Combine(outputDirectory, "events.json"), events);
        Reports.WriteEventsCsv(Path.Combine(outputDirectory, "events.csv"), events);
        var segments = SegmentBuilder.Build(events, active.Clips, video.DurationSeconds);
        Reports.WriteSegmentsJson(Path.Combine(outputDirectory, "segments.json"), segments);
        IReadOnlyList<string> clips = [];
        if (exportClips && segments.Count > 0)
        {
            Report("exporting");
            clips = (await new ClipExporter().ExportAsync(video.Path, segments,
                Path.Combine(outputDirectory, "clips"), null, CancellationToken.None)).Written;
        }
        var elapsed = started.Elapsed.TotalSeconds;
        var summary = new AnalysisSummary(name, Math.Round(video.DurationSeconds, 3), sampled, calls,
            checks, events.Count, clips.Count, Math.Round(elapsed, 3),
            elapsed > 0 ? Math.Round(sampled / elapsed, 2) : 0, interrupted);
        Reports.WriteSummaryJson(Path.Combine(outputDirectory, "summary.json"), summary);
        Report("done");
        return new(video, events, segments, summary, interrupted) { Clips = clips };
    }

    private sealed record Pending(SampledFrame Frame, Mat Notice,
        Task<(IReadOnlyList<OcrLine> Killfeed, IReadOnlyList<OcrLine> Notice)> Ocr);

    private static async Task Take(Queue<Pending> pending,
        Action<Pending, IReadOnlyList<OcrLine>, IReadOnlyList<OcrLine>> consume)
    {
        var entry = pending.Dequeue();
        (IReadOnlyList<OcrLine> Killfeed, IReadOnlyList<OcrLine> Notice) lines;
        try { lines = await entry.Ocr; }
        catch { entry.Frame.Dispose(); entry.Notice.Dispose(); throw; }
        consume(entry, lines.Killfeed, lines.Notice);
    }

    private static PixelRegion Union(PixelRegion a, PixelRegion b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x,
            Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    /// <summary>An own copy of a region, so the decoded frame can go before the OCR runs.</summary>
    private static Mat Crop(Mat image, PixelRegion origin, PixelRegion region)
    {
        using var view = new Mat(image, new Rect(region.X - origin.X, region.Y - origin.Y,
            region.Width, region.Height));
        return view.Clone();
    }
}
