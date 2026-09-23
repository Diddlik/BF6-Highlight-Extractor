namespace Bf6Highlights;

public sealed record DetectionSettings
{
    public required string[] PlayerNames { get; init; }
    public double NameThreshold { get; init; } = 82;
    public double MinimumConfidence { get; init; } = 0.45;
    public bool StripSpecial { get; init; } = true;
    public bool Confusables { get; init; } = true;
    public string KillerSide { get; init; } = "left";
    public string Direction { get; init; } = "left_to_right";
    public double KillerMinRatio { get; init; } = 0;
    public double KillerMaxRatio { get; init; } = 0.48;
    public double DuplicateWindowSeconds { get; init; } = 8;
    public double TextThreshold { get; init; } = 88;
    public double OpponentThreshold { get; init; } = 85;

    public void Validate()
    {
        if (PlayerNames is null || PlayerNames.Length == 0 || PlayerNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Mindestens ein nicht leerer Spielername ist erforderlich.");
        if (KillerSide is not ("left" or "right") || Direction is not ("left_to_right" or "right_to_left"))
            throw new ArgumentException("Ungültige Killer-Seite oder Leserichtung.");
        if (!double.IsFinite(NameThreshold) || NameThreshold is < 0 or > 100 ||
            !double.IsFinite(MinimumConfidence) || MinimumConfidence is < 0 or > 1 ||
            !double.IsFinite(KillerMinRatio) || !double.IsFinite(KillerMaxRatio) ||
            KillerMinRatio < 0 || KillerMaxRatio > 1 || KillerMinRatio >= KillerMaxRatio ||
            !double.IsFinite(DuplicateWindowSeconds) || DuplicateWindowSeconds < 0 ||
            !double.IsFinite(TextThreshold) || TextThreshold is < 0 or > 100 ||
            !double.IsFinite(OpponentThreshold) || OpponentThreshold is < 0 or > 100)
            throw new ArgumentException("Ungültiger Erkennungs-Schwellwert.");
    }
}

public sealed record KillCandidate(double TimestampSeconds, string PlayerNameDetected,
    string PlayerNameConfigured, string? OpponentName, string? WeaponText, string RawText,
    double Confidence, double SimilarityScore, int FrameNumber, string SourceVideo, int RowY,
    string EventType = "kill", string DetectionMethod = "ocr");
public sealed record DetectionRejection(string RowText, string Reason, double Score, double TimestampSeconds);
public sealed record DetectionResult(IReadOnlyList<KillCandidate> Candidates,
    IReadOnlyList<DetectionRejection> Rejections);

public sealed class KillfeedDetector
{
    /// <summary>
    /// Rejection reason for a killfeed row that carries the player's name on the victim side.
    /// Such a row is a confirmed death of the player, not a miss.
    /// </summary>
    public const string VictimSide = "name_not_on_killer_side";

    /// <summary>
    /// Rejection reason for a ping or marking ("BulletWaltz hat eine Gefahr gepingt", "Ping abgebrochen").
    /// These share the killfeed with kills, but a kill row holds only names and the weapon icon, never a sentence.
    /// </summary>
    public const string PingMessage = "ping_message";

    private readonly DetectionSettings settings;
    private readonly (string Original, string Normalized)[] names;
    private sealed record Token(string Text, double Confidence, int X0, int X1, int Y, int Height, OcrLine Line)
    { public double Center => (X0 + (double)X1) / 2; }

    public KillfeedDetector(DetectionSettings settings)
    {
        settings.Validate();
        this.settings = settings;
        names = settings.PlayerNames.Select(n => (n.Trim(), Normalize(n.Trim()))).ToArray();
    }

    private string Normalize(string value) => NameMatching.Normalize(value, settings.StripSpecial, settings.Confusables);

    // Thresholds tolerate the usual OCR damage seen in recordings: "gepngf", "het cine", "hai ein".
    private bool IsPing(IEnumerable<Token> matched, IEnumerable<Token> others)
    {
        // The OCR glues "Ping" onto the name at times: "BulletWaltzPing abgebrochen".
        var longest = names.Max(name => name.Normalized.Length);
        if (Normalize(string.Join(' ', matched.Select(t => t.Text))).Split(' ')
            .Any(word => word.Length > longest && word.EndsWith("ping", StringComparison.Ordinal))) return true;
        var words = Normalize(string.Join(' ', others.Select(t => t.Text)))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            if (word.EndsWith("ping", StringComparison.Ordinal) || NameMatching.Ratio(word, "gepingt") >= 70
                || NameMatching.Ratio(word, "pinged") >= 75 || NameMatching.Ratio(word, "abgebrochen") >= 75)
                return true;
            if (index + 1 < words.Length && NameMatching.Ratio(word, "hat") >= 66
                && (words[index + 1].StartsWith("ein", StringComparison.Ordinal)
                    || NameMatching.Ratio(words[index + 1], "eine") >= 75)) return true;
        }
        return false;
    }

