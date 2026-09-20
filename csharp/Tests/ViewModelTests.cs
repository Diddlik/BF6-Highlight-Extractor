using Bf6Highlights.Desktop;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>The window state is plain logic on top of Core and is tested without a window.</summary>
public sealed class ViewModelTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("bf6-viewmodel-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static SettingsViewModel Settings()
    {
        var settings = new SettingsViewModel();
        settings.From(new Configuration
        {
            Player = new() { Names = ["BulletWaltz", "[CLAN]Diddlik"] },
            Killfeed = new() { Region = new() { X = 1967, Y = 173, Width = 593, Height = 295 } },
            Analysis = new() { SamplesPerSecond = 5, MaxWorkers = 8, EnableChangeDetection = false },
            Clips = new() { SecondsBefore = 3, SecondsAfter = 1, MergeGapSeconds = 1.5, ExportMode = "fast" },
        }, "config.yaml");
        return settings;
    }

    [Fact]
    public void SettingsSurviveTheRoundTripThroughTheWidgets()
    {
        var configuration = Settings().ToConfiguration();
        Assert.Equal(["BulletWaltz", "[CLAN]Diddlik"], configuration.Player.Names);
        Assert.Equal(new RegionSettings { X = 1967, Y = 173, Width = 593, Height = 295 },
            configuration.Killfeed.Region);
        Assert.Equal(5, configuration.Analysis.SamplesPerSecond);
        Assert.Equal(8, configuration.Analysis.MaxWorkers);
        Assert.False(configuration.Analysis.EnableChangeDetection);
        Assert.Equal(1.0, configuration.Clips.SecondsAfter);
        Assert.Equal("fast", configuration.Clips.ExportMode);
        Assert.Equal("onnx", configuration.Ocr.Engine);
    }

    [Fact]
    public void EditedSettingsReachTheConfiguration()
    {
        var settings = Settings();
        settings.PlayerNames = " Neuer Name , Zweiter ";
        settings.SamplesPerSecond = "7";
        settings.SecondsBefore = "4.5";
        settings.AccurateExport = true;
        settings.TemplateMode = false;
        var configuration = settings.ToConfiguration();
        Assert.Equal(["Neuer Name", "Zweiter"], configuration.Player.Names);
        Assert.Equal(7, configuration.Analysis.SamplesPerSecond);
        Assert.Equal(4.5, configuration.Clips.SecondsBefore);
        Assert.Equal("accurate", configuration.Clips.ExportMode);
        Assert.Equal("ocr", configuration.Detection.Mode);
    }

    [Fact]
    public void BadInputIsReportedWithItsFieldName()
    {
        var settings = Settings();
        settings.SamplesPerSecond = "viele";
        Assert.Contains("analysis.samples_per_second",
            Assert.Throws<ConfigurationException>(settings.ToConfiguration).Message);
        settings.SamplesPerSecond = "3";
        settings.MergeGap = "~";
        Assert.Contains("clips.merge_gap_seconds",
            Assert.Throws<ConfigurationException>(settings.ToConfiguration).Message);
    }

    [Fact]
    public void InvalidValuesAreStillValidatedByTheCore()
    {
        var settings = Settings();
        settings.SamplesPerSecond = "99";
        Assert.Contains("zwischen 1 und 15",
            Assert.Throws<ConfigurationException>(settings.ToConfiguration).Message);
    }

    [Fact]
    public void ExistingRegionCanBeReopenedWithoutValidatingUnrelatedSettings()
    {
        var settings = Settings();
        settings.SamplesPerSecond = "ungültig";
        Assert.Equal(new RegionSettings { X = 1967, Y = 173, Width = 593, Height = 295 },
            settings.RegionForEditor());

        settings.RegionWidth = "0";
        Assert.Null(settings.RegionForEditor());
    }

    private static SegmentItem Item(double start, double end) => new()
    {
        SourcePath = @"D:\Streams\match.mkv",
        Segment = new(start, end, [new(start + 1, "BulletWaltz", "BulletWaltz", "Gegner", null, "x",
            0.9, 100, 0, "match.mkv", 0)], "single_kill"),
        VideoDurationSeconds = 600,
    };

    [Fact]
    public void ClipBoundsCanBeCorrectedAndStayInsideTheVideo()
    {
        var item = Item(97, 102);
        item.StartText = "95.5";
        Assert.Equal(95.5, item.Segment.StartSeconds);
        item.EndText = "700";
        Assert.Equal(600, item.Segment.EndSeconds);
        item.StartText = "-10";
        Assert.Equal(0, item.Segment.StartSeconds);
    }

    [Fact]
    public void NonsenseAndInvertedBoundsAreIgnored()
    {
        var item = Item(97, 102);
        item.StartText = "keine Zahl";
        item.EndText = "";
        Assert.Equal(97, item.Segment.StartSeconds);
        Assert.Equal(102, item.Segment.EndSeconds);
        item.StartText = "500";
        Assert.True(item.Segment.StartSeconds < item.Segment.EndSeconds);
    }

    [Fact]
    public void PreviewPlayheadCanSetClampedClipBounds()
    {
        var item = Item(97, 102);
        item.SetStart(98.25);
        item.SetEnd(101.75);
        Assert.Equal(98.25, item.Segment.StartSeconds);
        Assert.Equal(101.75, item.Segment.EndSeconds);

        item.SetStart(double.NaN);
        item.SetEnd(900);
        Assert.Equal(98.25, item.Segment.StartSeconds);
        Assert.Equal(600, item.Segment.EndSeconds);
    }

    [Fact]
    public void TheClipNameAndTypeAreShownReadably()
    {
        Assert.Equal("Einzelkill", Item(97, 102).Kind);
        Assert.Equal("match.mkv", Item(97, 102).SourceName);
        Assert.Contains("00:01:37", Item(97, 102).Range);
    }

    [Fact]
    public async Task AVideoThatCannotBeReadIsExplainedInItsRow()
    {
        var model = new MainViewModel();
        var broken = Path.Combine(folder, "broken.mkv");
        await File.WriteAllTextAsync(broken, "kein Video");
        await model.AddVideosAsync([broken, broken]);
        var item = Assert.Single(model.Videos);
        Assert.False(item.Valid);
        Assert.False(model.CanStart);
        Assert.Contains("bereits in der Liste", model.Status);
        Assert.Contains("lesbares Video", model.StartHint);
    }

    [Fact]
    public void ExportingWithoutSelectionIsNotOffered()
    {
        var model = new MainViewModel();
        Assert.False(model.CanExport);
        model.Highlights.Add(Item(97, 102));
        Assert.True(model.CanExport);
        model.Highlights[0].Selected = false;
        model.SelectionChanged();
        Assert.False(model.CanExport);
    }

    [Fact]
    public void TheUserConfigurationLivesInAWritableUserFolder()
    {
        var path = ConfigurationFile.DefaultUserConfigPath;
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), path);
        Assert.EndsWith(Path.Combine("BF6-Highlight-Extractor", "config.yaml"), path);
        Assert.False(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavingWithoutADialogUsesTheLoadedFile()
    {
        var path = Path.Combine(folder, "eigene.yaml");
        var model = new MainViewModel();
        model.Settings.ConfigPath = path;
        model.Settings.PlayerNames = "BulletWaltz";
        model.SaveUserSettings();
        Assert.True(File.Exists(path));
        Assert.Contains("gespeichert", model.Status);
        Assert.Equal(["BulletWaltz"], ConfigurationFile.Load(path).Player.Names);
    }

    [Fact]
    public void AnInvalidConfigurationFileIsReportedNotThrown()
    {
        var model = new MainViewModel();
        var path = Path.Combine(folder, "bad.yaml");
        File.WriteAllText(path, "player:\n  names: []\n");
        model.LoadSettings(path);
        Assert.True(model.HasProblem);
        Assert.Contains("player.names", model.Problem);
        Assert.Contains("nicht geladen", model.Status);
    }
}
