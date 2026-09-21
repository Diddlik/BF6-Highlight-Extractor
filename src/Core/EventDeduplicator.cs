namespace Bf6Highlights;

public sealed class EventDeduplicator
{
    private readonly DetectionSettings settings;
    private readonly List<Recent> recent = [];
    private readonly Dictionary<string, double> lastTimestamp = new(StringComparer.Ordinal);
    private sealed record Recent(KillCandidate Event, string Text, string Opponent)
    { public double LastSeen { get; set; } = Event.TimestampSeconds; }

    public EventDeduplicator(DetectionSettings settings) { settings.Validate(); this.settings = settings; }
    private string Normalize(string text) => NameMatching.Normalize(text, settings.StripSpecial, settings.Confusables);

    public bool Accept(KillCandidate candidate)
    {
        var now = candidate.TimestampSeconds;
        if (!double.IsFinite(now) || now < 0 || string.IsNullOrWhiteSpace(candidate.SourceVideo))
            throw new ArgumentException("Ungültiger Ereigniszeitpunkt oder Quelle.");
        if (lastTimestamp.TryGetValue(candidate.SourceVideo, out var previous) && now < previous)
            throw new ArgumentException("Ereignisse müssen pro Quelle zeitlich geordnet sein.");
        lastTimestamp[candidate.SourceVideo] = now;
        recent.RemoveAll(r => r.Event.SourceVideo == candidate.SourceVideo && now - r.LastSeen > settings.DuplicateWindowSeconds);
        var text = Normalize(candidate.RawText);
        var opponent = Normalize(candidate.OpponentName ?? "");
        foreach (var entry in recent)
        {
            if (entry.Event.SourceVideo != candidate.SourceVideo ||
                entry.Event.PlayerNameConfigured != candidate.PlayerNameConfigured ||
                entry.Event.EventType != candidate.EventType) continue;
            if (NameMatching.TokenSortRatio(text, entry.Text) < settings.TextThreshold) continue;
            if (opponent.Length != 0 && entry.Opponent.Length != 0 &&
                NameMatching.TokenSortRatio(opponent, entry.Opponent) < settings.OpponentThreshold) continue;
            entry.LastSeen = now;
            return false;
        }
        recent.Add(new(candidate, text, opponent));
        return true;
    }
}
