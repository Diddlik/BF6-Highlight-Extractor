using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class DetectionSafetyTests
{
    private static DetectionSettings Settings => new() { PlayerNames = ["BulletWaltz"] };
    private static KillCandidate Candidate => new(10, "BulletWaltz", "BulletWaltz", "Enemy", null,
        "BulletWaltz Enemy", .9, 100, 600, "a.mp4", 10);

    [Fact]
    public void SourcesPlayersAndTypesAreIsolated()
    {
        var dedup = new EventDeduplicator(Settings);
        Assert.True(dedup.Accept(Candidate));
        Assert.True(dedup.Accept(Candidate with { SourceVideo = "b.mp4" }));
        Assert.True(dedup.Accept(Candidate with { PlayerNameConfigured = "Other" }));
        Assert.True(dedup.Accept(Candidate with { EventType = "headshot" }));
        Assert.False(dedup.Accept(Candidate with { TimestampSeconds = 11 }));
    }

    [Fact]
    public void OutOfOrderEventsMustNotSilentlyCorruptSlidingWindow()
    {
        var dedup = new EventDeduplicator(Settings);
        dedup.Accept(Candidate);
        Assert.Throws<ArgumentException>(() => dedup.Accept(Candidate with { TimestampSeconds = 9 }));
        Assert.True(dedup.Accept(Candidate with { TimestampSeconds = 1, SourceVideo = "b.mp4" }));
    }

    [Fact]
    public void SourceCoordinatesAreTranslatedBackToCropCoordinates()
    {
        OcrLine[] lines = [new("BulletWaltz", .9, new(2010, 190, 100, 20)), new("Enemy", .9, new(2330, 190, 70, 20))];
        var detector = new KillfeedDetector(Settings);
        Assert.Single(detector.Detect(lines, new(2000, 170, 600, 300), 10, 600, "video").Candidates);
        Assert.Empty(detector.Detect(lines, new(0, 0, 600, 300), 10, 600, "video").Candidates);
    }

    [Fact]
    public void EmptyAndUnmatchableRowsDoNotThrowAtZeroThreshold()
    {
        var detector = new KillfeedDetector(Settings with { NameThreshold = 0 });
        Assert.Empty(detector.Detect([new("   ", .9, new(0, 0, 50, 20))], new(0, 0, 600, 300), 0, 0, "video").Candidates);
        Assert.Empty(detector.Detect([new("???", .9, new(0, 0, 50, 20))], new(0, 0, 600, 300), 0, 0, "video").Candidates);
    }

    [Fact]
    public void InvalidSettingsAndNonFiniteConfidenceAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new KillfeedDetector(Settings with { NameThreshold = double.NaN }));
        var detector = new KillfeedDetector(Settings);
        Assert.Throws<ArgumentException>(() => detector.Detect([new("BulletWaltz", double.NaN, new(0, 0, 50, 20))],
            new(0, 0, 600, 300), 0, 0, "video"));
    }
}
