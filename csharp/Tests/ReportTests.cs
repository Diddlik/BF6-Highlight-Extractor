using System.Text.Json;
using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>The written reports must match the Python baseline byte for byte.</summary>
public sealed class ReportTests : IDisposable
{
    private static readonly string Baseline =
        Path.Combine(AppContext.BaseDirectory, "reports-baseline");
    private readonly string folder = Directory.CreateTempSubdirectory("bf6-reports-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Target(string name) => Path.Combine(folder, name);

    [Fact]
    public void EventsJsonMatchesPythonBaseline()
    {
        var events = Reports.ReadEventsJson(Path.Combine(Baseline, "events.json"));
        Assert.Equal(7, events.Count);
        Reports.WriteEventsJson(Target("events.json"), events);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Baseline, "events.json")),
            File.ReadAllBytes(Target("events.json")));
    }

    [Fact]
    public void EventsCsvMatchesPythonBaseline()
    {
        Reports.WriteEventsCsv(Target("events.csv"),
            Reports.ReadEventsJson(Path.Combine(Baseline, "events.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Baseline, "events.csv")),
            File.ReadAllBytes(Target("events.csv")));
    }

    [Fact]
    public void SegmentsJsonMatchesPythonBaseline()
    {
        var path = Path.Combine(Baseline, "segments.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var segments = document.RootElement.EnumerateArray().Select(segment => new ClipSegment(
            segment.GetProperty("start_seconds").GetDouble(),
            segment.GetProperty("end_seconds").GetDouble(),
            ReadEvents(segment.GetProperty("events")),
            segment.GetProperty("clip_type").GetString()!)).ToArray();
        Assert.NotEmpty(segments);
        Reports.WriteSegmentsJson(Target("segments.json"), segments);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Target("segments.json")));
    }

    [Fact]
    public void SummaryJsonMatchesPythonBaseline()
    {
        var path = Path.Combine(Baseline, "summary.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var data = document.RootElement;
        Reports.WriteSummaryJson(Target("summary.json"), new AnalysisSummary(
            data.GetProperty("source_video").GetString()!,
            data.GetProperty("duration_seconds").GetDouble(),
            data.GetProperty("frames_sampled").GetInt32(),
            data.GetProperty("ocr_calls").GetInt32(),
            data.GetProperty("template_checks").GetInt32(),
            data.GetProperty("kills_detected").GetInt32(),
            data.GetProperty("clips_created").GetInt32(),
            data.GetProperty("processing_duration_seconds").GetDouble(),
            data.GetProperty("average_processing_fps").GetDouble(),
            data.GetProperty("interrupted").GetBoolean()));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Target("summary.json")));
    }

    [Fact]
    public void EmptyReportsStayValidJson()
    {
        Reports.WriteEventsJson(Target("events.json"), []);
        Reports.WriteSegmentsJson(Target("segments.json"), []);
        Assert.Equal("[]", File.ReadAllText(Target("events.json")));
        Assert.Equal("[]", File.ReadAllText(Target("segments.json")));
        Reports.WriteEventsCsv(Target("events.csv"), []);
        Assert.Equal(string.Join(',', Reports.CsvColumns) + "\r\n", File.ReadAllText(Target("events.csv")));
    }

    [Fact]
    public void RoundingAndFormattingFollowPython()
    {
        var item = Candidate(0.9397846460342407, 90.90909090909091, 12.0004999, "a,b \"c\"\r\nd");
        Reports.WriteEventsJson(Target("events.json"), [item]);
        var text = File.ReadAllText(Target("events.json"));
        Assert.Contains("\"confidence\": 0.9398,", text);
        Assert.Contains("\"similarity_score\": 90.91,", text);
        Assert.Contains("\"timestamp_seconds\": 12.0,", text);
        Assert.Contains("\"timestamp_formatted\": \"00:00:12.000\",", text);
        Assert.Contains("\"weapon_text\": null,", text);

        Reports.WriteEventsCsv(Target("events.csv"), [item]);
        Assert.EndsWith("12.0,00:00:12.000,BulletWaltz,BulletWaltz,,,\"a,b \"\"c\"\"\r\nd\""
            + ",0.9398,90.91,7,clip.mp4,kill,ocr\r\n", File.ReadAllText(Target("events.csv")));
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(-5, "00:00:00.000")]
    [InlineData(3723.4567, "01:02:03.457")]
    [InlineData(86400, "24:00:00.000")]
    public void TimestampsUsePythonFormat(double seconds, string expected) =>
        Assert.Equal(expected, Reports.FormatTimestamp(seconds));

    [Fact]
    public void FailedWriteLeavesNoTemporaryFile()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Reports.WriteEventsJson(Target("events.json"), [Candidate(double.NaN, 100, 1, "x")]));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void ReportsAreReplacedOnRepeatedRuns()
    {
        Reports.WriteEventsJson(Target("events.json"), [Candidate(0.5, 100, 1, "x")]);
        Reports.WriteEventsJson(Target("events.json"), []);
        Assert.Equal("[]", File.ReadAllText(Target("events.json")));
    }

    [Fact]
    public void InvalidEventFilesAreReported()
    {
        File.WriteAllText(Target("broken.json"), "{}");
        Assert.Throws<IOException>(() => Reports.ReadEventsJson(Target("broken.json")));
        File.WriteAllText(Target("wrong.json"), """[{"timestamp_seconds": "spät"}]""");
        Assert.Throws<IOException>(() => Reports.ReadEventsJson(Target("wrong.json")));
    }

    private static KillCandidate Candidate(double confidence, double similarity, double timestamp, string raw) =>
        new(timestamp, "BulletWaltz", "BulletWaltz", null, null, raw, confidence, similarity, 7,
            "clip.mp4", 0);

    private static KillCandidate[] ReadEvents(JsonElement events) =>
        events.EnumerateArray().Select(item => new KillCandidate(
            item.GetProperty("timestamp_seconds").GetDouble(),
            item.GetProperty("player_name_detected").GetString()!,
            item.GetProperty("player_name_configured").GetString()!,
            item.GetProperty("opponent_name").GetString(),
            item.GetProperty("weapon_text").GetString(),
            item.GetProperty("raw_text").GetString()!,
            item.GetProperty("confidence").GetDouble(),
            item.GetProperty("similarity_score").GetDouble(),
            item.GetProperty("frame_number").GetInt32(),
            item.GetProperty("source_video").GetString()!, 0,
            item.GetProperty("event_type").GetString()!,
            item.GetProperty("detection_method").GetString()!)).ToArray();
}
