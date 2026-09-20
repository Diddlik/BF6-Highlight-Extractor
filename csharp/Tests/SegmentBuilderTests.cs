using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>The five canonical merge cases of the specification, ported from tests/unit/test_segment_builder.py.</summary>
public sealed class SegmentBuilderTests
{
    private static readonly ClipSettings Clips =
        new() { SecondsBefore = 3.0, SecondsAfter = 2.0, MergeGapSeconds = 1.5 };

    private static List<ClipSegment> Build(double[] timestamps, double duration = 600.0) =>
        SegmentBuilder.Build([.. timestamps.Select(t => new KillCandidate(t, "BulletWaltz", "BulletWaltz",
            null, null, "kill " + t, 0.9, 100, 0, "clip.mp4", 0))], Clips, duration);

    private static (double, double)[] Bounds(List<ClipSegment> segments) =>
        [.. segments.Select(s => (s.StartSeconds, s.EndSeconds))];

    [Fact]
    public void SingleKillKeepsItsWindow()
    {
        var segments = Build([100.0]);
        Assert.Equal([(97.0, 102.0)], Bounds(segments));
        Assert.Equal("single_kill", segments[0].ClipType);
        Assert.Equal(5.0, segments[0].DurationSeconds);
    }

    [Fact]
    public void OverlappingKillsMerge()
    {
        var segments = Build([100.0, 103.0]);
        Assert.Equal([(97.0, 105.0)], Bounds(segments));
        Assert.Equal("double_kill", segments[0].ClipType);
    }

    [Fact]
    public void DistantKillsStaySeparate() =>
        Assert.Equal([(97.0, 102.0), (107.0, 112.0)], Bounds(Build([100.0, 110.0])));

    [Fact]
    public void NegativeStartIsClampedToZero() => Assert.Equal([(0.0, 3.0)], Bounds(Build([1.0])));

    [Fact]
    public void EndIsClampedToVideoLength() =>
        Assert.Equal(100.0, Build([98.0], duration: 100.0)[0].EndSeconds);

    [Fact]
    public void ThreeCloseKillsBecomeOneTripleKill()
    {
        var segments = Build([100.0, 102.0, 105.0]);
        Assert.Equal([(97.0, 107.0)], Bounds(segments));
        Assert.Equal("triple_kill", segments[0].ClipType);
    }

    [Fact]
    public void FourKillsAreAMultiKill()
    {
        var segments = Build([100.0, 102.0, 104.0, 106.0]);
        Assert.Equal("multi_kill", segments[0].ClipType);
        Assert.Equal(4, segments[0].Events.Count);
    }

    [Fact]
    public void AGapEqualToTheMergeGapStillMerges() => Assert.Single(Build([100.0, 106.5]));

    [Fact]
    public void UnsortedEventsAreOrderedFirst()
    {
        var segments = Build([110.0, 100.0]);
        Assert.Equal(97.0, segments[0].StartSeconds);
        Assert.Equal([100.0, 110.0], segments.Select(s => s.Events[0].TimestampSeconds));
    }

    [Fact]
    public void NoEventsMeansNoSegments() => Assert.Empty(Build([]));

    [Fact]
    public void MergedSegmentsKeepEveryEventInOrder()
    {
        var segments = Build([105.0, 100.0, 102.0]);
        Assert.Equal([100.0, 102.0, 105.0], segments[0].Events.Select(e => e.TimestampSeconds));
    }

    /// <summary>The Python run behind the baseline used 3 s before, 1 s after and a 1.5 s gap.</summary>
    [Fact]
    public void BaselineSegmentsAreReproducedFromTheBaselineEvents()
    {
        var baseline = Path.Combine(AppContext.BaseDirectory, "reports-baseline");
        var segments = SegmentBuilder.Build(
            Reports.ReadEventsJson(Path.Combine(baseline, "events.json")),
            new ClipSettings { SecondsBefore = 3.0, SecondsAfter = 1.0, MergeGapSeconds = 1.5 },
            videoDurationSeconds: 423.65);
        var target = Path.Combine(Directory.CreateTempSubdirectory("bf6-segments-").FullName, "segments.json");
        try
        {
            Reports.WriteSegmentsJson(target, segments);
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline, "segments.json")), File.ReadAllBytes(target));
        }
        finally { Directory.Delete(Path.GetDirectoryName(target)!, recursive: true); }
    }

    [Theory]
    [InlineData(double.NaN, 600.0)]
    [InlineData(-1.0, 600.0)]
    [InlineData(100.0, double.NaN)]
    [InlineData(100.0, -1.0)]
    public void InvalidTimestampsAndDurationsAreRejected(double timestamp, double duration) =>
        Assert.Throws<ArgumentException>(() => Build([timestamp], duration));
}