    public DetectionResult Detect(IReadOnlyList<OcrLine> lines, PixelRegion region,
        double timestamp, int frameNumber, string source)
    {
        if (!double.IsFinite(timestamp) || timestamp < 0 || frameNumber < 0 ||
            region.Width <= 0 || region.Height <= 0 || string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Ungültiger Frame-/Quellbezug.");
        var tokens = new List<Token>();
        foreach (var line in lines)
        {
            var box = line.BoundingBox;
            if (!double.IsFinite(line.Confidence) || line.Confidence is < 0 or > 1 ||
                box.Width <= 0 || box.Height <= 0) throw new ArgumentException("Ungültige OCR-Box.");
            var words = NameMatching.Words(line.Text);
            var length = words.Sum(w => w.EnumerateRunes().Count()) + words.Length - 1;
            var cursor = 0;
            foreach (var word in words)
            {
                var start = box.X - region.X + (int)((double)box.Width * cursor / length);
                cursor += word.EnumerateRunes().Count();
                var end = box.X - region.X + (int)((double)box.Width * cursor / length);
                cursor++;
                tokens.Add(new(word, line.Confidence, start, Math.Max(start + 1, end),
                    box.Y - region.Y + box.Height / 2, box.Height, line));
            }
        }
        var rows = new List<List<Token>>();
        foreach (var token in tokens.OrderBy(t => t.Y).ThenBy(t => t.X0))
        {
            if (rows.Count == 0 || Math.Abs(token.Y - rows[^1][^1].Y) >
                Math.Max(1, .6 * Math.Max(token.Height, rows[^1][^1].Height))) rows.Add([]);
            rows[^1].Add(token);
        }
        var events = new List<KillCandidate>();
        var rejected = new List<DetectionRejection>();
        foreach (var unsorted in rows)
        {
            var row = unsorted.OrderBy(t => t.X0).ToArray();
            var score = -1d;
            var first = 0;
            var last = 0;
            var configured = "";
            for (var size = 1; size <= 2; size++)
                for (var start = 0; start + size <= row.Length; start++)
                {
                    var text = Normalize(string.Join(' ', row[start..(start + size)].Select(t => t.Text)));
                    if (text.Length == 0) continue;
                    foreach (var name in names)
                    {
                        var candidate = NameMatching.Ratio(text, name.Normalized);
                        if (candidate <= score) continue;
                        (score, first, last, configured) = (candidate, start, start + size, name.Original);
                    }
                }
            if (score < 0 || score < settings.NameThreshold - 15) continue;
            var raw = string.Join(' ', (settings.Direction == "right_to_left" ? row.Reverse() : row).Select(t => t.Text));
            var matched = row[first..last];
            var confidence = matched.Min(t => t.Confidence);
            var center = matched.Average(t => t.Center);
            var other = row[..first].Concat(row[last..]).ToArray();
            var side = center / region.Width >= settings.KillerMinRatio && center / region.Width <= settings.KillerMaxRatio
                && (other.Length == 0 || (settings.KillerSide == "left"
                    ? center < other.Average(t => t.Center) : center > other.Average(t => t.Center)));
            var reason = score < settings.NameThreshold ? "similarity_below_threshold"
                : confidence < settings.MinimumConfidence ? "ocr_confidence_below_minimum"
                : IsPing(matched, other) ? PingMessage
                : !side ? VictimSide : null;
            if (reason is not null) { rejected.Add(new(raw, reason, score, timestamp)); continue; }
            var rest = settings.KillerSide == "left" ? row[last..] : row[..first];
            // A victim name with a space ("Mahmoud Samy", or "Bad Trip95" as the OCR splits it) arrives as a
            // line of its own; only when that line also holds the own name is the last word the victim.
            var edge = rest.Length == 0 ? null : settings.KillerSide == "left" ? rest[^1] : rest[0];
            var victim = edge is null ? []
                : matched.Any(t => ReferenceEquals(t.Line, edge.Line)) ? [edge]
                : rest.Where(t => ReferenceEquals(t.Line, edge.Line)).ToArray();
            var opponent = victim.Length == 0 ? null : string.Join(' ', victim.Select(t => t.Text));
            var middle = rest.Except(victim).ToArray();
            var weapon = string.Join(' ', middle.Select(t => t.Text));
            events.Add(new(timestamp, string.Join(' ', matched.Select(t => t.Text)), configured, opponent,
                weapon.Length == 0 ? null : weapon, raw, confidence, score, frameNumber, source,
                (int)row.Average(t => t.Y)));
        }
        return new(events, rejected);
    }
}
