using System.Text.Json;
using Bf6Highlights;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("bf6-config-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string name, string yaml)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    private const string Minimal = """
        player:
          names:
          - BulletWaltz
        """;

    [Fact]
    public void DefaultsMatchPythonBaseline()
    {
        using var reference = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "config-defaults.json")));
        var actual = JsonSerializer.SerializeToDocument(new Configuration(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var differences = new List<string>();
        Compare(reference.RootElement, actual.RootElement, "", differences);
        differences.Sort(StringComparer.Ordinal);
        // Documented migration changes: the file is versioned, the OCR backend is the bundled
        // ONNX pipeline, a player name has no usable default, and the packaged application
        // keeps its update settings here.
        Assert.Equal(["config_version", "ocr.engine", "player.names", "update"], differences);
    }

    [Fact]
    public void ExampleConfigurationImportsWithBackendNotice()
    {
        var (configuration, notices) =
            ConfigurationFile.Import(Path.Combine(AppContext.BaseDirectory, "reference", "python-config.example.yaml"));
        Assert.Equal(["BulletWaltz", "[CLAN]Diddlik"], configuration.Player.Names);
        Assert.Equal(new RegionSettings { X = 1900, Y = 100, Width = 600, Height = 500 },
            configuration.Killfeed.Region);
        Assert.Equal(500, configuration.Detection.Region!.Width);
        Assert.Equal(82.0, configuration.Deduplication.OpponentSimilarityThreshold);
        Assert.Equal("onnx", configuration.Ocr.Engine);
        Assert.Single(notices);
        Assert.Contains("paddleocr", notices[0]);
    }

    [Fact]
    public void ImportReplacesUnsupportedEngineAndGpu()
    {
        var path = Write("python.yaml", Minimal + """

            ocr:
              engine: easyocr
              use_gpu: true
            """);
        var (configuration, notices) = ConfigurationFile.Import(path);
        Assert.Equal("onnx", configuration.Ocr.Engine);
        Assert.False(configuration.Ocr.UseGpu);
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, n => n.StartsWith("ocr.engine:") && n.Contains("easyocr"));
        Assert.Contains(notices, n => n.StartsWith("ocr.use_gpu:") && n.Contains("CPU"));
        Assert.Equal(Configuration.CurrentVersion, configuration.ConfigVersion);
    }

    [Fact]
    public void ImportKeepsUnknownKeysOutOfTheWayAndLeavesTheSourceUntouched()
    {
        var path = Write("legacy.yaml", Minimal + """

            experimental:
              obs_live_mode: true
            """);
        var before = File.ReadAllBytes(path);
        Assert.Empty(ConfigurationFile.Import(path).Notices);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ImportResolvesTemplatePathsAndReportsMissingFiles()
    {
        Directory.CreateDirectory(Path.Combine(folder, "templates"));
        File.WriteAllBytes(Path.Combine(folder, "templates", "kill.png"), [1]);
        var path = Write("template.yaml", Minimal + """

            detection:
              mode: template
              region:
                x: 0
                y: 0
                width: 100
                height: 50
              templates:
              - path: templates/kill.png
              - path: templates/headshot.png
                label: headshot
            """);
        var (configuration, notices) = ConfigurationFile.Import(path);
        Assert.Equal(Path.Combine(folder, "templates", "kill.png"), configuration.Detection.Templates[0].Path);
        Assert.Equal("kill", configuration.Detection.Templates[0].Label);
        Assert.DoesNotContain("fehlt", notices[0]);
        Assert.Contains("fehlt", notices[1]);
    }

    [Fact]
    public void UnknownKeysAreRejectedWithTheirName()
    {
        var path = Write("unknown.yaml", Minimal + """

            analysis:
              samples_per_secnod: 4
            """);
        var error = Assert.Throws<ConfigurationException>(() => ConfigurationFile.Load(path));
        Assert.Contains("samples_per_secnod", error.Message);
    }

    [Fact]
    public void BrokenYamlIsReportedWithTheLine()
    {
        var path = Write("broken.yaml", "player:\n  names:\n  - a\n\tb: 1\n");
        Assert.Contains("nicht lesbar", Assert.Throws<ConfigurationException>(
            () => ConfigurationFile.Load(path)).Message);
    }

    [Fact]
    public void MissingFilesAreReported() => Assert.Contains("nicht gefunden",
        Assert.Throws<ConfigurationException>(
            () => ConfigurationFile.Load(Path.Combine(folder, "absent.yaml"))).Message);

    [Fact]
    public void EveryInvalidFieldIsNamed()
    {
        var path = Write("invalid.yaml", """
            player:
              names: []
            application:
              log_level: LOUD
            analysis:
              samples_per_second: 99
              max_workers: 0
            clips:
              crf: 80
              export_mode: turbo
            killfeed:
              killer_side: middle
              killer_region:
                x_min_ratio: 0.6
                x_max_ratio: 0.4
            ocr:
              engine: onnx
              minimum_confidence: 1.5
            """);
        var message = Assert.Throws<ConfigurationException>(() => ConfigurationFile.Load(path)).Message;
        foreach (var field in new[]
        {
            "player.names", "application.log_level", "analysis.samples_per_second", "analysis.max_workers",
            "clips.crf", "clips.export_mode", "killfeed.killer_side", "killfeed.killer_region.x_max_ratio",
            "ocr.minimum_confidence",
        }) Assert.Contains(field, message);
    }

    [Fact]
    public void TemplateModeNeedsRegionAndTemplates()
    {
        var path = Write("template-mode.yaml", Minimal + """

            detection:
              mode: template
            """);
        var message = Assert.Throws<ConfigurationException>(() => ConfigurationFile.Load(path)).Message;
        Assert.Contains("detection.region", message);
        Assert.Contains("detection.templates", message);
    }

    [Fact]
    public void GpuIsRejectedInAValidatedConfiguration()
    {
        var path = Write("gpu.yaml", Minimal + """

            ocr:
              use_gpu: true
            """);
        Assert.Contains("ocr.use_gpu", Assert.Throws<ConfigurationException>(
            () => ConfigurationFile.Load(path)).Message);
    }

    [Fact]
    public void SavedConfigurationLoadsBackUnchanged()
    {
        var source = Write("source.yaml", Minimal + """

            ocr:
              engine: easyocr
            killfeed:
              region:
                x: 1967
                y: 173
                width: 593
                height: 295
            profiles:
              bf6_2560x1440:
                resolution:
                  width: 2560
                  height: 1440
                killfeed:
                  x: 1900
                  y: 100
                  width: 600
                  height: 500
            """);
        var imported = ConfigurationFile.Import(source).Configuration;
        var target = Path.Combine(folder, "csharp", "config.yaml");
        ConfigurationFile.Save(target, imported);
        // Records compare lists and dictionaries by reference, so compare the serialized values.
        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(ConfigurationFile.Load(target)));
        Assert.Contains("config_version: 1", File.ReadAllText(target));
        Assert.Equal(2560, ConfigurationFile.Load(target).Profiles["bf6_2560x1440"].Resolution.Width);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFileWhenTheConfigurationIsInvalid()
    {
        Assert.Throws<ConfigurationException>(() => ConfigurationFile.Save(
            Path.Combine(folder, "out.yaml"), new Configuration()));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void KillfeedRegionPrefersProfileThenNormalizedThenAbsolute()
    {
        var configuration = new Configuration
        {
            Player = new() { Names = ["BulletWaltz"] },
            Killfeed = new()
            {
                Region = new() { X = 10, Y = 10, Width = 100, Height = 100 },
                RegionNormalized = new() { X = 0.5, Y = 0.25, Width = 0.25, Height = 0.5 },
            },
            Profiles = new()
            {
                ["bf6"] = new()
                {
                    Resolution = new() { Width = 2560, Height = 1440 },
                    Killfeed = new() { X = 1967, Y = 173, Width = 593, Height = 295 },
                },
            },
        };
        Assert.Equal(1967, configuration.ResolveKillfeedRegion(2560, 1440).X);
        Assert.Equal(new RegionSettings { X = 960, Y = 270, Width = 480, Height = 540 },
            configuration.ResolveKillfeedRegion(1920, 1080));
        Assert.Equal(10, (configuration with { Killfeed = configuration.Killfeed with { RegionNormalized = null } })
            .ResolveKillfeedRegion(1920, 1080).X);
    }

    [Fact]
    public void RegionsOutsideTheVideoAndMissingRegionsAreReported()
    {
        var configuration = new Configuration { Player = new() { Names = ["BulletWaltz"] } };
        Assert.Contains("Kein Erkennungsbereich", Assert.Throws<ConfigurationException>(
            () => configuration.ResolveDetectionRegion(1920, 1080)).Message);
        var narrow = configuration with
        {
            Killfeed = new() { Region = new() { X = 1900, Y = 100, Width = 600, Height = 500 } },
        };
        Assert.Contains("außerhalb", Assert.Throws<ConfigurationException>(
            () => narrow.ResolveKillfeedRegion(1920, 1080)).Message);
    }

    // 2560x1440 is the measured region; the others follow from the top-right anchor and the height.
    [Theory]
    [InlineData(2560, 1440, 1850, 173, 710, 295)]
    [InlineData(1920, 1080, 1388, 130, 532, 221)]
    [InlineData(3840, 2160, 2775, 260, 1065, 442)]
    [InlineData(3440, 1440, 2730, 173, 710, 295)]
    public void WithoutAConfiguredRegionTheBf6KillfeedIsUsed(int width, int height, int x, int y, int w, int h)
    {
        var configuration = new Configuration { Player = new() { Names = ["BulletWaltz"] } };
        Assert.Equal(new RegionSettings { X = x, Y = y, Width = w, Height = h },
            configuration.ResolveKillfeedRegion(width, height));
    }

    [Fact]
    public void DetectionSettingsUseTheConfiguredThresholds()
    {
        var settings = ConfigurationFile.Load(Write("detect.yaml", Minimal + """

            killfeed:
              killer_side: right
              killer_region:
                x_min_ratio: 0.52
                x_max_ratio: 1.0
            deduplication:
              opponent_similarity_threshold: 82
            """)).ToDetectionSettings();
        settings.Validate();
        Assert.Equal(["BulletWaltz"], settings.PlayerNames);
        Assert.Equal("right", settings.KillerSide);
        Assert.Equal(0.52, settings.KillerMinRatio);
        Assert.Equal(82, settings.OpponentThreshold);
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> differences)
    {
        if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
            {
                var child = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (actual.TryGetProperty(property.Name, out var value)) Compare(property.Value, value, child, differences);
                else differences.Add(child);
            }
            foreach (var property in actual.EnumerateObject())
                if (!expected.TryGetProperty(property.Name, out _))
                    differences.Add(path.Length == 0 ? property.Name : path + "." + property.Name);
            return;
        }
        var same = (expected.ValueKind, actual.ValueKind) switch
        {
            (JsonValueKind.Number, JsonValueKind.Number) => expected.GetDouble() == actual.GetDouble(),
            (JsonValueKind.Array, JsonValueKind.Array) => expected.GetArrayLength() == actual.GetArrayLength()
                && expected.EnumerateArray().Zip(actual.EnumerateArray())
                    .All(pair => pair.First.GetRawText() == pair.Second.GetRawText()),
            var (first, second) => first == second && expected.GetRawText() == actual.GetRawText(),
        };
        if (!same) differences.Add(path);
    }
}
