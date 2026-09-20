namespace Bf6Highlights;

/// <summary>Kill timestamps to clamped, merged clip segments (Python clips/segment_builder.py).</summary>
public static class SegmentBuilder
{
    public static List<ClipSegment> Build(IReadOnlyList<KillCandidate> events, ClipSettings clips,
        double videoDurationSeconds)
    {
        if (!double.IsFinite(videoDurationSeconds) || videoDurationSeconds < 0)
            throw new ArgumentException("Ungültige Videolänge.");
        if (events.Any(e => !double.IsFinite(e.TimestampSeconds) || e.TimestampSeconds < 0))
            throw new ArgumentException("Ungültiger Ereigniszeitpunkt.");
        return Merge(events.OrderBy(e => e.TimestampSeconds).Select(e => new ClipSegment(
            Math.Max(0, e.TimestampSeconds - clips.SecondsBefore),
            Math.Min(videoDurationSeconds, e.TimestampSeconds + clips.SecondsAfter),
            [e], ClipTypeFor(1))).ToArray(), clips.MergeGapSeconds);
    }

    /// <summary>Merges segments that overlap or sit closer together than the gap.</summary>
    public static List<ClipSegment> Merge(IReadOnlyList<ClipSegment> segments, double mergeGapSeconds)
    {
        if (!double.IsFinite(mergeGapSeconds) || mergeGapSeconds < 0)
            throw new ArgumentException("Ungültiger Zusammenführungsabstand.");
        var merged = new List<(double Start, double End, List<KillCandidate> Events)>();
        foreach (var segment in segments.OrderBy(s => s.StartSeconds))
            if (merged.Count > 0 && segment.StartSeconds - merged[^1].End <= mergeGapSeconds)
            {
                var current = merged[^1];
                merged[^1] = (current.Start, Math.Max(current.End, segment.EndSeconds), current.Events);
                current.Events.AddRange(segment.Events);
            }
            else merged.Add((segment.StartSeconds, segment.EndSeconds, [.. segment.Events]));
        return merged.Select(entry => new ClipSegment(entry.Start, entry.End,
            [.. entry.Events.OrderBy(e => e.TimestampSeconds)], ClipTypeFor(entry.Events.Count))).ToList();
    }

    public static string ClipTypeFor(int killCount) => killCount switch
    {
        1 => "single_kill", 2 => "double_kill", 3 => "triple_kill", _ => "multi_kill",
    };
}
