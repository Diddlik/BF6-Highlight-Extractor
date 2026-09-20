using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bf6Highlights;

/// <summary>Configuration values are invalid; the message lists every field and cause.</summary>
public sealed class ConfigurationException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record ApplicationSettings
{
    public string LogLevel { get; init; } = "INFO";
    public string Language { get; init; } = "de";
}

public sealed record PlayerSettings
{
    public List<string> Names { get; init; } = [];
}

public sealed record VideoSettings
{
    public string Input { get; init; } = "";
    public string OutputDirectory { get; init; } = "output";
}

public sealed record RegionSettings
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public PixelRegion ToPixelRegion() => new(X, Y, Width, Height);
}

public sealed record NormalizedRegionSettings
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public RegionSettings ToRegion(int videoWidth, int videoHeight) => new()
    {
        X = (int)Math.Round(X * videoWidth, MidpointRounding.ToEven),
        Y = (int)Math.Round(Y * videoHeight, MidpointRounding.ToEven),
        Width = Math.Max(1, (int)Math.Round(Width * videoWidth, MidpointRounding.ToEven)),
        Height = Math.Max(1, (int)Math.Round(Height * videoHeight, MidpointRounding.ToEven)),
    };
}

public sealed record ResolutionSettings
{
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed record ProfileSettings
{
    public ResolutionSettings Resolution { get; init; } = new();
    public RegionSettings Killfeed { get; init; } = new();
}

public sealed record KillerRegionSettings
{
    public double XMinRatio { get; init; }
    public double XMaxRatio { get; init; } = 0.48;
}

public sealed record KillfeedSettings
{
    public string Direction { get; init; } = "left_to_right";
    public string KillerSide { get; init; } = "left";
    public RegionSettings? Region { get; init; }
    public NormalizedRegionSettings? RegionNormalized { get; init; }
    public KillerRegionSettings KillerRegion { get; init; } = new();
}

public sealed record TemplateSettings
{
    public string Path { get; init; } = "";
    public string Label { get; init; } = "kill";
    public double Threshold { get; init; } = 0.72;
    public int? ReferenceWidth { get; init; }
    public bool EdgeDetection { get; init; }
}

public sealed record DetectionModeSettings
{
    public string Mode { get; init; } = "ocr";
    public RegionSettings? Region { get; init; }
    public NormalizedRegionSettings? RegionNormalized { get; init; }
    public List<TemplateSettings> Templates { get; init; } = [];
    public double GroupingGapSeconds { get; init; } = 0.8;
    public int MinConfirmations { get; init; } = 2;
}

public sealed record AnalysisSettings
{
    public int SamplesPerSecond { get; init; } = 3;
    public bool EnableChangeDetection { get; init; } = true;
    public double ChangeThreshold { get; init; } = 0.08;
    public int MaxWorkers { get; init; } = 2;
}

public sealed record PreprocessingSettings
{
    public double UpscaleFactor { get; init; } = 2.0;
    public bool Grayscale { get; init; } = true;
    public bool Sharpen { get; init; } = true;
    public bool AdaptiveThreshold { get; init; }
    public bool Denoise { get; init; } = true;
    public bool Invert { get; init; }
}

public sealed record OcrSettings
{
    /// <summary>Only the bundled ONNX pipeline; the Python engine names are migrated on import.</summary>
    public string Engine { get; init; } = "onnx";
    public string Language { get; init; } = "en";
    public bool UseGpu { get; init; }
    public bool FallbackToCpu { get; init; } = true;
    public double MinimumConfidence { get; init; } = 0.45;
    public double PlayerNameSimilarityThreshold { get; init; } = 82.0;
    public bool NormalizeStripSpecial { get; init; } = true;
    public bool NormalizeConfusables { get; init; } = true;
}

public sealed record DeduplicationSettings
{
    public double DuplicateWindowSeconds { get; init; } = 8.0;
    public double TextSimilarityThreshold { get; init; } = 88.0;
    public double OpponentSimilarityThreshold { get; init; } = 85.0;
}

public sealed record ClipSettings
{
    public double SecondsBefore { get; init; } = 3.0;
    public double SecondsAfter { get; init; } = 2.0;
    public double MergeGapSeconds { get; init; } = 1.5;
    public string ExportMode { get; init; } = "accurate";
    public string VideoCodec { get; init; } = "libx264";
    public string Preset { get; init; } = "fast";
    public int Crf { get; init; } = 18;
    public string AudioCodec { get; init; } = "aac";
    public string AudioBitrate { get; init; } = "192k";
}

public sealed record DebugSettings
{
    public bool Enabled { get; init; }
    public bool SaveChangedFrames { get; init; }
    public bool SaveOcrFrames { get; init; } = true;
    public bool SaveDetectedKills { get; init; } = true;
    public bool SaveRejectedMatches { get; init; }
}

public sealed record Configuration
{
    public const int CurrentVersion = 1;

