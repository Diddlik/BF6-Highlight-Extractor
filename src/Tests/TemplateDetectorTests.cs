using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>Ported from tests/unit/test_template_detector.py.</summary>
public sealed class TemplateDetectorTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("bf6-template-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string name, Mat image)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, image.ToBytes(".png"));
        return path;
    }

    private static Mat Gray(int rows, int columns, byte[]? values = null)
    {
        var image = new Mat(rows, columns, MatType.CV_8UC1, Scalar.Black);
        for (var row = 0; row < rows && values is not null; row++)
            for (var column = 0; column < columns; column++)
                image.Set(row, column, values[row * columns + column]);
        return image;
    }

    private static DetectionModeSettings Detection(params TemplateSettings[] templates) =>
        new() { Mode = "template", Templates = [.. templates] };

    [Fact]
    public void TheBestLabelledTemplateWins()
    {
        using var kill = Gray(3, 3, [0, 40, 220, 20, 255, 80, 180, 30, 0]);
        using var headshot = Gray(3, 3, [255, 10, 40, 5, 90, 230, 70, 180, 20]);
        var settings = Detection(
            new TemplateSettings { Path = Write("kill.png", kill), Label = "kill", Threshold = 0.8 },
            new TemplateSettings { Path = Write("headshot.png", headshot), Label = "headshot", Threshold = 0.8 });
        using var region = Gray(20, 30);
        headshot.CopyTo(new Mat(region, new Rect(12, 8, 3, 3)));
        using var colour = new Mat();
        Cv2.CvtColor(region, colour, ColorConversionCodes.GRAY2BGR);

        using var matcher = new TemplateMatcher(settings, sourceWidth: 1920);
        var match = matcher.Match(colour, 12.5, 750);

        Assert.NotNull(match);
        Assert.Equal("headshot", match.Label);
        Assert.Equal(1.0, match.Confidence, 4);
        Assert.Equal(new PixelRegion(12, 8, 3, 3), match.BoundingBox);
    }

    [Fact]
    public void ATemplateIsScaledFromItsReferenceWidth()
    {
        using var template = Gray(8, 8);
        template.Rectangle(new Rect(2, 1, 4, 6), Scalar.White, -1);
        template.Rectangle(new Rect(0, 3, 8, 2), new Scalar(100), -1);
        var settings = Detection(new TemplateSettings
        {
            Path = Write("kill.png", template), Label = "kill", Threshold = 0.8, ReferenceWidth = 3840,
        });
        using var scaled = new Mat();
        Cv2.Resize(template, scaled, new Size(4, 4), interpolation: InterpolationFlags.Area);
        using var region = Gray(20, 20);
        scaled.CopyTo(new Mat(region, new Rect(7, 6, 4, 4)));

        using var matcher = new TemplateMatcher(settings, sourceWidth: 1920);
        Assert.NotNull(matcher.Match(region, 1.0, 30));
    }

    [Fact]
    public void HudEdgesCanBeCompared()
    {
        using var template = Gray(20, 40);
        Cv2.PutText(template, "K", new Point(7, 16), HersheyFonts.HersheySimplex, 0.55, Scalar.White, 2);
        var settings = Detection(new TemplateSettings
        {
            Path = Write("kill.png", template), Label = "kill", Threshold = 0.8, EdgeDetection = true,
        });
        using var region = Gray(60, 100);
        template.CopyTo(new Mat(region, new Rect(31, 23, 40, 20)));

        using var matcher = new TemplateMatcher(settings, sourceWidth: 1920);
        Assert.NotNull(matcher.Match(region, 1.0, 30));
    }

    [Fact]
    public void AWeakMatchStaysBelowTheThreshold()
    {
        using var template = Gray(4, 4, [255, 0, 255, 0, 0, 255, 0, 255, 255, 0, 255, 0, 0, 255, 0, 255]);
        var settings = Detection(new TemplateSettings
        {
            Path = Write("kill.png", template), Label = "kill", Threshold = 0.95,
        });
        using var region = Gray(20, 20);
        region.Rectangle(new Rect(5, 5, 6, 6), new Scalar(90), -1);

        using var matcher = new TemplateMatcher(settings, sourceWidth: 1920);
        Assert.Null(matcher.Match(region, 1.0, 30));
    }

    [Fact]
    public void ATemplateLargerThanTheRegionIsReported()
    {
        using var template = Gray(30, 30);
        var settings = Detection(new TemplateSettings { Path = Write("kill.png", template), Label = "kill" });
        using var matcher = new TemplateMatcher(settings, sourceWidth: 1920);
        using var region = Gray(20, 20);
        Assert.Contains("größer", Assert.Throws<ConfigurationException>(
            () => matcher.Match(region, 1.0, 30)).Message);
    }

    [Fact]
    public void MissingAndBrokenTemplatesAreReported()
    {
        Assert.Contains("nicht gefunden", Assert.Throws<ConfigurationException>(() => new TemplateMatcher(
            Detection(new TemplateSettings { Path = Path.Combine(folder, "missing.png") }), 1920)).Message);
        File.WriteAllText(Path.Combine(folder, "broken.png"), "kein Bild");
        Assert.Contains("konnte nicht gelesen werden", Assert.Throws<ConfigurationException>(
            () => new TemplateMatcher(Detection(new TemplateSettings
            {
                Path = Path.Combine(folder, "broken.png"),
            }), 1920)).Message);
    }

    private static TemplateMatch Match(double timestamp, string label = "kill", double confidence = 0.9) =>
        new(label, confidence, timestamp, (int)(timestamp * 10), new(1, 2, 3, 4));

    private static TemplateEventGrouper Grouper() => new(
        new DetectionModeSettings { GroupingGapSeconds = 0.8, MinConfirmations = 2 }, "stream.mkv");

    [Fact]
    public void TheGrouperRequiresSeveralConfirmations()
    {
        var grouper = Grouper();
        Assert.Null(grouper.Feed(Match(1.0), 1.0));
        Assert.Null(grouper.Feed(null, 2.0));
        Assert.Null(grouper.Finish());
    }

    [Fact]
    public void AMatchBurstBecomesOneTypedEvent()
    {
        var grouper = Grouper();
        Assert.Null(grouper.Feed(Match(1.0, "kill", 0.86), 1.0));
        Assert.Null(grouper.Feed(Match(1.4, "headshot", 0.95), 1.4));
        var kill = grouper.Feed(null, 2.3);

        Assert.NotNull(kill);
        Assert.Equal(1.0, kill.TimestampSeconds);
        Assert.Equal("headshot", kill.EventType);
        Assert.Equal("headshot", kill.RawText);
        Assert.Equal("template", kill.DetectionMethod);
        Assert.Equal(0.905, kill.Confidence, 6);
        Assert.Equal(90.5, kill.SimilarityScore, 6);
        Assert.Equal(10, kill.FrameNumber);
        Assert.Equal("stream.mkv", kill.SourceVideo);
    }

    [Fact]
    public void TheLastBurstIsFlushed()
    {
        var grouper = Grouper();
        grouper.Feed(Match(4.0), 4.0);
        grouper.Feed(Match(4.4), 4.4);
        Assert.NotNull(grouper.Finish());
    }

    [Fact]
    public void ANewBurstAfterTheGapClosesThePreviousOne()
    {
        var grouper = Grouper();
        Assert.Null(grouper.Feed(Match(1.0), 1.0));
        Assert.Null(grouper.Feed(Match(1.2), 1.2));
        var first = grouper.Feed(Match(5.0), 5.0);
        Assert.NotNull(first);
        Assert.Equal(1.0, first.TimestampSeconds);
        Assert.Null(grouper.Feed(Match(5.2), 5.2));
        Assert.Equal(5.0, grouper.Finish()!.TimestampSeconds);
    }
}
