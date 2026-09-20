using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Bf6Highlights.Desktop;

public sealed record RegionFrame(byte[] Png, VideoMetadata Video, double Timestamp);

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>One imported recording with what the probe found out about it.</summary>
public sealed class VideoItem : Observable
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public VideoMetadata? Metadata { get; private set; }

    private string status = "wird geprüft …";
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Valid => Metadata is not null;

    public string Details => Metadata is { } video
        ? $"{Reports.FormatTimestamp(video.DurationSeconds)} · {video.Width}×{video.Height} · "
          + $"{video.Fps:0.###} fps · {Size()} · {video.VideoCodec}"
          + (video.AudioCodec is null ? " · ohne Ton" : " · " + video.AudioCodec)
        : Status;

    public async Task ProbeAsync(CancellationToken token)
    {
        try
        {
            Metadata = await new VideoService().ProbeAsync(Path, token);
            Status = "bereit";
        }
        catch (Exception error) when (error is IOException or ArgumentException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            Status = "nicht lesbar: " + error.Message;
        }
        Raise(nameof(Details));
        Raise(nameof(Valid));
    }

    private string Size() => new FileInfo(Path).Length switch
    {
        var bytes and >= 1073741824 => (bytes / 1073741824.0).ToString("0.0 GB", CultureInfo.CurrentCulture),
        var bytes => (bytes / 1048576.0).ToString("0 MB", CultureInfo.CurrentCulture),
    };
}

/// <summary>A clip candidate in the review list; start and end stay editable before the export.</summary>
public sealed class SegmentItem : Observable
{
    public required string SourcePath { get; init; }
    public required ClipSegment Segment { get; set; }
    public required double VideoDurationSeconds { get; init; }

    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }

    public string SourceName => System.IO.Path.GetFileName(SourcePath);
    public string Kind => Segment.ClipType switch
    {
        "single_kill" => "Einzelkill", "double_kill" => "Doppelkill",
        "triple_kill" => "Dreifachkill", _ => $"Mehrfachkill ({Segment.Events.Count})",
    };
    public string Range => $"{Reports.FormatTimestamp(Segment.StartSeconds)} – "
        + $"{Reports.FormatTimestamp(Segment.EndSeconds)} ({Segment.DurationSeconds:0.0} s)";
    public string Opponents => string.Join(", ", Segment.Events
        .Select(e => e.OpponentName ?? "?").Distinct());

    public string StartText
    {
        get => Segment.StartSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        set { if (TryNumber(value, out var number)) SetStart(number); }
    }

    public string EndText
    {
        get => Segment.EndSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        set { if (TryNumber(value, out var number)) SetEnd(number); }
    }

    public void SetStart(double value) => Move(value, start: true);
    public void SetEnd(double value) => Move(value, start: false);

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    /// <summary>Manual correction of a clip boundary; invalid input is ignored.</summary>
    private void Move(double value, bool start)
    {
        if (!double.IsFinite(value)) return;
        value = Math.Clamp(value, 0, VideoDurationSeconds);
        var corrected = start
            ? Segment with { StartSeconds = Math.Min(value, Segment.EndSeconds - 0.1) }
            : Segment with { EndSeconds = Math.Max(value, Segment.StartSeconds + 0.1) };
        if (corrected.EndSeconds - corrected.StartSeconds < 0.1) return;
        Segment = corrected;
        Raise(nameof(StartText));
        Raise(nameof(EndText));
        Raise(nameof(Range));
    }
}

/// <summary>
/// The window's state. Every action runs through the same Core services the CLI uses; the
/// analysis and the export run off the UI thread and can be cancelled.
/// </summary>
public sealed class MainViewModel : Observable
{
    private CancellationTokenSource? cancellation;

    public ObservableCollection<VideoItem> Videos { get; } = [];
    public ObservableCollection<SegmentItem> Highlights { get; } = [];
    public SettingsViewModel Settings { get; } = new();

    private string status = "Videos hinzufügen, um zu beginnen.";
    public string Status { get => status; private set => Set(ref status, value); }

    private string? problem;
    public string? Problem
    {
        get => problem;
        private set { Set(ref problem, value); Raise(nameof(HasProblem)); }
    }
    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    private bool busy;
    public bool Busy
    {
        get => busy;
        private set
        {
            Set(ref busy, value);
            Raise(nameof(Idle));
            Raise(nameof(CanStart));
            Raise(nameof(CanExport));
        }
    }
    public bool Idle => !busy;

    private double progress;
    public double Progress { get => progress; private set => Set(ref progress, value); }

    private string stage = "";
    public string Stage { get => stage; private set => Set(ref stage, value); }

    public bool CanStart => !Busy && Videos.Any(video => video.Valid)
        && !string.IsNullOrWhiteSpace(Settings.OutputDirectory);
    public bool CanExport => !Busy && Highlights.Any(item => item.Selected);