    public int ConfigVersion { get; init; } = CurrentVersion;
    public ApplicationSettings Application { get; init; } = new();
    public PlayerSettings Player { get; init; } = new();
    public VideoSettings Video { get; init; } = new();
    public KillfeedSettings Killfeed { get; init; } = new();
    public DetectionModeSettings Detection { get; init; } = new();
    public Dictionary<string, ProfileSettings> Profiles { get; init; } = [];
    public AnalysisSettings Analysis { get; init; } = new();
    public PreprocessingSettings Preprocessing { get; init; } = new();
    public OcrSettings Ocr { get; init; } = new();
    public DeduplicationSettings Deduplication { get; init; } = new();
    public ClipSettings Clips { get; init; } = new();
    public DebugSettings Debug { get; init; } = new();

    public DetectionSettings ToDetectionSettings() => new()
    {
        PlayerNames = [.. Player.Names],
        NameThreshold = Ocr.PlayerNameSimilarityThreshold,
        MinimumConfidence = Ocr.MinimumConfidence,
        StripSpecial = Ocr.NormalizeStripSpecial,
        Confusables = Ocr.NormalizeConfusables,
        KillerSide = Killfeed.KillerSide,
        Direction = Killfeed.Direction,
        KillerMinRatio = Killfeed.KillerRegion.XMinRatio,
        KillerMaxRatio = Killfeed.KillerRegion.XMaxRatio,
        DuplicateWindowSeconds = Deduplication.DuplicateWindowSeconds,
        TextThreshold = Deduplication.TextSimilarityThreshold,
        OpponentThreshold = Deduplication.OpponentSimilarityThreshold,
    };

    /// <summary>Killfeed region for a resolution: matching profile, then normalized, then absolute.</summary>
    public RegionSettings ResolveKillfeedRegion(int videoWidth, int videoHeight)
    {
        foreach (var profile in Profiles.Values)
            if (profile.Resolution.Width == videoWidth && profile.Resolution.Height == videoHeight)
                return Inside(profile.Killfeed, videoWidth, videoHeight, "killfeed.region");
        if (Killfeed.RegionNormalized is not null)
            return Inside(Killfeed.RegionNormalized.ToRegion(videoWidth, videoHeight),
                videoWidth, videoHeight, "killfeed.region_normalized");
        if (Killfeed.Region is not null)
            return Inside(Killfeed.Region, videoWidth, videoHeight, "killfeed.region");
        throw new ConfigurationException($"Kein Killfeed-Bereich für {videoWidth}x{videoHeight} "
            + "konfiguriert. Bereich im Editor festlegen oder killfeed.region setzen.");
    }

    public RegionSettings ResolveDetectionRegion(int videoWidth, int videoHeight)
    {
        if (Detection.RegionNormalized is not null)
            return Inside(Detection.RegionNormalized.ToRegion(videoWidth, videoHeight),
                videoWidth, videoHeight, "detection.region_normalized");
        if (Detection.Region is not null)
            return Inside(Detection.Region, videoWidth, videoHeight, "detection.region");
        throw new ConfigurationException($"Kein Erkennungsbereich für {videoWidth}x{videoHeight} "
            + "konfiguriert. detection.region oder detection.region_normalized setzen.");
    }

    private static RegionSettings Inside(RegionSettings region, int videoWidth, int videoHeight, string field)
    {
        if (region.X + region.Width > videoWidth || region.Y + region.Height > videoHeight)
            throw new ConfigurationException(
                $"{field} {region.X},{region.Y} {region.Width}x{region.Height} liegt außerhalb "
                + $"des Videos ({videoWidth}x{videoHeight}).");
        return region;
    }
}

/// <summary>Reads, validates, migrates and writes the YAML configuration.</summary>
public static class ConfigurationFile
{
    private static readonly string[] LogLevels = ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"];

