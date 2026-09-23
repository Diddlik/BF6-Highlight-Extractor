namespace Bf6Highlights;

public sealed class EventDeduplicator
{
    private readonly DetectionSettings settings;
    private readonly List<Recent> recent = [];
    private readonly Dictionary<string, double> lastTimestamp = new(StringComparer.Ordinal);
    // Every spelling the OCR produced for the opponent of one killfeed row ("darkrabbit", "derkrabbit").
    private sealed record Recent(KillCandidate Event, string Text, List<string> Opponents)
    { public double LastSeen { get; set; } = Event.TimestampSeconds; }

    // A ping row stays on screen for seconds and its sentence is read in almost every frame; in the odd
    // frame only the name survives. Measured: the frame before such a slip always showed the ping.
    private const double PingShadowSeconds = 2;
    private readonly Dictionary<string, double> lastPing = new(StringComparer.Ordinal);

    public EventDeduplicator(DetectionSettings settings) { settings.Validate(); this.settings = settings; }
    private string Normalize(string text) => NameMatching.Normalize(text, settings.StripSpecial, settings.Confusables);

    /// <summary>Feeds the rejections of a frame, so a ping read without its sentence is not taken for a kill.</summary>
    public void Observe(IEnumerable<DetectionRejection> rejections, string source)
    {
        foreach (var rejection in rejections)
            if (rejection.Reason == KillfeedDetector.PingMessage) lastPing[source] = rejection.TimestampSeconds;
    }

    public bool Accept(KillCandidate candidate)
    {
        var now = candidate.TimestampSeconds;
        if (!double.IsFinite(now) || now < 0 || string.IsNullOrWhiteSpace(candidate.SourceVideo))
            throw new ArgumentException("Ungültiger Ereigniszeitpunkt oder Quelle.");
        if (lastTimestamp.TryGetValue(candidate.SourceVideo, out var previous) && now < previous)
            throw new ArgumentException("Ereignisse müssen pro Quelle zeitlich geordnet sein.");
        lastTimestamp[candidate.SourceVideo] = now;
        if (candidate is { EventType: "kill", DetectionMethod: "ocr", OpponentName: null, WeaponText: null }
            && lastPing.TryGetValue(candidate.SourceVideo, out var ping) && now - ping <= PingShadowSeconds)
            return false;
        recent.RemoveAll(r => r.Event.SourceVideo == candidate.SourceVideo && now - r.LastSeen > settings.DuplicateWindowSeconds);
        var text = Normalize(candidate.RawText);
        var opponent = Normalize(candidate.OpponentName ?? "");
        foreach (var entry in recent)
        {
            if (entry.Event.SourceVideo != candidate.SourceVideo ||
                entry.Event.PlayerNameConfigured != candidate.PlayerNameConfigured ||
                entry.Event.EventType != candidate.EventType) continue;
            // The weapon icon and the own name come out as noise ("DA 62", "BullefWalfz"), so the row text
            // alone misses repeats. The same opponent cannot die twice within the window, and a row whose
            // opponent was not read cannot be told apart from the one already on screen.
            var repeat = NameMatching.TokenSortRatio(text, entry.Text) >= settings.TextThreshold
                || opponent.Length == 0 || entry.Opponents.Count == 0
                || entry.Opponents.Any(known => NameMatching.TokenSortRatio(opponent, known) >= settings.OpponentThreshold);
            if (!repeat) continue;
            entry.LastSeen = now;
            if (opponent.Length != 0) entry.Opponents.Add(opponent);
            return false;
        }
        recent.Add(new(candidate, text, opponent.Length == 0 ? [] : [opponent]));
        return true;
    }
}
