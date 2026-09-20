using System.Text.Json;
using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class DetectionGoldenTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static IEnumerable<object[]> Cases(string section)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "detection-golden.json")));
        return document.RootElement.GetProperty(section).EnumerateArray()
            .Select(c => new object[] { c.GetProperty("id").GetString()!, c.Clone() }).ToArray();
    }

    [Theory, MemberData(nameof(Cases), "normalization")]
    public void NormalizeMatchesPython(string id, JsonElement data)
    {
        Assert.Equal(id, data.GetProperty("id").GetString());
        var input = data.GetProperty("input");
        Assert.Equal(data.GetProperty("expected").GetProperty("normalized").GetString(),
            NameMatching.Normalize(input.GetProperty("text").GetString()!,
                !input.TryGetProperty("strip_special", out var special) || special.GetBoolean(),
                !input.TryGetProperty("confusables", out var confusable) || confusable.GetBoolean()));
    }

    [Theory, MemberData(nameof(Cases), "similarity")]
    public void SimilaritiesMatchRapidFuzz(string id, JsonElement data)
    {
        Assert.Equal(id, data.GetProperty("id").GetString());
        var input = data.GetProperty("input");
        var expected = data.GetProperty("expected");
        var left = input.GetProperty("left").GetString()!;
        var right = input.GetProperty("right").GetString()!;
        Assert.Equal(expected.GetProperty("ratio").GetDouble(), NameMatching.Ratio(left, right), 10);
        Assert.Equal(expected.GetProperty("token_sort_ratio").GetDouble(), NameMatching.TokenSortRatio(left, right), 10);
    }

    [Theory, MemberData(nameof(Cases), "detector")]
    public void DetectorMatchesPythonIncludingRejectionReasons(string id, JsonElement data)
    {
        Assert.Equal(id, data.GetProperty("id").GetString());
        var input = data.GetProperty("input");
        var lines = input.GetProperty("lines").EnumerateArray().Select(line =>
        {
            var box = line.GetProperty("bounding_box").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            return new OcrLine(line.GetProperty("text").GetString()!, line.GetProperty("confidence").GetDouble(),
                new(box[0], box[1], box[2], box[3]));
        }).ToArray();
        var detector = new KillfeedDetector(Settings(data.GetProperty("config")));
        var result = detector.Detect(lines, new(0, 0, input.GetProperty("crop_width").GetInt32(), 500),
            input.GetProperty("timestamp_seconds").GetDouble(), input.GetProperty("frame_number").GetInt32(),
            input.GetProperty("source_video").GetString()!);
        Equivalent(data.GetProperty("expected").GetProperty("events"), JsonSerializer.SerializeToElement(result.Candidates, Json));
        Equivalent(data.GetProperty("expected").GetProperty("rejections"), JsonSerializer.SerializeToElement(result.Rejections, Json));
    }

    [Theory, MemberData(nameof(Cases), "deduplication")]
    public void DedupMatchesSlidingWindowReference(string id, JsonElement data)
    {
        Assert.Equal(id, data.GetProperty("id").GetString());
        var config = data.GetProperty("config");
        var dedup = new EventDeduplicator(new DetectionSettings
        {
            PlayerNames = ["Diddlik"], DuplicateWindowSeconds = config.GetProperty("duplicate_window_seconds").GetDouble(),
            TextThreshold = config.GetProperty("text_similarity_threshold").GetDouble(),
            OpponentThreshold = config.GetProperty("opponent_similarity_threshold").GetDouble(),
        });
        var events = data.GetProperty("input").GetProperty("events").Deserialize<KillCandidate[]>(Json)!;
        var accepted = events.Select((e, index) => (e, index)).Where(pair => dedup.Accept(pair.e)).ToArray();
        Assert.Equal(data.GetProperty("expected").GetProperty("accepted_indices").EnumerateArray().Select(n => n.GetInt32()),
            accepted.Select(pair => pair.index));
        Equivalent(data.GetProperty("expected").GetProperty("events"),
            JsonSerializer.SerializeToElement(accepted.Select(pair => pair.e), Json));
    }

    private static DetectionSettings Settings(JsonElement config)
    {
        JsonElement Field(string path)
        {
            var node = config;
            foreach (var name in path.Split('.'))
                if (!node.TryGetProperty(name, out node)) return default;
            return node;
        }
        double Number(string path, double fallback) => Field(path).ValueKind == JsonValueKind.Undefined ? fallback : Field(path).GetDouble();
        string Text(string path, string fallback) => Field(path).ValueKind == JsonValueKind.Undefined ? fallback : Field(path).GetString()!;
        bool Flag(string path) => Field(path).ValueKind == JsonValueKind.Undefined || Field(path).GetBoolean();
        return new()
        {
            PlayerNames = Field("player.names").Deserialize<string[]>()!,
            NameThreshold = Number("ocr.player_name_similarity_threshold", 82),
            MinimumConfidence = Number("ocr.minimum_confidence", .45),
            StripSpecial = Flag("ocr.normalize_strip_special"), Confusables = Flag("ocr.normalize_confusables"),
            KillerSide = Text("killfeed.killer_side", "left"), Direction = Text("killfeed.direction", "left_to_right"),
            KillerMinRatio = Number("killfeed.killer_region.x_min_ratio", 0),
            KillerMaxRatio = Number("killfeed.killer_region.x_max_ratio", .48),
        };
    }

    private static void Equivalent(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
                foreach (var property in expected.EnumerateObject()) Equivalent(property.Value, actual.GetProperty(property.Name));
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var index = 0; index < expected.GetArrayLength(); index++) Equivalent(expected[index], actual[index]);
                break;
            case JsonValueKind.Number: Assert.Equal(expected.GetDouble(), actual.GetDouble(), 10); break;
            default: Assert.Equal(expected.ToString(), actual.ToString()); break;
        }
    }
}