    public static Configuration Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigurationException("Konfigurationsdatei nicht gefunden: " + path);
        Configuration? configuration;
        try
        {
            configuration = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build()
                .Deserialize<Configuration>(File.ReadAllText(path));
        }
        catch (YamlException error)
        {
            throw new ConfigurationException(
                $"Konfigurationsdatei ist nicht lesbar ({Path.GetFileName(path)}, Zeile "
                + $"{error.Start.Line}): {error.InnerException?.Message ?? error.Message}", error);
        }
        if (configuration is null)
            throw new ConfigurationException("Konfigurationsdatei ist leer: " + path);
        return Validated(configuration);
    }

    public static Configuration Validated(Configuration configuration)
    {
        var problems = new List<string>();
        void Check(bool valid, string field, string cause)
        {
            if (!valid) problems.Add($"  - {field}: {cause}");
        }
        void Range(double value, string field, double minimum, double maximum)
            => Check(double.IsFinite(value) && value >= minimum && value <= maximum, field,
                $"Wert muss zwischen {Text(minimum)} und {Text(maximum)} liegen ({Text(value)})");

        var settings = configuration;
        Check(settings.ConfigVersion is >= 1 and <= Configuration.CurrentVersion, "config_version",
            $"Unterstützt wird Version 1 bis {Configuration.CurrentVersion} ({settings.ConfigVersion})");
        Check(LogLevels.Contains(settings.Application.LogLevel), "application.log_level",
            "Erlaubt sind " + string.Join(", ", LogLevels));
        Check(!string.IsNullOrWhiteSpace(settings.Application.Language), "application.language",
            "Sprachkürzel darf nicht leer sein");
        Check(settings.Player.Names.Count > 0 && settings.Player.Names.All(n => !string.IsNullOrWhiteSpace(n)),
            "player.names", "Mindestens ein nicht leerer Spielername ist erforderlich");
        Check(!string.IsNullOrWhiteSpace(settings.Video.OutputDirectory), "video.output_directory",
            "Ausgabeordner darf nicht leer sein");

        Check(settings.Killfeed.Direction is "left_to_right" or "right_to_left", "killfeed.direction",
            "Erlaubt sind left_to_right, right_to_left");
        Check(settings.Killfeed.KillerSide is "left" or "right", "killfeed.killer_side",
            "Erlaubt sind left, right");
        Range(settings.Killfeed.KillerRegion.XMinRatio, "killfeed.killer_region.x_min_ratio", 0, 1);
        Range(settings.Killfeed.KillerRegion.XMaxRatio, "killfeed.killer_region.x_max_ratio", 0, 1);
        Check(settings.Killfeed.KillerRegion.XMaxRatio > settings.Killfeed.KillerRegion.XMinRatio,
            "killfeed.killer_region.x_max_ratio", "Muss größer als x_min_ratio sein");
        Region(settings.Killfeed.Region, "killfeed.region", problems);
        Normalized(settings.Killfeed.RegionNormalized, "killfeed.region_normalized", problems);
        Region(settings.Detection.Region, "detection.region", problems);
        Normalized(settings.Detection.RegionNormalized, "detection.region_normalized", problems);

        Check(settings.Detection.Mode is "ocr" or "template", "detection.mode", "Erlaubt sind ocr, template");
        Check(double.IsFinite(settings.Detection.GroupingGapSeconds) && settings.Detection.GroupingGapSeconds > 0,
            "detection.grouping_gap_seconds", "Wert muss größer als 0 sein");
        Check(settings.Detection.MinConfirmations >= 1, "detection.min_confirmations",
            "Wert muss mindestens 1 sein");
        if (settings.Detection.Mode == "template")
        {
            Check(settings.Detection.Region is not null || settings.Detection.RegionNormalized is not null,
                "detection.region", "Im Template-Modus ist ein Erkennungsbereich erforderlich");
            Check(settings.Detection.Templates.Count > 0, "detection.templates",
                "Im Template-Modus ist mindestens eine Vorlage erforderlich");
        }
        for (var index = 0; index < settings.Detection.Templates.Count; index++)
        {
            var template = settings.Detection.Templates[index];
            var field = $"detection.templates[{index}]";
            Check(!string.IsNullOrWhiteSpace(template.Path), field + ".path", "Pfad darf nicht leer sein");
            Check(!string.IsNullOrWhiteSpace(template.Label), field + ".label", "Bezeichnung darf nicht leer sein");
            Range(template.Threshold, field + ".threshold", 0, 1);
            Check(template.ReferenceWidth is null or > 0, field + ".reference_width",
                "Wert muss größer als 0 sein");
        }

        foreach (var (name, profile) in settings.Profiles)
        {
            Check(!string.IsNullOrWhiteSpace(name), "profiles", "Profilname darf nicht leer sein");
            Check(profile.Resolution.Width > 0 && profile.Resolution.Height > 0,
                $"profiles.{name}.resolution", "Breite und Höhe müssen größer als 0 sein");
            Region(profile.Killfeed, $"profiles.{name}.killfeed", problems);
        }

        Check(settings.Analysis.SamplesPerSecond is >= 1 and <= 15, "analysis.samples_per_second",
            "Wert muss zwischen 1 und 15 liegen (" + settings.Analysis.SamplesPerSecond + ")");
        Range(settings.Analysis.ChangeThreshold, "analysis.change_threshold", 0, 1);
        Check(settings.Analysis.MaxWorkers is >= 1 and <= 32, "analysis.max_workers",
            "Wert muss zwischen 1 und 32 liegen (" + settings.Analysis.MaxWorkers + ")");
        Range(settings.Preprocessing.UpscaleFactor, "preprocessing.upscale_factor", 1, 8);

        Check(settings.Ocr.Engine == "onnx", "ocr.engine",
            "Unterstützt wird nur onnx; paddleocr, easyocr und tesseract werden beim Import ersetzt");
        Check(!string.IsNullOrWhiteSpace(settings.Ocr.Language), "ocr.language",
            "Sprachkürzel darf nicht leer sein");
        Check(!settings.Ocr.UseGpu, "ocr.use_gpu",
            "Es ist kein geprüfter GPU-Anbieter vorhanden; nur false ist möglich");
        Range(settings.Ocr.MinimumConfidence, "ocr.minimum_confidence", 0, 1);
        Range(settings.Ocr.PlayerNameSimilarityThreshold, "ocr.player_name_similarity_threshold", 0, 100);

        Check(double.IsFinite(settings.Deduplication.DuplicateWindowSeconds)
            && settings.Deduplication.DuplicateWindowSeconds >= 0,
            "deduplication.duplicate_window_seconds", "Wert darf nicht negativ sein");
        Range(settings.Deduplication.TextSimilarityThreshold, "deduplication.text_similarity_threshold", 0, 100);
        Range(settings.Deduplication.OpponentSimilarityThreshold,
            "deduplication.opponent_similarity_threshold", 0, 100);

        Check(double.IsFinite(settings.Clips.SecondsBefore) && settings.Clips.SecondsBefore >= 0,
            "clips.seconds_before", "Wert darf nicht negativ sein");
        Check(double.IsFinite(settings.Clips.SecondsAfter) && settings.Clips.SecondsAfter >= 0,
            "clips.seconds_after", "Wert darf nicht negativ sein");
        Check(double.IsFinite(settings.Clips.MergeGapSeconds) && settings.Clips.MergeGapSeconds >= 0,
            "clips.merge_gap_seconds", "Wert darf nicht negativ sein");
        Check(settings.Clips.ExportMode is "accurate" or "fast", "clips.export_mode",
            "Erlaubt sind accurate, fast");
        Check(settings.Clips.Crf is >= 0 and <= 51, "clips.crf",
            "Wert muss zwischen 0 und 51 liegen (" + settings.Clips.Crf + ")");
        foreach (var (value, field) in new[]
        {
            (settings.Clips.VideoCodec, "clips.video_codec"), (settings.Clips.Preset, "clips.preset"),
            (settings.Clips.AudioCodec, "clips.audio_codec"), (settings.Clips.AudioBitrate, "clips.audio_bitrate"),
        }) Check(!string.IsNullOrWhiteSpace(value), field, "Wert darf nicht leer sein");

        if (problems.Count > 0)
            throw new ConfigurationException("Ungültige Konfiguration:\n" + string.Join('\n', problems));
        return settings with { Player = settings.Player with { Names = [.. settings.Player.Names.Select(n => n.Trim())] } };
    }

