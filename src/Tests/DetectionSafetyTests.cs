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

    // Misreadings of a blurred or covered opponent from a real recording, next to kills of other
    // opponents within the same window.
    [Fact]
    public void AMostlyContainedReadingIsTheSameOpponent()
    {
        (double Time, string Opponent)[] reads =
        [
            (892.33, "SlyRanger"), (894.67, "WRanger"), (897.0, "Blackout"), (897.33, "jiggymacswag"),
            (2602.67, "Quarantine"), (2607.67, "BLQuäranfine"),
            (1014.67, "10Philip98"), (1015.0, "ioPhifp98"), (1038.67, "Sequoia121299"),
        ];
        var dedup = new EventDeduplicator(Settings with { OpponentThreshold = 85 });
        var accepted = reads.Order().Where(read => dedup.Accept(Candidate with
        {
            TimestampSeconds = read.Time, RawText = "BulletWaltz " + read.Opponent, OpponentName = read.Opponent,
        })).Select(read => read.Opponent).ToArray();
        Assert.Equal(["SlyRanger", "Blackout", "jiggymacswag", "10Philip98", "Sequoia121299", "Quarantine"],
            accepted);
    }

    // Boxes as the OCR returned them at 2560x1440: an icon read as a tall box across two rows, and
    // a squad marker just below the own row.
    [Fact]
    public void BoxesReachingIntoANeighbouringRowDoNotJoinIt()
    {
        var region = new PixelRegion(1763, 175, 790, 96);
        var detector = new KillfeedDetector(Settings);
        OcrLine[] tallIcon =
        [
            new("gläl", .64, new(1763, 175, 204, 79)), new("Sequoia121299", .98, new(2004, 175, 171, 38)),
            new("ty455555", .95, new(2247, 175, 107, 37)), new("BulletWaltz", .97, new(1974, 215, 124, 35)),
            new("ne", .84, new(2110, 225, 42, 23)), new("Sequoia121299", 1, new(2189, 218, 164, 33)),
        ];
        Assert.Equal("Sequoia121299", Assert.Single(
            detector.Detect(tallIcon, region, 1039, 62360, "video").Candidates).OpponentName);
        OcrLine[] marker =
        [
            new("BUBBUKA", 1, new(2034, 176, 119, 31)), new("QuietMoon", .95, new(2235, 175, 117, 36)),
            new("BulletWaltz", .95, new(2022, 216, 124, 31)), new("BUBBUKA", 1, new(2236, 216, 112, 30)),
            new("Errar 701: Pany Hangs Up[2/6]", .84, new(2053, 231, 288, 36)),
        ];
        Assert.Equal("BUBBUKA", Assert.Single(
            detector.Detect(marker, region, 1768, 106100, "video").Candidates).OpponentName);
    }

    // Rows as the OCR read them from real recordings, damage included.
    [Theory]
    [InlineData("hat eine Gefahr gepingt")]
    [InlineData("hat Wiedereinsatzpunkt gepingt")]
    [InlineData("hat eine Feindeinheit gepngf")]
    [InlineData("het cine endenheft gf")]
    [InlineData("hai ein LMG gesinel")]
    [InlineData("Ping abgebrochen")]
    [InlineData("abgebrochen")]
    [InlineData("pinged an enemy")]
    public void PingsAreRejectedInsteadOfCountedAsKills(string message)
    {
        OcrLine[] lines = [new("BulletWaltz " + message, .95, new(10, 20, 400, 20))];
        var result = new KillfeedDetector(Settings).Detect(lines, new(0, 0, 600, 300), 10, 600, "video");
        Assert.Empty(result.Candidates);
        Assert.Equal(KillfeedDetector.PingMessage, Assert.Single(result.Rejections).Reason);
    }

    // 667 s and 677 s of a real recording: the same row, the victim later read as two words.
    [Fact]
    public void AVictimSplitIntoTwoWordsIsStillTheSameKill()
    {
        var detector = new KillfeedDetector(Settings);
        var dedup = new EventDeduplicator(Settings);
        KillCandidate Read(double time, params OcrLine[] lines) => Assert.Single(detector.Detect(lines,
            new(1850, 173, 710, 295), time, (int)(time * 60), "video").Candidates);
        var first = Read(667.33, new("BulletWaltz DA", .95, new(1955, 262, 200, 22)),
            new("BadTrip95", .95, new(2241, 261, 110, 22)));
        var later = Read(677.0, new("14 m", .99, new(1871, 187, 40, 22)),
            new("BulletWaltz", .98, new(1956, 182, 140, 22)), new("Bad Tfip95", .91, new(2240, 181, 115, 22)));
        Assert.Equal("Bad Tfip95", later.OpponentName);
        Assert.True(dedup.Accept(first));
        Assert.False(dedup.Accept(later with { TimestampSeconds = 672 }));
    }

    [Fact]
    public void APingGluedToTheNameIsRejected()
    {
        OcrLine[] lines = [new("BulletWaltzPing abgebrochen", .95, new(10, 20, 400, 20))];
        var result = new KillfeedDetector(Settings).Detect(lines, new(0, 0, 600, 300), 10, 600, "video");
        Assert.Equal(KillfeedDetector.PingMessage, Assert.Single(result.Rejections).Reason);
    }

    // The frames around 543.0 s of a real recording: the ping sentence is read in every frame but one.
    [Fact]
    public void TheNameAloneRightAfterAPingIsNotAKill()
    {
        var detector = new KillfeedDetector(Settings);
        var dedup = new EventDeduplicator(Settings);
        bool Kill(double time, params string[] texts)
        {
            OcrLine[] lines = [.. texts.Select((text, index) => new OcrLine(text, .95, new(10 + index * 200, 20, 150, 20)))];
            var result = detector.Detect(lines, new(0, 0, 600, 300), time, (int)(time * 60), "video");
            dedup.Observe(result.Rejections, "video");
            return result.Candidates.Any(dedup.Accept);
        }
        Assert.False(Kill(542.67, "BullefWaltz", "hat cin Gewchr gepingt"));
        Assert.False(Kill(543.0, "BulletWaltz"));
        Assert.False(Kill(543.33, "BullefWaltz", "hat ein Gewehr gepingt"));
        // A victim the OCR cannot read, such as a name in Chinese characters, is still a kill.
        Assert.True(Kill(600.0, "BulletWaltz"));
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
