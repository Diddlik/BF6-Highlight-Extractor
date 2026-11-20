namespace Bf6Highlights;

/// <summary>
/// BF6 confirms each own kill below the crosshair: the victim's name above a label such as "KILLS",
/// "KILL BESTÄTIGT" or "KOPFSCHUSS". A jammer hides the killfeed but not this notice. Colours are
/// not used, because players can change them in the game settings.
/// </summary>
public static partial class KillNotice
{
    public const string Method = "notice";

    /// <summary>
    /// Where the notice's name and label rows sit, or null when the frame is too narrow. Measured at
    /// 2560x1440; the HUD scales with the height and stays centred.
    /// </summary>
    public static PixelRegion? Region(int videoWidth, int videoHeight)
    {
        var scale = videoHeight / 1440d;
        int Scaled(double pixels) => (int)Math.Round(pixels * scale, MidpointRounding.ToEven);
        var region = new PixelRegion(videoWidth / 2 - Scaled(680), videoHeight / 2 + Scaled(225),
            Scaled(500), Scaled(105));
        return region.X < 0 || region.Width <= 0 || region.Height <= 0 ? null : region;
    }

    /// <summary>The kill shown in the notice, or null when its label is not a kill.</summary>
    public static KillCandidate? Detect(IReadOnlyList<OcrLine> lines, PixelRegion region,
        DetectionSettings settings, double timestamp, int frameNumber, string source)
    {
        // The name row fills the upper part of the region, the label row the lower part.
        var split = region.Y + region.Height * 0.6;
        static double Center(OcrLine line) => line.BoundingBox.Y + line.BoundingBox.Height / 2d;
        var label = lines.Where(line => Center(line) >= split && IsKillLabel(line.Text))
            .MaxBy(line => line.Confidence);
        if (label is null) return null;
        // Points ("350") share the name row; a name has letters.
        var name = lines.Where(line => Center(line) < split && line.Text.Any(char.IsLetter)
                && line.Confidence >= settings.MinimumConfidence)
            .OrderBy(line => line.BoundingBox.X).Select(line => line.Text.Trim()).ToArray();
        var raw = string.Join(' ', lines.OrderBy(Center).ThenBy(line => line.BoundingBox.X)
            .Select(line => line.Text));
        return new(timestamp, "", settings.PlayerNames[0], name.Length == 0 ? null : string.Join(' ', name),
            null, raw, label.Confidence, 100, frameNumber, source, 0, DetectionMethod: Method);
    }

    /// <summary>How many kills in a row the notice counts: "KILLS x 2" is 2, a plain kill 1.</summary>
    public static int Count(string text) =>
        CountPattern().Match(text) is { Success: true } match ? int.Parse(match.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture) : 1;

    // "FEIND NIEDERGEHALTEN x 2" shares the row, so the count must follow the kill word.
    [System.Text.RegularExpressions.GeneratedRegex(@"KILLS?\s*[x×]\s*(\d{1,2})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CountPattern();

    // Measured German labels. Not kills: "ASSIST BESTÄTIGT", "SQUAD-WIEDERBELEBUNG",
    // "CASH ERHALTEN", "ÜBERLEBEN".
    private static bool IsKillLabel(string text) =>
        !text.Contains("ASSIST", StringComparison.OrdinalIgnoreCase)
        && NameMatching.Words(text).Select(word => word.ToUpperInvariant()).Any(word =>
            word.StartsWith("KILL", StringComparison.Ordinal) || NameMatching.Ratio(word, "KOPFSCHUSS") >= 75);
}