    /// <summary>
    /// Reads a Python configuration, migrates the values C# handles differently and reports every
    /// change. The source file is never written to.
    /// </summary>
    public static (Configuration Configuration, IReadOnlyList<string> Notices) Import(string path)
    {
        if (!File.Exists(path)) throw new ConfigurationException("Konfigurationsdatei nicht gefunden: " + path);
        Configuration raw;
        try
        {
            raw = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Configuration>(File.ReadAllText(path))
                ?? throw new ConfigurationException("Konfigurationsdatei ist leer: " + path);
        }
        catch (YamlException error)
        {
            throw new ConfigurationException(
                $"Konfigurationsdatei ist nicht lesbar ({Path.GetFileName(path)}, Zeile "
                + $"{error.Start.Line}): {error.InnerException?.Message ?? error.Message}", error);
        }
        var notices = new List<string>();
        var engine = raw.Ocr.Engine;
        if (engine != "onnx")
            notices.Add(engine == "paddleocr"
                ? "ocr.engine: paddleocr wird auf das mitgelieferte ONNX-Backend (onnx) umgestellt."
                : $"ocr.engine: {engine} ist in C# nicht vorhanden; es wird das mitgelieferte "
                  + "ONNX-Backend (onnx) verwendet. Erkennungsergebnisse bitte erneut prüfen.");
        if (raw.Ocr.UseGpu)
            notices.Add("ocr.use_gpu: Es ist kein geprüfter GPU-Anbieter vorhanden; die Analyse "
                + "läuft auf der CPU.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var templates = raw.Detection.Templates.Select(template =>
        {
            if (template.Path.Length == 0) return template;
            var resolved = Path.GetFullPath(template.Path, folder);
            notices.Add($"detection.templates: {template.Path} wird zu {resolved} aufgelöst"
                + (File.Exists(resolved) ? "." : " und fehlt dort."));
            return template with { Path = resolved };
        }).ToList();
        return (Validated(raw with
        {
            ConfigVersion = Configuration.CurrentVersion,
            Ocr = raw.Ocr with { Engine = "onnx", UseGpu = false },
            Detection = raw.Detection with { Templates = templates },
        }), notices);
    }

