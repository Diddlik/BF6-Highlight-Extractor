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
    public string Opponents => Segment.Events.Count == 0 ? "" : "Gegner: " + string.Join(", ",
        Segment.Events.Select(e => e.OpponentName ?? "unbekannt").Distinct());

    /// <summary>
    /// How sure the detection was. This is the measured quality of the recognition, not a
    /// rating of the scene; a scoring system is deliberately not part of the migration.
    /// </summary>
    public string Quality => Segment.Events.Count == 0 ? ""
        : $"Erkennung {Segment.Events.Average(e => e.SimilarityScore):0} % · "
          + $"OCR {Segment.Events.Average(e => e.Confidence) * 100:0} %";

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

/// <summary>One line of the live event stream in the analysis view.</summary>
public sealed record StreamEntry(string Time, string Kind, string Detail);

/// <summary>
/// The window's state. Every action runs through the same Core services the CLI uses; the
/// analysis and the export run off the UI thread and can be cancelled.
/// </summary>
public sealed class MainViewModel : Observable
{
    private CancellationTokenSource? cancellation;

    public ObservableCollection<VideoItem> Videos { get; } = [];
    public ObservableCollection<SegmentItem> Highlights { get; } = [];
    /// <summary>The candidates as the list shows them: filtered and sorted.</summary>
    public ObservableCollection<SegmentItem> VisibleHighlights { get; } = [];
    public ObservableCollection<StreamEntry> Events { get; } = [];
    public SettingsViewModel Settings { get; } = new();

    // Which of the four areas is on screen.
    private int area;
    public int Area
    {
        get => area;
        set
        {
            Set(ref area, value);
            foreach (var name in new[]
            {
                nameof(ShowVideos), nameof(ShowSettings), nameof(ShowAnalysis),
                nameof(ShowHighlights), nameof(AreaTitle), nameof(AreaSubtitle),
            }) Raise(name);
        }
    }
    public bool ShowVideos => area == 0;
    public bool ShowSettings => area == 1;
    public bool ShowAnalysis => area == 2;
    public bool ShowHighlights => area == 3;

    public string AreaTitle => area switch
    {
        0 => "Neues Projekt", 1 => "Einstellungen", 2 => "Analyse", _ => "Highlights",
    };
    public string AreaSubtitle => area switch
    {
        0 => "Aufnahmen auswählen und die Analyse vorbereiten",
        1 => "Erkennung, Leistung und Ausgabe festlegen",
        2 => "Laufenden Vorgang beobachten",
        _ => "Kandidaten prüfen, Grenzen anpassen und exportieren",
    };

    /// <summary>State of each area for the navigation: not started, ready, active, done, failed.</summary>
    public string VideosState => Videos.Count == 0 ? "offen"
        : Videos.Any(video => !video.Valid) ? "Hinweis" : "bereit";
    public string SettingsState => string.IsNullOrWhiteSpace(Settings.OutputDirectory) ? "offen"
        : Settings.RegionForEditor() is null ? "Bereich fehlt" : "bereit";
    public string AnalysisState => Busy && !Exporting ? "läuft"
        : analysisFailed ? "Fehler"
        : analysisDone ? (interruptedRun ? "abgebrochen" : "fertig")
        : CanStart ? "bereit" : "offen";
    public string HighlightsState => Highlights.Count == 0
        ? (analysisDone ? "keine Treffer" : "offen")
        : $"{SelectedCount} von {Highlights.Count} gewählt";

    private bool analysisDone;
    private bool analysisFailed;
    private bool interruptedRun;

    private bool exporting;
    public bool Exporting { get => exporting; private set => Set(ref exporting, value); }

