using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>OCR lines measured on the notice below the crosshair of a 2560x1440 recording.</summary>
public sealed class KillNoticeTests
{
    private static readonly DetectionSettings Settings = new() { PlayerNames = ["BulletWaltz"] };
    private static readonly PixelRegion Region = KillNotice.Region(2560, 1440)!;

    private static KillCandidate? Detect(params OcrLine[] lines) =>
        KillNotice.Detect(lines, Region, Settings, 110, 6600, "a.mkv");

    [Fact]
    public void TheRegionScalesWithTheHeightAndStaysCentred()
    {
        Assert.Equal(new PixelRegion(600, 945, 500, 105), Region);
        Assert.Equal(new PixelRegion(1210, 709, 375, 79), KillNotice.Region(3440, 1080));
        Assert.Null(KillNotice.Region(400, 1440));
    }

    [Fact]
    public void AKillLabelYieldsTheVictimAboveIt()
    {
        var kill = Detect(new("300", 1, new(1031, 915, 89, 49)),
            new("Catwoolfii", .88, new(891, 969, 113, 30)), new("KILLS", 1, new(918, 1003, 82, 45)));

        Assert.NotNull(kill);
        Assert.Equal("Catwoolfii", kill.OpponentName);
        Assert.Equal(KillNotice.Method, kill.DetectionMethod);
        Assert.Equal("BulletWaltz", kill.PlayerNameConfigured);
    }

    [Theory]
    [InlineData("KILL BESTÄTIGT")]
    [InlineData("KILLS x 2")]
    [InlineData("KOPESCHUS")]
    public void KillLabelsAreKills(string label) =>
        Assert.NotNull(Detect(new OcrLine(label, 1, new(819, 1010, 184, 36))));

    [Theory]
    [InlineData("ASSIST BESTÄTIGT")]
    [InlineData("AD-WIEDERBELEBUNG")]
    [InlineData("CASH ERHALTEN")]
    [InlineData("ÜBERLEBEN")]
    public void OtherLabelsAreNoKills(string label) =>
        Assert.Null(Detect(new("popus1982", 1, new(879, 966, 123, 38)), new(label, 1, new(760, 1008, 236, 30))));

    [Fact]
    public void AVictimNamedLikeALabelIsNoKill() =>
        Assert.Null(Detect(new("Killer42", 1, new(879, 966, 123, 38)),
            new("ASSIST BESTÄTIGT", 1, new(760, 1008, 236, 30))));

    [Theory]
    [InlineData("KILLS", 1)]
    [InlineData("HALTEN x 2 KILLSx 2]", 2)]
    [InlineData("NIEDERGEHALTEN x 2 KILLS", 1)]
    [InlineData("KILLS × 3", 3)]
    public void TheCountFollowsTheKillWord(string label, int count) => Assert.Equal(count, KillNotice.Count(label));

    [Fact]
    public void AGarbledNameOnTheSameNoticeIsNoNewKillButAHigherCountIs()
    {
        var dedup = new EventDeduplicator(Settings);
        KillCandidate Shown(double time, string name, string label) =>
            Detect(new(name, .9, new(891, 969, 113, 30)), new(label, 1, new(819, 1003, 184, 36)))!
                with { TimestampSeconds = time };
        Assert.True(dedup.Accept(Shown(149.0, "1Miglio3", "KOPFSCHUSS")));
        Assert.False(dedup.Accept(Shown(149.8, "emramo3", "KOPFSCHUSS KILL BESTÄTIGT")));
        Assert.True(dedup.Accept(Shown(150.4, "Zsofia16", "KILLS x 2")));
        Assert.False(dedup.Accept(Shown(150.6, "Zsofial6", "KILLS x 2")));
    }

    [Fact]
    public void TheNoticeAndTheKillfeedRowOfOneKillCountOnce()
    {
        var dedup = new EventDeduplicator(Settings with { PlayerNames = ["BulletWaltz", "Second"] });
        var notice = Detect(new("Catwoolfii", .9, new(891, 969, 113, 30)), new("KILLS", 1, new(918, 1003, 82, 45)))!;
        Assert.True(dedup.Accept(notice with { PlayerNameConfigured = "Second", TimestampSeconds = 145.6, OpponentName = null }));
        Assert.False(dedup.Accept(notice with { TimestampSeconds = 146.2 }));
        Assert.False(dedup.Accept(new(147.4, "Second", "Second", "Catwoolfii", null, "Second Catwoolfii",
            .9, 100, 8844, "a.mkv", 10)));
    }
}
