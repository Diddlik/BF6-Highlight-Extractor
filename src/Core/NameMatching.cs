using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bf6Highlights;

public static class NameMatching
{
    private static readonly Dictionary<int, string> Casefold = LoadCasefold();

    private static Dictionary<int, string> LoadCasefold()
    {
        using var file = typeof(NameMatching).Assembly.GetManifestResourceStream("casefold.json")!;
        using var document = JsonDocument.Parse(file);
        return document.RootElement.GetProperty("map").Deserialize<Dictionary<int, string>>()!;
    }

    internal static bool WhiteSpace(Rune rune) => Rune.IsWhiteSpace(rune) || rune.Value is >= 0x1c and <= 0x1f;

    internal static string[] Words(string text) => string.Concat(text.EnumerateRunes()
        .Select(r => WhiteSpace(r) ? " " : r.ToString())).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string Normalize(string text, bool stripSpecial = true, bool confusables = true)
    {
        var folded = new StringBuilder();
        foreach (var original in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var rune = confusables ? original.Value switch
            {
                '1' or 'I' or '|' or '!' => new Rune('l'),
                '0' => new Rune('o'), '5' => new Rune('s'), _ => original,
            } : original;
            folded.Append(Casefold.GetValueOrDefault(rune.Value, rune.ToString()));
        }
        var clean = new StringBuilder();
        foreach (var rune in folded.ToString().EnumerateRunes())
        {
            var word = Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                || rune.Value == '_';
            clean.Append(stripSpecial && !word && !WhiteSpace(rune) ? " " : rune.ToString());
        }
        return string.Join(' ', Words(clean.ToString()));
    }

    // RapidFuzz fuzz.ratio uses normalized Indel similarity (LCS), not Levenshtein substitution cost 1.
    public static double Ratio(string left, string right)
    {
        var a = left.EnumerateRunes().Select(r => r.Value).ToArray();
        var b = right.EnumerateRunes().Select(r => r.Value).ToArray();
        if (a.Length + b.Length == 0) return 100;
        if (a.Length < b.Length) (a, b) = (b, a);
        var row = new int[b.Length + 1];
        foreach (var rune in a)
        {
            var diagonal = 0;
            for (var j = 1; j <= b.Length; j++)
            {
                var previous = row[j];
                row[j] = rune == b[j - 1] ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                diagonal = previous;
            }
        }
        var sum = a.Length + b.Length;
        return 100d * (1d - (sum - 2 * row[^1]) / (double)sum);
    }

    public static double TokenSortRatio(string left, string right)
    {
        var comparer = Comparer<string>.Create((a, b) => a.EnumerateRunes().Select(r => r.Value).ToArray()
            .AsSpan().SequenceCompareTo(b.EnumerateRunes().Select(r => r.Value).ToArray()));
        return Ratio(string.Join(' ', Words(left).OrderBy(s => s, comparer)),
            string.Join(' ', Words(right).OrderBy(s => s, comparer)));
    }
}
