using System.Globalization;

namespace Bf6Highlights.Desktop;

/// <summary>
/// The settings a run needs, as editable text. <see cref="ToConfiguration"/> builds a validated
/// <see cref="Configuration"/>, so the same rules apply as on the command line.
/// </summary>
public sealed class SettingsViewModel : Observable
{
    private Configuration source = new() { Player = new() { Names = ["Spielername"] } };

    public string ConfigPath { get; set; } = "";

    private string playerNames = "Spielername";
    public string PlayerNames { get => playerNames; set => Set(ref playerNames, value); }

    private string outputDirectory = "output";
    public string OutputDirectory { get => outputDirectory; set => Set(ref outputDirectory, value); }

    private string mode = "ocr";
    public string Mode { get => mode; set => Set(ref mode, value); }
    public bool OcrMode
    {
        get => mode == "ocr";
        set { if (value) { Mode = "ocr"; Raise(nameof(OcrMode)); Raise(nameof(TemplateMode)); } }
    }
    public bool TemplateMode
    {
        get => mode == "template";
        set { if (value) { Mode = "template"; Raise(nameof(OcrMode)); Raise(nameof(TemplateMode)); } }
    }

    private string regionX = "0";
    public string RegionX { get => regionX; set => Set(ref regionX, value); }
    private string regionY = "0";
    public string RegionY { get => regionY; set => Set(ref regionY, value); }
    private string regionWidth = "0";
    public string RegionWidth { get => regionWidth; set => Set(ref regionWidth, value); }
    private string regionHeight = "0";
    public string RegionHeight { get => regionHeight; set => Set(ref regionHeight, value); }

    public RegionSettings? RegionForEditor()
    {
        if (!int.TryParse(RegionX, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(RegionY, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(RegionWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(RegionHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || x < 0 || y < 0 || width < 1 || height < 1)
            return null;
        return new() { X = x, Y = y, Width = width, Height = height };
    }

    private string samplesPerSecond = "3";
    public string SamplesPerSecond { get => samplesPerSecond; set => Set(ref samplesPerSecond, value); }
    private string maxWorkers = "2";
    public string MaxWorkers { get => maxWorkers; set => Set(ref maxWorkers, value); }
    private bool changeDetection = true;
    public bool ChangeDetection { get => changeDetection; set => Set(ref changeDetection, value); }

    private string secondsBefore = "3";
    public string SecondsBefore { get => secondsBefore; set => Set(ref secondsBefore, value); }
    private string secondsAfter = "2";
    public string SecondsAfter { get => secondsAfter; set => Set(ref secondsAfter, value); }
    private string mergeGap = "1.5";
    public string MergeGap { get => mergeGap; set => Set(ref mergeGap, value); }

    private string exportMode = "accurate";
    public string ExportMode { get => exportMode; set => Set(ref exportMode, value); }
    public bool AccurateExport
    {
        get => exportMode == "accurate";
        set { if (value) { ExportMode = "accurate"; Raise(nameof(AccurateExport)); Raise(nameof(FastExport)); } }
    }
    public bool FastExport
    {
        get => exportMode == "fast";
        set { if (value) { ExportMode = "fast"; Raise(nameof(AccurateExport)); Raise(nameof(FastExport)); } }
    }

    private string nameThreshold = "82";
    public string NameThreshold { get => nameThreshold; set => Set(ref nameThreshold, value); }
    private string textThreshold = "88";
    public string TextThreshold { get => textThreshold; set => Set(ref textThreshold, value); }
    private string opponentThreshold = "85";
    public string OpponentThreshold { get => opponentThreshold; set => Set(ref opponentThreshold, value); }

    public void From(Configuration configuration, string path)
    {
        source = configuration;
        ConfigPath = path;
        PlayerNames = string.Join(", ", configuration.Player.Names);
        OutputDirectory = configuration.Video.OutputDirectory;
        Mode = configuration.Detection.Mode;
        var region = configuration.Killfeed.Region ?? new RegionSettings();
        (RegionX, RegionY) = (Text(region.X), Text(region.Y));
        (RegionWidth, RegionHeight) = (Text(region.Width), Text(region.Height));
        SamplesPerSecond = Text(configuration.Analysis.SamplesPerSecond);
        MaxWorkers = Text(configuration.Analysis.MaxWorkers);
        ChangeDetection = configuration.Analysis.EnableChangeDetection;
        SecondsBefore = Text(configuration.Clips.SecondsBefore);
        SecondsAfter = Text(configuration.Clips.SecondsAfter);
        MergeGap = Text(configuration.Clips.MergeGapSeconds);
        ExportMode = configuration.Clips.ExportMode;
        NameThreshold = Text(configuration.Ocr.PlayerNameSimilarityThreshold);
        TextThreshold = Text(configuration.Deduplication.TextSimilarityThreshold);
        OpponentThreshold = Text(configuration.Deduplication.OpponentSimilarityThreshold);
        foreach (var name in new[]
        {
            nameof(OcrMode), nameof(TemplateMode), nameof(AccurateExport), nameof(FastExport),
        }) Raise(name);
    }

    /// <summary>Builds the configuration and validates it, so bad input is reported per field.</summary>
    public Configuration ToConfiguration()
    {
        var region = new RegionSettings
        {
            X = Whole(RegionX, "killfeed.region.x"), Y = Whole(RegionY, "killfeed.region.y"),
            Width = Whole(RegionWidth, "killfeed.region.width"),
            Height = Whole(RegionHeight, "killfeed.region.height"),
        };
        return ConfigurationFile.Validated(source with
        {
            Player = new()
            {
                Names = [.. PlayerNames.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)],
            },
            Video = source.Video with { OutputDirectory = OutputDirectory.Trim() },
            Killfeed = source.Killfeed with
            {
                Region = region is { Width: 0, Height: 0 } ? source.Killfeed.Region : region,
            },
            Detection = source.Detection with { Mode = Mode },
            Analysis = source.Analysis with
            {
                SamplesPerSecond = Whole(SamplesPerSecond, "analysis.samples_per_second"),
                MaxWorkers = Whole(MaxWorkers, "analysis.max_workers"),
                EnableChangeDetection = ChangeDetection,
            },
            Ocr = source.Ocr with
            {
                Engine = "onnx", UseGpu = false,
                PlayerNameSimilarityThreshold = Number(NameThreshold,
                    "ocr.player_name_similarity_threshold"),
            },
            Deduplication = source.Deduplication with
            {
                TextSimilarityThreshold = Number(TextThreshold, "deduplication.text_similarity_threshold"),
                OpponentSimilarityThreshold = Number(OpponentThreshold,
                    "deduplication.opponent_similarity_threshold"),
            },
            Clips = source.Clips with
            {
                SecondsBefore = Number(SecondsBefore, "clips.seconds_before"),
                SecondsAfter = Number(SecondsAfter, "clips.seconds_after"),
                MergeGapSeconds = Number(MergeGap, "clips.merge_gap_seconds"),
                ExportMode = ExportMode,
            },
        });
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Whole(string text, string field) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ConfigurationException($"Ungültige Konfiguration:\n  - {field}: "
                + $"ganze Zahl erwartet ({text})");

    private static double Number(string text, string field) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            ? value : throw new ConfigurationException($"Ungültige Konfiguration:\n  - {field}: "
                + $"Zahl erwartet ({text})");
}
