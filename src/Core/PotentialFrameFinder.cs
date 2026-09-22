namespace Bf6Highlights;

/// <summary>
/// Searches the next frame after a position that the configured detection accepts as a kill, or
/// as a death of the player. The sample player proposes those frames, so a suggestion means a
/// detected event and not merely a changed picture.
/// </summary>
public static class PotentialFrameFinder
{
    public static async Task<double?> FindNextAsync(Configuration configuration, VideoMetadata video,
        double afterSeconds, Func<IOcrEngine> ocrEngineFactory, string? profilePath = null,
        IProgress<double>? progress = null, bool ownDeath = false,
        CancellationToken token = default)
    {
        if (!double.IsFinite(afterSeconds) || afterSeconds < 0)
            throw new ArgumentException("Startzeit liegt außerhalb des Videos.");
        if (afterSeconds >= video.DurationSeconds) return null;

        var (active, _) = PersonalProfileStore.Apply(configuration, profilePath,
            video.Width, video.Height);
        var templateMode = active.Detection.Mode == "template";
        var region = (templateMode
            ? active.ResolveDetectionRegion(video.Width, video.Height)
            : active.ResolveKillfeedRegion(video.Width, video.Height)).ToPixelRegion();
        var sampleStep = ChangeDetector.SampleStep(video.Fps, active.Analysis.SamplesPerSecond);
        if (!templateMode)
            return await ByOcrAsync(active, video, region, sampleStep, afterSeconds,
                ocrEngineFactory, progress, ownDeath, token);
        if (ownDeath)
            throw new ConfigurationException(
                "Tode werden nur von der OCR-Erkennung gefunden, nicht im Vorlagen-Modus.");

        // Template events need consecutive confirmations, so this mode stays a single pass.
        var name = Path.GetFileName(video.Path);
        using var matcher = new TemplateMatcher(active.Detection, video.Width);
        var grouper = new TemplateEventGrouper(active.Detection, name);
        await foreach (var frame in FrameStream.ReadEveryAsync(video, region, sampleStep, token,
                           afterSeconds))
            using (frame)
            {
                progress?.Report(frame.TimestampSeconds);
                if (grouper.Feed(matcher.Match(frame.Image, frame.TimestampSeconds, frame.Number),
                        frame.TimestampSeconds) is { } grouped)
                    return grouped.TimestampSeconds;
            }
        return grouper.Finish()?.TimestampSeconds;
    }

    private static async Task<double?> ByOcrAsync(Configuration active, VideoMetadata video,
        PixelRegion region, int sampleStep, double afterSeconds, Func<IOcrEngine> ocrEngineFactory,
        IProgress<double>? progress, bool ownDeath, CancellationToken token)
    {
        var name = Path.GetFileName(video.Path);
        var settings = active.ToDetectionSettings();
        // One engine for both passes. Spreading the frames over several engines was measured to be
        // slower, because a single OCR call already keeps every core busy.
        using var engine = ocrEngineFactory();

        // The first accepted kill and the sampled position before it, or null.
        async Task<(double Hit, double Previous)?> Scan(IAsyncEnumerable<SampledFrame> frames,
            double start, bool skipUnchanged)
        {
            var detector = new KillfeedDetector(settings);
            var deduplicator = new EventDeduplicator(settings);
            using var change = new ChangeDetector(active.Analysis.ChangeThreshold);
            // The killfeed still shows the entries of the position we start from; they go into the
            // deduplicator so only an entry appearing afterwards is proposed.
            var baseline = true;
            var previous = start;
            await foreach (var frame in frames)
                using (frame)
                {
                    progress?.Report(frame.TimestampSeconds);
                    if (skipUnchanged && !change.Changed(frame.Image).Changed) continue;
                    var lines = await engine.ReadAsync(frame.Image, region, token);
                    var detected = detector.Detect(lines, region, frame.TimestampSeconds,
                        frame.Number, name);
                    foreach (var candidate in ownDeath
                                 ? detected.Rejections
                                     .Where(rejection => rejection.Reason == KillfeedDetector.VictimSide)
                                     .Select(rejection => AsDeath(rejection, frame, name))
                                 : detected.Candidates)
                        if (deduplicator.Accept(candidate) && !baseline)
                            return (candidate.TimestampSeconds, previous);
                    baseline = false;
                    previous = frame.TimestampSeconds;
                }
            return null;
        }

        // A killfeed entry stays visible for about the deduplication window, so a coarse pass
        // with at most that distance between two frames cannot skip a kill and needs a fraction
        // of the OCR calls. Keyframes are the cheapest coarse pass because FFmpeg then decodes
        // nothing in between; a recording whose keyframes are further apart than an entry lives
        // falls back to a fixed step, and that one keeps half the window as a margin. Change
        // detection is pointless between coarse frames, they always differ.
        var lifetime = active.Deduplication.DuplicateWindowSeconds;
        var keyframes = await new VideoService().KeyframesAsync(video.Path, afterSeconds, token);
        var widestGap = keyframes.Count == 0 ? double.PositiveInfinity
            : keyframes.Zip(keyframes.Skip(1)).Aggregate(keyframes[0] - afterSeconds,
                (widest, pair) => Math.Max(widest, pair.Second - pair.First));
        var closeEnough = widestGap <= lifetime;
        var coarseStep = Math.Max(sampleStep,
            (int)Math.Round(video.Fps * Math.Max(1, lifetime / 2)));
        // An intra-only codec hands out every frame instead of the keyframes; thinning them out
        // keeps the OCR cheap, and the widest keyframe gap is the room left for that.
        var coarse = await Scan(closeEnough
                ? FrameStream.ReadKeyframesAsync(video, region, token, afterSeconds,
                    lifetime - widestGap)
                : FrameStream.ReadEveryAsync(video, region, coarseStep, token, afterSeconds),
            afterSeconds, skipUnchanged: false);
        if (coarse is not { } found) return null;
        if (!closeEnough && coarseStep == sampleStep) return found.Hit;
        var fine = await Scan(FrameStream.ReadEveryAsync(video, region, sampleStep, token,
            found.Previous, found.Hit), found.Previous, active.Analysis.EnableChangeDetection);
        return fine?.Hit ?? found.Hit;
    }

    /// <summary>
    /// The rejected row as an event, so the same deduplicator can tell whether the killfeed still
    /// shows the entry of the previous frame. Only text, type and time are compared.
    /// </summary>
    private static KillCandidate AsDeath(DetectionRejection rejection, SampledFrame frame,
        string source) =>
        new(rejection.TimestampSeconds, "", "", null, null, rejection.RowText, 1,
            rejection.Score, frame.Number, source, 0, "death");
}