    public string StartHint => CanStart ? "" : Busy ? "Ein Vorgang läuft."
        : !Videos.Any(video => video.Valid) ? "Es fehlt ein lesbares Video."
        : "Es fehlt ein Ausgabeordner.";

    public MainViewModel()
    {
        Videos.CollectionChanged += (_, _) => { Raise(nameof(CanStart)); Raise(nameof(StartHint)); };
        Highlights.CollectionChanged += (_, _) => Raise(nameof(CanExport));
        Settings.PropertyChanged += (_, _) => { Raise(nameof(CanStart)); Raise(nameof(StartHint)); };
    }

    public void SelectionChanged() => Raise(nameof(CanExport));

    /// <summary>Adds files, skips duplicates silently and explains unreadable ones in their row.</summary>
    public async Task AddVideosAsync(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Videos.Any(video => string.Equals(video.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            var item = new VideoItem { Path = full };
            Videos.Add(item);
            await item.ProbeAsync(CancellationToken.None);
            Raise(nameof(CanStart));
            Raise(nameof(StartHint));
            added++;
        }
        Status = $"{added} Video(s) hinzugefügt"
            + (skipped > 0 ? $", {skipped} bereits in der Liste." : ".");
    }

    public void Remove(VideoItem video) => Videos.Remove(video);

    public void ClearVideos()
    {
        Videos.Clear();
        Status = "Liste geleert.";
    }

    public void Cancel()
    {
        cancellation?.Cancel();
        Status = "Abbruch angefordert …";
    }

    public async Task AnalyzeAsync()
    {
        if (!CanStart) return;
        Problem = null;
        Highlights.Clear();
        Busy = true;
        using var cancel = new CancellationTokenSource();
        cancellation = cancel;
        var kills = 0;
        try
        {
            var configuration = Settings.ToConfiguration();
            var service = new AnalysisService(configuration, () => new OnnxOcrEngine());
            foreach (var video in Videos.Where(item => item.Valid).ToArray())
            {
                Stage = "Analyse: " + video.FileName;
                var target = System.IO.Path.Combine(Settings.OutputDirectory,
                    System.IO.Path.GetFileNameWithoutExtension(video.Path));
                var result = await Task.Run(() => service.RunAsync(video.Path, target,
                    new Progress<AnalysisProgress>(update =>
                    {
                        Progress = update.Percent;
                        Stage = $"{video.FileName}: {update.Stage} · {update.KillsDetected} Kills";
                    }), cancel.Token), cancel.Token);
                foreach (var segment in result.Segments)
                    Highlights.Add(new SegmentItem
                    {
                        SourcePath = video.Path, Segment = segment,
                        VideoDurationSeconds = result.Video.DurationSeconds,
                    });
                kills += result.Events.Count;
                if (result.Interrupted) break;
            }
            Status = Highlights.Count == 0
                ? kills == 0
                    ? "Keine Kills gefunden. Bereich und Schwellwerte prüfen."
                    : "Keine Clip-Abschnitte gebildet."
                : $"{kills} Kill-Kandidaten in {Highlights.Count} Abschnitten. "
                  + "Auswahl prüfen und exportieren.";
        }
        catch (OperationCanceledException)
        {
            Status = $"Abgebrochen. {Highlights.Count} Abschnitte bleiben erhalten.";
        }
        catch (Exception error) when (error is ConfigurationException or IOException
            or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or OpenCvSharp.OpenCVException)
        {
            Problem = error.Message;
            Status = "Analyse fehlgeschlagen.";
        }
        finally
        {
            cancellation = null;
            Busy = false;
            Stage = "";
            Progress = 0;
            Raise(nameof(CanExport));
        }
    }

    /// <summary>Exports the selected candidates into the shared clips folder of the output target.</summary>
    public async Task ExportSelectionAsync()
    {
        if (!CanExport) return;
        Problem = null;
        Busy = true;
        using var cancel = new CancellationTokenSource();
        cancellation = cancel;
        try
        {
            var exporter = new ClipExporter(Settings.ToConfiguration().Clips);
            var directory = System.IO.Path.Combine(Settings.OutputDirectory, "clips");
            var written = 0;
            var failures = new List<string>();
            foreach (var group in Highlights.Where(item => item.Selected).GroupBy(item => item.SourcePath))
            {
                Stage = "Export: " + System.IO.Path.GetFileName(group.Key);
                var result = await Task.Run(() => exporter.ExportAsync(group.Key,
                    [.. group.Select(item => item.Segment)], directory,
                    new Progress<string>(clip => Stage = "Clip: " + clip), cancel.Token), cancel.Token);
                written += result.Written.Count;
                failures.AddRange(result.Failures);
            }
            Problem = failures.Count == 0 ? null : string.Join("\n", failures);
            Status = $"{written} Clip(s) in {directory}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Export abgebrochen. Unfertige Dateien wurden entfernt.";
        }
        catch (Exception error) when (error is ConfigurationException or IOException
            or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Problem = error.Message;
            Status = "Export fehlgeschlagen.";
        }
        finally
        {
            cancellation = null;
            Busy = false;
            Stage = "";
        }
    }

    public async Task ExportSampleAsync(VideoItem video, SampleRequest request)
    {
        if (Busy || !video.Valid) return;
        Problem = null;
        Busy = true;
        using var cancel = new CancellationTokenSource();
        cancellation = cancel;
        try
        {
            Stage = "Prüfsample: " + video.FileName;
            await Task.Run(() => SampleExporter.ExportAsync(video.Path, request.Start, request.End,
                request.Timestamp, request.Label, request.Destination, cancel.Token,
                request.Player, request.Split), cancel.Token);
            Status = "Prüfsample erstellt: " + request.Destination;
        }
        catch (OperationCanceledException)
        {
            Status = "Sample-Export abgebrochen. Unfertige Dateien wurden entfernt.";
        }
        catch (Exception error) when (error is IOException or ArgumentException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            Problem = error.Message;
            Status = "Sample-Export fehlgeschlagen.";
        }
        finally
        {
            cancellation = null;
            Busy = false;
            Stage = "";
        }
    }

    /// <summary>A full frame for the region editor at the requested source time.</summary>
    public async Task<RegionFrame?> FrameForRegionAsync(VideoItem? source = null, double timestamp = 60)
    {
        var video = source is { Valid: true } ? source : Videos.FirstOrDefault(item => item.Valid);
        if (video?.Metadata is not { } metadata)
        {
            Problem = "Für den Bereichseditor fehlt ein lesbares Video.";
            return null;
        }
        Busy = true;
        try
        {
            if (!double.IsFinite(timestamp)) throw new ArgumentException("Ungültige Frame-Zeit.");
            var at = Math.Clamp(timestamp, 0, Math.Max(0, metadata.DurationSeconds - 0.001));
            using var frame = await Task.Run(() => FrameInspector.CropAsync(metadata, at,
                new PixelRegion(0, 0, metadata.Width, metadata.Height)));
            Problem = null;
            Status = $"Bild bei {Reports.FormatTimestamp(at)} aus {video.FileName}.";
            return new(frame.ToBytes(".png"), metadata, at);
        }
        catch (Exception error) when (error is IOException or ArgumentException
            or ConfigurationException or TimeoutException or OpenCvSharp.OpenCVException)
        {
            Problem = error.Message;
            return null;
        }
        finally { Busy = false; }
    }

    public RegionSettings? RegionForEditor(VideoMetadata video)
    {
        var region = Settings.RegionForEditor();
        return region is not null && region.Width <= video.Width && region.Height <= video.Height
            && region.X <= video.Width - region.Width && region.Y <= video.Height - region.Height
            ? region : null;
    }

    /// <summary>Takes the picked region and reports which resolution it belongs to.</summary>
    public void ApplyRegion(RegionSettings region, VideoMetadata video)
    {
        Settings.RegionX = region.X.ToString(CultureInfo.InvariantCulture);
        Settings.RegionY = region.Y.ToString(CultureInfo.InvariantCulture);
        Settings.RegionWidth = region.Width.ToString(CultureInfo.InvariantCulture);
        Settings.RegionHeight = region.Height.ToString(CultureInfo.InvariantCulture);
        Status = $"Bereich {region.X},{region.Y} {region.Width}×{region.Height} für "
            + $"{video.Width}×{video.Height} übernommen. Zum Behalten die Konfiguration speichern.";
    }

    public void LoadSettings(string path)
    {
        try
        {
            Settings.From(ConfigurationFile.Load(path), path);
            Problem = null;
            Status = "Konfiguration geladen: " + path;
        }
        catch (ConfigurationException error)
        {
            Problem = error.Message;
            Status = "Konfiguration nicht geladen.";
        }
    }

    /// <summary>Imports a Python configuration and shows every migration notice.</summary>
    public void ImportSettings(string path)
    {
        try
        {
            var (configuration, notices) = ConfigurationFile.Import(path);
            Settings.From(configuration, Settings.ConfigPath);
            Problem = notices.Count == 0 ? null : string.Join("\n", notices);
            Status = "Konfiguration übernommen aus " + path;
        }
        catch (ConfigurationException error)
        {
            Problem = error.Message;
            Status = "Konfiguration nicht übernommen.";
        }
    }

    public void SaveSettings(string path)
    {
        try
        {
            ConfigurationFile.Save(path, Settings.ToConfiguration());
            Settings.ConfigPath = path;
            Problem = null;
            Status = "Konfiguration gespeichert: " + path;
        }
        catch (ConfigurationException error)
        {
            Problem = error.Message;
            Status = "Konfiguration nicht gespeichert.";
        }
    }
}
