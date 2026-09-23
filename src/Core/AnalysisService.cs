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
/// The detection pipeline (Python services/analysis_service.py): sample, cheap change detection,
/// OCR only on changed crops or template matching, then deduplicate, group and report.
/// </summary>
public sealed class AnalysisService(Configuration configuration, Func<IOcrEngine> ocrEngineFactory)
{
    /// <summary>A personal profile to calibrate the detection with, or null for the standard.</summary>
    public string? ProfilePath { get; init; }

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
            var detector = new KillfeedDetector(active.ToDetectionSettings());
            var deduplicator = new EventDeduplicator(active.ToDetectionSettings());
            var engines = new ConcurrentBag<IOcrEngine>();
            var workers = Math.Max(1, active.Analysis.MaxWorkers);
            using var slots = new SemaphoreSlim(workers);
            var pending = new Queue<(SampledFrame Frame, Task<IReadOnlyList<OcrLine>> Ocr)>();
            using var change = new ChangeDetector(active.Analysis.ChangeThreshold);

            void Consume((SampledFrame Frame, IReadOnlyList<OcrLine> Lines) sample)
            {
                lastEvent = null;
                using (sample.Frame)
                {
                    var detected = detector.Detect(sample.Lines, region,
                        sample.Frame.TimestampSeconds, sample.Frame.Number, name);
                    deduplicator.Observe(detected.Rejections, name);
                    foreach (var candidate in detected.Candidates)
                        if (deduplicator.Accept(candidate))
                        {
                            events.Add(candidate);
                            lastEvent = candidate;
                        }
                }
                Report("analyzing");
            }

            try
            {
                await foreach (var frame in FrameStream.ReadAsync(video, region,
                    active.Analysis.SamplesPerSecond, token))
                {
                    sampled++;
                    timestamp = frame.TimestampSeconds;
                    if (active.Analysis.EnableChangeDetection
                        && !change.Changed(frame.Image).Changed)
                    {
                        frame.Dispose();
                        Report("analyzing");
                        continue;
                    }
                    calls++;
                    pending.Enqueue((frame, Task.Run(async () =>
                    {
                        // At most one engine per worker slot; native OCR sessions are expensive.
                        await slots.WaitAsync(token);
                        var engine = engines.TryTake(out var idle) ? idle : ocrEngineFactory();
                        try { return await engine.ReadAsync(frame.Image, region, token); }
                        finally { engines.Add(engine); slots.Release(); }
                    }, token)));
                    while (pending.Count >= workers * 2) Consume(await Take(pending));
                }
                while (pending.Count > 0) Consume(await Take(pending));
            }
            finally
            {
                // Frames must outlive the OCR tasks that still read their pixels.
                var remaining = pending.ToArray();
                pending.Clear();
                try { await Task.WhenAll(remaining.Select(entry => entry.Ocr)); }
                catch (Exception error) when (error is OperationCanceledException or IOException
                    or ArgumentException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { }
                foreach (var (frame, _) in remaining) frame.Dispose();
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
            clips = (await new ClipExporter(active.Clips).ExportAsync(video.Path, segments,
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

    private static async Task<(SampledFrame Frame, IReadOnlyList<OcrLine> Lines)> Take(
        Queue<(SampledFrame Frame, Task<IReadOnlyList<OcrLine>> Ocr)> pending)
    {
        var (frame, ocr) = pending.Dequeue();
        try { return (frame, await ocr); }
        catch { frame.Dispose(); throw; }
    }
}