    // Analysis detail, shown while a run is in progress.
    private string currentVideo = "Keine Analyse gestartet";
    public string CurrentVideo { get => currentVideo; private set => Set(ref currentVideo, value); }
    private string queuePosition = "Aufnahmen hinzufügen, dann die Analyse starten";
    public string QueuePosition { get => queuePosition; private set => Set(ref queuePosition, value); }
    private string position = "00:00:00.000 / 00:00:00.000";
    public string Position { get => position; private set => Set(ref position, value); }
    private string elapsed = "00:00";
    public string Elapsed { get => elapsed; private set => Set(ref elapsed, value); }
    private string remaining = "unbekannt";
    public string Remaining { get => remaining; private set => Set(ref remaining, value); }
    private int framesSampled;
    public int FramesSampled { get => framesSampled; private set => Set(ref framesSampled, value); }
    private int ocrCalls;
    public int OcrCalls { get => ocrCalls; private set => Set(ref ocrCalls, value); }
    private int templateChecks;
    public int TemplateChecks { get => templateChecks; private set => Set(ref templateChecks, value); }
    private int killsDetected;
    public int KillsDetected { get => killsDetected; private set => Set(ref killsDetected, value); }
    private string step = "bereit";
    public string Step { get => step; private set => Set(ref step, value); }

    // Sorting and filtering of the candidate list.
    private string sortBy = "time";
    public string SortBy { get => sortBy; private set { Set(ref sortBy, value); RefreshHighlights(); } }
    public bool SortByTime { get => sortBy == "time"; set { if (value) SortBy = "time"; } }
    public bool SortByKills { get => sortBy == "kills"; set { if (value) SortBy = "kills"; } }
    public bool SortByDuration { get => sortBy == "duration"; set { if (value) SortBy = "duration"; } }

    private string filter = "all";
    public string Filter { get => filter; private set { Set(ref filter, value); RefreshHighlights(); } }
    public bool FilterAll { get => filter == "all"; set { if (value) Filter = "all"; } }
    public bool FilterSingle { get => filter == "single"; set { if (value) Filter = "single"; } }
    public bool FilterMulti { get => filter == "multi"; set { if (value) Filter = "multi"; } }

