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

    // A sequence from a real recording: three kills, each read several times with OCR damage.
    [Fact]
    public void RepeatedReadsOfOneKillfeedRowCountOnce()
    {
        (double Time, string Raw, string? Opponent)[] reads =
        [
            (9.0, "BulletWaltz SyphzonSPW", "SyphzonSPW"),
            (10.33, "BulleiWaltz DA 62 SyphzonSPW", "SyphzonSPW"),
            (37.33, "BulletWaltz DA Real_Chillfe", "Real_Chillfe"),
            (38.0, "BullefWaltz", null),
            (38.33, "BullefWaltz Real_Chillie", "Real_Chillie"),
            (51.67, "BulletWaltz bA dankrabbit", "dankrabbit"),
            (52.67, "BullefWalfz darkrabbit", "darkrabbit"),
            (53.67, "BulleiWalfz Ar derkrabbit", "derkrabbit"),
            (54.0, "BulletWaltz yung_mygeL", "yung_mygeL"),
        ];
        var dedup = new EventDeduplicator(Settings);
        var accepted = reads.Where(read => dedup.Accept(Candidate with
        {
            TimestampSeconds = read.Time, RawText = read.Raw, OpponentName = read.Opponent,
        })).Select(read => read.Opponent ?? "").ToArray();
        Assert.Equal(["SyphzonSPW", "Real_Chillfe", "dankrabbit", "yung_mygeL"], accepted);
    }

    // Rows as the OCR read them from real recordings, damage included.
    [Theory]
    [InlineData("hat eine Gefahr gepingt")]
    [InlineData("hat Wiedereinsatzpunkt gepingt")]
    [InlineData("hat eine Feindeinheit gepngf")]
    [InlineData("het cine endenheft gf")]
    [InlineData("hai ein LMG gesinel")]
    [InlineData("Ping abgebrochen")]
    [InlineData("pinged an enemy")]
    public void PingsAreRejectedInsteadOfCountedAsKills(string message)
    {
        OcrLine[] lines = [new("BulletWaltz " + message, .95, new(10, 20, 400, 20))];
        var result = new KillfeedDetector(Settings).Detect(lines, new(0, 0, 600, 300), 10, 600, "video");
        Assert.Empty(result.Candidates);
        Assert.Equal(KillfeedDetector.PingMessage, Assert.Single(result.Rejections).Reason);
    }

    [Theory]
    [InlineData("BulletWaltz DA 62 SyphzonSPW")]
    [InlineData("BulletWaltz X Lobby-_-Gobbler")]
    [InlineData("BulletWaltz Hamidsfar7379")]
    public void KillRowsAreNotMistakenForPings(string row)
    {
        OcrLine[] lines = [new(row, .95, new(10, 20, 400, 20))];
        Assert.Single(new KillfeedDetector(Settings).Detect(lines, new(0, 0, 600, 300), 10, 600, "video").Candidates);
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
