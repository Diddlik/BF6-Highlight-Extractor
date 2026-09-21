using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class FrameInspectorTests : IAsyncLifetime
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "bf6-inspect-" + Guid.NewGuid().ToString("N"));
    private string video = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        video = Path.Combine(directory, "match.mkv");
        await MediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=128x96:rate=10", "-t", "3", "-c:v", "ffv1", video], TimeSpan.FromSeconds(60));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Answers with one killfeed row, whatever the crop looks like.</summary>
    private sealed class FakeOcr(string text) : IOcrEngine
    {
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<OcrLine>> ReadAsync(Mat crop, PixelRegion origin,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<OcrLine>>(
                text.Length == 0 ? [] : [new(text, 0.95, new(origin.X + 4, origin.Y + 10, 60, 16))]);

        public void Dispose() => Disposed = true;
    }

    private static Configuration Config() => new()
    {
        Player = new() { Names = ["BulletWaltz"] },
        Killfeed = new() { Region = new() { X = 8, Y = 4, Width = 80, Height = 40 } },
    };

    [Fact]
    public async Task TheKillfeedRegionIsReadAndTheCropIsSaved()
    {
        var ocr = new FakeOcr("BulletWaltz Gegner1");
        var inspection = await FrameInspector.InspectAsync(Config(), video, 1.0, directory, () => ocr);
        Assert.Equal(new PixelRegion(8, 4, 80, 40), inspection.Region);
        Assert.Equal(10, inspection.FrameNumber);
        Assert.Single(inspection.Lines);
        Assert.Equal("Gegner1", Assert.Single(inspection.Candidates).OpponentName);
        Assert.Empty(inspection.Rejections);
        Assert.True(ocr.Disposed);
        using var crop = Cv2.ImRead(inspection.CropPath);
        Assert.Equal(80, crop.Width);
        Assert.Equal(40, crop.Height);
    }

    [Fact]
    public async Task ADeathIsShownAsARejectionWithItsReason()
    {
        var inspection = await FrameInspector.InspectAsync(Config(), video, 1.0, directory,
            () => new FakeOcr("Gegner1 BulletWaltz"));
        Assert.Empty(inspection.Candidates);
        Assert.Equal("name_not_on_killer_side", Assert.Single(inspection.Rejections).Reason);
    }

    [Fact]
    public async Task RepeatedInspectionsKeepTheEarlierCrop()
    {
        var first = await FrameInspector.InspectAsync(Config(), video, 1.0, directory, () => new FakeOcr(""));
        var second = await FrameInspector.InspectAsync(Config(), video, 1.0, directory, () => new FakeOcr(""));
        Assert.NotEqual(first.CropPath, second.CropPath);
        Assert.EndsWith("00-00-01_2.png", second.CropPath);
        Assert.True(File.Exists(first.CropPath));
    }

    [Fact]
    public async Task TemplateModeReportsTheMatch()
    {
        var marker = Path.Combine(directory, "marker.png");
        using (var crop = await FrameInspector.CropAsync(await new VideoService().ProbeAsync(video),
            1.0, new PixelRegion(20, 20, 16, 16)))
        {
            using var gray = new Mat();
            Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
            File.WriteAllBytes(marker, gray.ToBytes(".png"));
        }
        var configuration = Config() with
        {
            Detection = new()
            {
                Mode = "template", Region = new() { X = 0, Y = 0, Width = 128, Height = 96 },
                Templates = [new() { Path = marker, Label = "kill", Threshold = 0.8 }],
            },
        };
        var inspection = await FrameInspector.InspectAsync(configuration, video, 1.0, directory,
            () => new FakeOcr(""));
        Assert.NotNull(inspection.Template);
        Assert.Equal("kill", inspection.Template.Label);
        Assert.Equal(new PixelRegion(20, 20, 16, 16), inspection.Template.BoundingBox);
        Assert.Empty(inspection.Lines);
    }

    [Fact]
    public async Task ARegionOutsideTheVideoIsReported()
    {
        var configuration = Config() with
        {
            Killfeed = new() { Region = new() { X = 100, Y = 4, Width = 80, Height = 40 } },
        };
        await Assert.ThrowsAsync<ConfigurationException>(() => FrameInspector.InspectAsync(
            configuration, video, 1.0, directory, () => new FakeOcr("")));
    }

    [Fact]
    public async Task AProfileIsAddedWithoutLosingOtherValues()
    {
        var path = Path.Combine(directory, "config.yaml");
        ConfigurationFile.Save(path, Config() with
        {
            Analysis = new() { SamplesPerSecond = 7 },
            Profiles = new()
            {
                ["old"] = new()
                {
                    Resolution = new() { Width = 1920, Height = 1080 },
                    Killfeed = new() { X = 1400, Y = 90, Width = 500, Height = 250 },
                },
            },
        });
        var updated = ConfigurationFile.SaveRegionProfile(path, "battlefield6_2560x1440",
            new ResolutionSettings { Width = 2560, Height = 1440 },
            new RegionSettings { X = 1967, Y = 173, Width = 593, Height = 295 });
        Assert.Equal(2, updated.Profiles.Count);
        var reloaded = ConfigurationFile.Load(path);
        Assert.Equal(7, reloaded.Analysis.SamplesPerSecond);
        Assert.Equal(1080, reloaded.Profiles["old"].Resolution.Height);
        Assert.Equal(593, reloaded.Profiles["battlefield6_2560x1440"].Killfeed.Width);
        Assert.Equal(1967, reloaded.ResolveKillfeedRegion(2560, 1440).X);
        await Task.CompletedTask;
    }

    [Fact]
    public void AProfileNeedsANameAndAValidConfiguration()
    {
        var path = Path.Combine(directory, "named.yaml");
        ConfigurationFile.Save(path, Config());
        Assert.Contains("Profilname", Assert.Throws<ConfigurationException>(() =>
            ConfigurationFile.SaveRegionProfile(path, "  ", new(), new())).Message);
        Assert.Throws<ConfigurationException>(() => ConfigurationFile.SaveRegionProfile(
            Path.Combine(directory, "absent.yaml"), "a",
            new() { Width = 1920, Height = 1080 }, new() { Width = 10, Height = 10 }));
    }
}