    public int SelectedCount => Highlights.Count(item => item.Selected);
    public string SelectionSummary => Highlights.Count == 0 ? "Keine Kandidaten"
        : $"{SelectedCount} von {Highlights.Count} gewählt · "
          + $"{Highlights.Where(item => item.Selected).Sum(item => item.Segment.DurationSeconds):0.0} s";

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
        updates = new UpdateService(() => Settings.UpdateSettings());
        Videos.CollectionChanged += (_, _) => RaiseAll();
        Highlights.CollectionChanged += (_, _) => { RefreshHighlights(); RaiseAll(); };
        Settings.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName is nameof(SettingsViewModel.RepositoryUrl)
                or nameof(SettingsViewModel.PrereleaseUpdates)) updates.Forget();
            RaiseAll();
        };
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(CanStart), nameof(StartHint), nameof(CanExport), nameof(VideosState),
            nameof(SettingsState), nameof(AnalysisState), nameof(HighlightsState),
            nameof(SelectedCount), nameof(SelectionSummary),
        }) Raise(name);
    }

    public void Note(string message) => Status = message;

    // Updates: the check runs on demand, and on start only when it is switched on.
    private readonly UpdateService updates;

    private string updateStatus = "Noch nicht geprüft.";
    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public string VersionText => "Version " + updates.Version;

    private bool updateReady;
    public bool UpdateReady { get => updateReady; private set => Set(ref updateReady, value); }

    private bool checkingUpdate;
    public bool CheckingUpdate
    {
        get => checkingUpdate;
        private set { Set(ref checkingUpdate, value); Raise(nameof(CanCheckUpdate)); }
    }
    public bool CanCheckUpdate => !checkingUpdate;

    public async Task CheckUpdatesAsync(bool silent = false)
    {
        if (CheckingUpdate) return;
        CheckingUpdate = true;
        UpdateStatus = "Suche nach Aktualisierung …";
        try
        {
            var state = await updates.CheckAsync();
            UpdateStatus = state.Message;
            UpdateReady = state.HasUpdate;
            if (!silent) Status = state.Message;
        }
        finally { CheckingUpdate = false; }
    }

    /// <summary>Only runs when the user left the automatic check switched on.</summary>
    public async Task CheckUpdatesOnStartAsync()
    {
        if (!Settings.AutomaticUpdates || !updates.Installed)
        {
            UpdateStatus = updates.Installed
                ? "Automatische Suche ist ausgeschaltet."
                : "Aktualisierung gibt es nur in der installierten Fassung.";
            return;
        }
        await CheckUpdatesAsync(silent: true);
    }

    public async Task ApplyUpdateAsync()
    {
        if (!UpdateReady) return;
        CheckingUpdate = true;
        try
        {
            var state = await updates.ApplyAsync(new Progress<int>(percent =>
                UpdateStatus = $"Wird geladen … {percent} %"));
            UpdateStatus = state.Message;
            Status = state.Message;
        }
        finally { CheckingUpdate = false; }
    }

    public void SelectionChanged() => RaiseAll();

    /// <summary>Applies filter and sort order to the list the window shows.</summary>
    private void RefreshHighlights()
    {
        foreach (var name in new[] { nameof(SortByTime), nameof(SortByKills), nameof(SortByDuration),
            nameof(FilterAll), nameof(FilterSingle), nameof(FilterMulti) }) Raise(name);
        var visible = Highlights.Where(item => filter switch
        {
            "single" => item.Segment.Events.Count <= 1,
            "multi" => item.Segment.Events.Count > 1,
            _ => true,
        });
        visible = sortBy switch
        {
            "kills" => visible.OrderByDescending(item => item.Segment.Events.Count)
                .ThenBy(item => item.Segment.StartSeconds),
            "duration" => visible.OrderByDescending(item => item.Segment.DurationSeconds)
                .ThenBy(item => item.Segment.StartSeconds),
            _ => visible.OrderBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Segment.StartSeconds),
        };
        VisibleHighlights.Clear();
        foreach (var item in visible) VisibleHighlights.Add(item);
        Raise(nameof(HasHighlights));
        Raise(nameof(VisibleEmpty));
    }

    public bool HasHighlights => VisibleHighlights.Count > 0;
    public bool VisibleEmpty => VisibleHighlights.Count == 0;

    public void SelectAll(bool selected)
    {
        foreach (var item in VisibleHighlights) item.Selected = selected;
        RaiseAll();
    }

    public void RemoveHighlight(SegmentItem item)
    {
        Highlights.Remove(item);
        RefreshHighlights();
        RaiseAll();
    }

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

    private static string Clock(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
        : $"{span.Minutes:00}:{span.Seconds:00}";

    private static string StepName(string stage) => stage switch
    {
        "analyzing" => "Erkennung", "reporting" => "Berichte", "exporting" => "Clips schneiden",
        "done" => "fertig", _ => stage,
    };

    public async Task AnalyzeAsync()
    {
        if (!CanStart) return;
        Problem = null;
        Highlights.Clear();
        Events.Clear();
        analysisDone = false;
        analysisFailed = false;
        interruptedRun = false;
        Area = 2;
        Busy = true;
        using var cancel = new CancellationTokenSource();
        cancellation = cancel;
        var kills = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var configuration = Settings.ToConfiguration();
            var service = new AnalysisService(configuration, () => new OnnxOcrEngine());
            var queue = Videos.Where(item => item.Valid).ToArray();
            for (var index = 0; index < queue.Length; index++)
            {
                var video = queue[index];
                CurrentVideo = video.FileName;
                QueuePosition = $"Video {index + 1} von {queue.Length}";
                Stage = "Analyse: " + video.FileName;
                var target = System.IO.Path.Combine(Settings.OutputDirectory,
                    System.IO.Path.GetFileNameWithoutExtension(video.Path));
                var result = await Task.Run(() => service.RunAsync(video.Path, target,
                    new Progress<AnalysisProgress>(update =>
                    {
                        Progress = update.Percent;
                        Stage = $"{video.FileName}: {StepName(update.Stage)}";
                        Step = StepName(update.Stage);
                        Position = $"{Reports.FormatTimestamp(update.TimestampSeconds)} / "
                            + Reports.FormatTimestamp(update.DurationSeconds);
                        FramesSampled = update.FramesSampled;
                        OcrCalls = update.OcrCalls;
                        TemplateChecks = update.TemplateChecks;
                        KillsDetected = kills + update.KillsDetected;
                        Elapsed = Clock(watch.Elapsed);
                        Remaining = update.Percent > 1
                            ? Clock(TimeSpan.FromSeconds(watch.Elapsed.TotalSeconds
                                * (100 - update.Percent) / update.Percent))
                            : "wird geschätzt";
                        if (update.LastEvent is { } kill)
                            Events.Insert(0, new(Reports.FormatTimestamp(kill.TimestampSeconds),
                                kill.EventType == "headshot" ? "Headshot bestätigt" : "Kill bestätigt",
                                $"{kill.SimilarityScore:0} % · {kill.OpponentName ?? "Gegner unbekannt"}"));
                    }), cancel.Token), cancel.Token);
                foreach (var segment in result.Segments)
                    Highlights.Add(new SegmentItem
                    {
                        SourcePath = video.Path, Segment = segment,
                        VideoDurationSeconds = result.Video.DurationSeconds,
                    });
                kills += result.Events.Count;
                if (result.Interrupted) { interruptedRun = true; break; }
            }
            analysisDone = true;
            Status = Highlights.Count == 0
                ? kills == 0
                    ? "Keine Kills gefunden. Bereich und Schwellwerte prüfen."
                    : "Keine Clip-Abschnitte gebildet."
                : $"{kills} Kill-Kandidaten in {Highlights.Count} Abschnitten. "
                  + "Auswahl prüfen und exportieren.";
        }
        catch (OperationCanceledException)
        {
            interruptedRun = true;
            analysisDone = true;
            Status = $"Abgebrochen. {Highlights.Count} Abschnitte bleiben erhalten.";
        }
        catch (Exception error) when (error is ConfigurationException or IOException
            or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or OpenCvSharp.OpenCVException)
        {
            analysisFailed = true;
            Problem = error.Message;
            Status = "Analyse fehlgeschlagen.";
        }
        finally
        {
            cancellation = null;
            Busy = false;
            Stage = "";
            Step = analysisFailed ? "abgebrochen" : "fertig";
            Progress = 0;
            if (Highlights.Count > 0) Area = 3;
            RaiseAll();
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
            Problem = failures.Count == 0 ? null
                : $"{failures.Count} Clip(s) fehlgeschlagen:\n" + string.Join("\n", failures);
            Status = failures.Count == 0
                ? $"{written} Clip(s) geschrieben nach {directory}."
                : $"{written} Clip(s) geschrieben, {failures.Count} fehlgeschlagen. Ziel: {directory}.";
            LastExportDirectory = directory;
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
            Exporting = false;
            Stage = "";
            RaiseAll();
        }
    }

    /// <summary>The clips folder of the last export, for the action that opens it.</summary>
    public string? LastExportDirectory { get; private set; }

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

    /// <summary>
    /// Loads the configuration of this installation from the user folder. On a first start there
    /// is none; the path is kept so that saving needs no dialog.
    /// </summary>
    public void LoadUserSettings()
    {
        var path = ConfigurationFile.DefaultUserConfigPath;
        if (File.Exists(path)) { LoadSettings(path); return; }
        Settings.ConfigPath = path;
        Status = "Noch keine gespeicherte Konfiguration. Einstellungen prüfen und speichern.";
    }

    /// <summary>Saves to the loaded file, or to the user folder when none was opened yet.</summary>
    public void SaveUserSettings() => SaveSettings(string.IsNullOrWhiteSpace(Settings.ConfigPath)
        ? ConfigurationFile.DefaultUserConfigPath : Settings.ConfigPath);

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
