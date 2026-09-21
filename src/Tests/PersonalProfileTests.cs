using System.Text.Json;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class PersonalProfileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bfhe-profile-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadsConfirmedDevelopmentAndHoldoutSamples()
    {
        Sample("positive", "development", "own_kill", "source-a");
        Sample("negative", "development", "no_event", "source-b");
        Sample("holdout", "holdout", "foreign_kill", "source-c");

        var dataset = PersonalProfileTrainer.LoadDataset(directory, allowLabelHints: true);

        Assert.Equal(2, dataset.Development.Count);
        Assert.Single(dataset.Holdout);
        Assert.Single(dataset.Development, sample => sample.ExpectedPositive);
        Assert.Empty(dataset.Issues);
    }

    [Fact]
    public void RejectsSourceSharedByDevelopmentAndHoldout()
    {
        Sample("development", "development", "own_kill", "same-source");
        Sample("holdout", "holdout", "no_event", "same-source");

        var error = Assert.Throws<ConfigurationException>(() =>
            PersonalProfileTrainer.LoadDataset(directory, allowLabelHints: true));

        Assert.Contains("Development und Holdout", error.Message);
    }

    [Fact]
    public void CalibrationLearnsConfidenceThresholdFromDevelopmentOnly()
    {
        var region = new PixelRegion(0, 0, 800, 200);
        var positive = TrainingSample("positive", "development", true, "source-a");
        var negative = TrainingSample("negative", "development", false, "source-b");
        var holdout = TrainingSample("holdout", "holdout", true, "source-c");
        var cases = new[]
        {
            new PersonalCalibrationCase(positive, region,
                [new("BulletWaltz AK Enemy", 0.35, new(20, 20, 500, 30))]),
            new PersonalCalibrationCase(negative, region,
                [new("OtherPlayer AK Enemy", 0.95, new(20, 20, 500, 30))]),
            new PersonalCalibrationCase(holdout, region,
                [new("BulletWaltz AK Enemy", 0.10, new(20, 20, 500, 30))]),
        };
        var configuration = new Configuration
        {
            Player = new() { Names = ["BulletWaltz"] },
            Ocr = new() { MinimumConfidence = 0.45, PlayerNameSimilarityThreshold = 82 },
        };

        var profile = PersonalProfileTrainer.Calibrate(configuration, cases);

        Assert.Equal(0.35, profile.MinimumConfidence, 3);
        Assert.Equal(1, profile.DevelopmentMetrics.F1, 3);
        Assert.Equal(0, profile.HoldoutMetrics.F1, 3);
    }

    private static PersonalCalibrationCase Case(PersonalTrainingSample sample, string text,
        double confidence) => new(sample, new(0, 0, 800, 200),
            [new(text, confidence, new(20, 20, 500, 30))]);

    private static Configuration Player() => new()
    {
        Player = new() { Names = ["BulletWaltz"] },
        Ocr = new() { MinimumConfidence = 0.45, PlayerNameSimilarityThreshold = 82 },
    };

    [Fact]
    public void CalibrationNeedsPositiveAndNegativeExamples()
    {
        var positive = Case(TrainingSample("a", "development", true, "source-a"), "BulletWaltz AK Enemy", 0.9);
        var negative = Case(TrainingSample("b", "development", false, "source-b"), "Other AK Enemy", 0.9);

        Assert.Contains("positive Beispiele", Assert.Throws<ConfigurationException>(
            () => PersonalProfileTrainer.Calibrate(Player(), [negative])).Message);
        Assert.Contains("negative Beispiele", Assert.Throws<ConfigurationException>(
            () => PersonalProfileTrainer.Calibrate(Player(), [positive])).Message);
        Assert.Contains("Entwicklungsfälle", Assert.Throws<ConfigurationException>(
            () => PersonalProfileTrainer.Calibrate(Player(), [])).Message);
    }

    [Fact]
    public void TheSameInputAlwaysGivesTheSameProfile()
    {
        PersonalCalibrationCase[] cases =
        [
            Case(TrainingSample("a", "development", true, "source-a"), "BulletWaltz AK Enemy", 0.62),
            Case(TrainingSample("b", "development", true, "source-b"), "BulletWaltz AK Other", 0.48),
            Case(TrainingSample("c", "development", false, "source-c"), "Enemy AK BulletWaltz", 0.91),
        ];

        var first = PersonalProfileTrainer.Calibrate(Player(), cases);
        var second = PersonalProfileTrainer.Calibrate(Player(), cases);

        Assert.Equal(first.MinimumConfidence, second.MinimumConfidence);
        Assert.Equal(first.NameThreshold, second.NameThreshold);
        Assert.Equal(first.DevelopmentMetrics, second.DevelopmentMetrics);
        // A death must not become a kill, whatever the thresholds are.
        Assert.Equal(0, first.DevelopmentMetrics.FalsePositives);
    }

    [Fact]
    public void ProfilesAreVersionedAndNeverOverwritten()
    {
        var cases = new[]
        {
            Case(TrainingSample("a", "development", true, "source-a"), "BulletWaltz AK Enemy", 0.6),
            Case(TrainingSample("b", "development", false, "source-b"), "Enemy AK BulletWaltz", 0.9),
        };
        var profile = PersonalProfileTrainer.Calibrate(Player(), cases, "Mein Profil");

        var first = PersonalProfileStore.Save(profile, directory);
        var second = PersonalProfileStore.Save(profile, directory);

        Assert.NotEqual(first, second);
        Assert.EndsWith("Mein Profil.json", first);
        Assert.EndsWith("Mein Profil-2.json", second);
        Assert.Equal(2, PersonalProfileStore.List(directory).Count);

        var loaded = PersonalProfileStore.Load(first);
        Assert.Equal(profile.MinimumConfidence, loaded.MinimumConfidence);
        Assert.Equal(profile.NameThreshold, loaded.NameThreshold);
        Assert.Equal(profile.DevelopmentMetrics.F1, loaded.DevelopmentMetrics.F1, 6);
        Assert.Equal(profile.ManifestHashes, loaded.ManifestHashes);
    }

    [Fact]
    public void AnUnsuitableOrBrokenProfileFallsBackToTheStandardDetection()
    {
        var cases = new[]
        {
            Case(TrainingSample("a", "development", true, "source-a"), "BulletWaltz AK Enemy", 0.6),
            Case(TrainingSample("b", "development", false, "source-b"), "Enemy AK BulletWaltz", 0.9),
        };
        var configuration = Player();
        var path = PersonalProfileStore.Save(
            PersonalProfileTrainer.Calibrate(configuration, cases, "Profil") with
            {
                SourceWidth = 2560, SourceHeight = 1440,
            }, directory);

        var (fitting, note) = PersonalProfileStore.Apply(configuration, path, 2560, 1440);
        Assert.Contains("Persönliches Profil", note);
        Assert.NotEqual(configuration.Ocr.MinimumConfidence, fitting.Ocr.MinimumConfidence);

        var (other, mismatch) = PersonalProfileStore.Apply(configuration, path, 1920, 1080);
        Assert.Contains("Standarderkennung", mismatch);
        Assert.Equal(configuration.Ocr.MinimumConfidence, other.Ocr.MinimumConfidence);

        var broken = Path.Combine(directory, "kaputt.json");
        File.WriteAllText(broken, "{ kein json");
        Assert.Contains("nicht lesbar", PersonalProfileStore.Apply(configuration, broken, 2560, 1440).Note);
        Assert.Contains("Standarderkennung", PersonalProfileStore.Apply(configuration, null, 2560, 1440).Note);
    }

    private void Sample(string name, string split, string label, string sourceHash)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "frame.png"), [1]);
        File.WriteAllText(Path.Combine(path, "sample.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1,
            id = name,
            source_sha256 = sourceHash,
            source_width = 1920,
            source_height = 1080,
            timestamp_hint_seconds = 10,
            label_hint = label,
            status = "needs_review",
            split,
            expected_events = (object?)null,
            frame = "frame.png",
        }));
    }

    private PersonalTrainingSample TrainingSample(string id, string split, bool positive, string source) =>
        new(id, directory, Path.Combine(directory, id + ".png"), source, 1920, 1080, 10,
            positive ? "own_kill" : "no_event", split, positive, id + "-hash");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