    /// <summary>
    /// Adds or replaces a resolution profile in an existing configuration. Every other value is
    /// kept; comments of a hand-written file are lost, as in the Python original.
    /// </summary>
    public static Configuration SaveRegionProfile(string path, string name,
        ResolutionSettings resolution, RegionSettings region)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ConfigurationException("Profilname fehlt.");
        var configuration = Load(path);
        var profiles = new Dictionary<string, ProfileSettings>(configuration.Profiles)
        {
            [name.Trim()] = new() { Resolution = resolution, Killfeed = region },
        };
        var updated = configuration with { Profiles = profiles };
        Save(path, updated);
        return updated;
    }

    /// <summary>Writes the configuration to a temporary file first and publishes it afterwards.</summary>
    public static void Save(string path, Configuration configuration)
    {
        var text = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(Validated(configuration));
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".bf6-config-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, full, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Region(RegionSettings? region, string field, List<string> problems)
    {
        if (region is null) return;
        if (region.X < 0 || region.Y < 0)
            problems.Add($"  - {field}: x und y dürfen nicht negativ sein");
        if (region.Width <= 0 || region.Height <= 0)
            problems.Add($"  - {field}: width und height müssen größer als 0 sein");
    }

    private static void Normalized(NormalizedRegionSettings? region, string field, List<string> problems)
    {
        if (region is null) return;
        if (!(region.X is >= 0 and <= 1) || !(region.Y is >= 0 and <= 1))
            problems.Add($"  - {field}: x und y müssen zwischen 0 und 1 liegen");
        if (!(region.Width is > 0 and <= 1) || !(region.Height is > 0 and <= 1))
            problems.Add($"  - {field}: width und height müssen größer als 0 und höchstens 1 sein");
    }

    private static string Text(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
