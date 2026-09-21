using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Bf6Highlights.Ui;

public sealed record SampleRequest(
    double Start, double End, double Timestamp, string Label, string Destination,
    string Player, string Split);

public sealed partial class SampleWindow : Window
{
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private Media? media;
    private readonly DispatcherTimer timer;
    private SampleSession? session;
    private bool updating;
    private bool seeking;
    private bool resumeAfterSeek;
    private bool initialPausePending;
    private bool closed;
    private double frameStep;
    private string destination = "";
    private bool editing;

    public IReadOnlyList<SampleRequest> Requests { get; private set; } = [];

    /// <summary>Where the drafts of this session live, so they can be dropped after the export.</summary>
    public string DraftDestination { get; private set; } = "";

    public SampleWindow()
    {
        AvaloniaXamlLoader.Load(this);
        timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += Tick;
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        Closed += (_, _) => DisposePlayer();
        Opened += (_, _) => this.FindControl<Button>("PlayPause")!.Focus();
        var seek = this.FindControl<Slider>("Seek")!;
        seek.PropertyChanged += SeekChanged;
        seek.AddHandler(InputElement.PointerPressedEvent, SeekStarted, RoutingStrategies.Tunnel, true);
        seek.AddHandler(InputElement.PointerReleasedEvent, SeekFinished, RoutingStrategies.Tunnel, true);
        seek.PointerCaptureLost += SeekFinished;
    }

    public SampleWindow(VideoItem video, string firstDestination, string playerName = "") : this()
    {
        if (video.Metadata is not { } metadata)
            throw new ArgumentException("Das Video besitzt keine gültigen Metadaten.", nameof(video));

        frameStep = metadata.Fps > 0 ? 1 / metadata.Fps : 0;
        destination = firstDestination;
        session = new(metadata.DurationSeconds, firstDestination, video.Path);
        this.FindControl<ListBox>("MarkerList")!.ItemsSource = session.Markers;
        this.FindControl<TextBlock>("TitleText")!.Text = video.FileName;
        this.FindControl<TextBlock>("StatusText")!.Text = "Position suchen und mit einer Taste markieren. Exportiert wird erst nach der Kontrolle.";
        this.FindControl<TextBox>("Player")!.Text = playerName;
        this.FindControl<ComboBox>("Split")!.ItemsSource = new[] { "development", "holdout", "unassigned" };
        this.FindControl<ComboBox>("Split")!.SelectedIndex = 0;
        var seek = this.FindControl<Slider>("Seek")!;
        seek.Maximum = metadata.DurationSeconds;
        this.FindControl<TextBlock>("DurationText")!.Text = Format(metadata.DurationSeconds);
        var labels = this.FindControl<ComboBox>("EditLabel")!;
        labels.ItemsSource = SampleExporter.Labels.Select(SampleMarker.LabelName).ToArray();
        session.Markers.CollectionChanged += (_, _) => DrawMarkers();
        this.FindControl<Canvas>("MarkerTrack")!.SizeChanged += (_, _) => DrawMarkers();

        // An interrupted session left its drafts next to the destination.
        var drafts = SampleSession.LoadDrafts(firstDestination, video.Path);
        if (drafts.Count > 0)
        {
            session.Restore(drafts);
            UpdateQueue($"{drafts.Count} Markierung(en) aus einer unterbrochenen Sitzung übernommen.");
        }
        StartPlayer(video.Path);
    }

    private void StartPlayer(string path)
    {
        try
        {
            Core.Initialize();
            libVlc = new LibVLC("--no-video-title-show");
            player = new MediaPlayer(libVlc);
            this.FindControl<LibVLCSharp.Avalonia.VideoView>("Video")!.MediaPlayer = player;
            media = new Media(libVlc, path, FromType.FromPath);
            player.Play(media);
            initialPausePending = true;
            timer.Start();
        }
        catch (Exception error) when (error is ArgumentException or IOException or VLCException)
        {
            ShowError("Video konnte nicht geöffnet werden: " + error.Message);
            this.FindControl<Button>("PlayPause")!.IsEnabled = false;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (closed || player is null || seeking) return;
        if (initialPausePending && player.Length > 0)
        {
            initialPausePending = false;
            player.Time = 0;
            player.Pause();
        }
        var seconds = Math.Max(0, player.Time / 1000d);
        var seek = this.FindControl<Slider>("Seek")!;
        updating = true;
        seek.Value = Math.Clamp(seconds, seek.Minimum, seek.Maximum);
        updating = false;
        this.FindControl<TextBlock>("CurrentText")!.Text = Format(seconds);
        this.FindControl<Button>("PlayPause")!.Content = player.IsPlaying ? "Pause" : "Abspielen";
    }

    private void SeekChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (updating || player is null || e.Property != RangeBase.ValueProperty) return;
        var seconds = CurrentSeconds();
        player.Time = (long)Math.Round(seconds * 1000d);
        this.FindControl<TextBlock>("CurrentText")!.Text = Format(seconds);
    }

    private void SeekStarted(object? sender, PointerPressedEventArgs e)
    {
        if (player is null) return;
        seeking = true;
        resumeAfterSeek = player.IsPlaying;
        if (resumeAfterSeek) player.Pause();
    }

    private void SeekFinished(object? sender, RoutedEventArgs e)
    {
        if (!seeking || player is null) return;
        seeking = false;
        player.Time = (long)Math.Round(CurrentSeconds() * 1000d);
        if (resumeAfterSeek) player.Play();
        resumeAfterSeek = false;
    }

    private void PlayPauseClick(object? sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (player is null) return;
        if (player.IsPlaying) player.Pause(); else player.Play();
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (this.FindControl<TextBox>("Player")!.IsKeyboardFocusWithin
            || this.FindControl<ComboBox>("Split")!.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; return; }
        if (e.Key is Key.Left or Key.Right)
        {
            // A single frame comes from the source frame rate; LibVLC seeks to the nearest
            // keyframe, the exported still is cut by FFmpeg at the exact timestamp.
            var distance = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 5
                : e.KeyModifiers.HasFlag(KeyModifiers.Shift) && frameStep > 0 ? frameStep
                : 1;
            SeekBy(e.Key == Key.Left ? -distance : distance);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Undo();
            e.Handled = true;
            return;
        }
        var label = e.Key switch
        {
            Key.K => "own_kill", Key.F => "foreign_kill", Key.D => "own_death",
            Key.H => "headshot", Key.M => "no_event_marker", Key.N => "no_event",
            Key.X => "multiple_kills", _ => null,
        };
        if (label is not null) { Mark(label); e.Handled = true; }
    }

    private void SeekBy(double delta)
    {
        if (player is null) return;
        player.Pause();
        var seek = this.FindControl<Slider>("Seek")!;
        seek.Value = Math.Clamp(CurrentSeconds() + delta, seek.Minimum, seek.Maximum);
    }

    private void MarkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string label) Mark(label);
    }

    private void Mark(string label)
    {
        if (session?.Add(CurrentSeconds(), label) != true)
        {
            ShowError("Diese Markierung existiert an der aktuellen Position bereits.");
            return;
        }
        HideError();
        this.FindControl<ListBox>("MarkerList")!.SelectedIndex = session.Markers.Count - 1;
        UpdateQueue($"{SampleMarker.LabelName(label)} bei {Format(CurrentSeconds())} markiert.");
    }

    private void UndoClick(object? sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (session?.Undo() == true) UpdateQueue("Letzte Markierung entfernt.");
    }

    private void RemoveClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || this.FindControl<ListBox>("MarkerList")!.SelectedItem is not SampleMarker marker) return;
        session.Remove(marker);
        UpdateQueue("Markierung entfernt.");
    }

    private void MarkerSelected(object? sender, SelectionChangedEventArgs e)
    {
        var marker = (sender as ListBox)?.SelectedItem as SampleMarker;
        if (marker is not null) this.FindControl<Slider>("Seek")!.Value = marker.Timestamp;
        ShowEditor(marker);
        DrawMarkers();
    }

    /// <summary>The editor works on the draft only; exported samples stay as they are.</summary>
    private void ShowEditor(SampleMarker? marker)
    {
        this.FindControl<Border>("EditPanel")!.IsVisible = marker is not null;
        if (marker is null) return;
        editing = true;
        this.FindControl<ComboBox>("EditLabel")!.SelectedIndex =
            Array.IndexOf(SampleExporter.Labels, marker.Label);
        editing = false;
    }

    private void EditLabelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (editing || session is null) return;
        var list = this.FindControl<ListBox>("MarkerList")!;
        var index = this.FindControl<ComboBox>("EditLabel")!.SelectedIndex;
        if (list.SelectedItem is not SampleMarker marker || index < 0) return;
        var label = SampleExporter.Labels[index];
        if (label == marker.Label) return;
        if (!session.Update(marker, marker.Timestamp, label))
        {
            ShowError("Dieses Label liegt an dieser Position bereits vor.");
            ShowEditor(marker);
            return;
        }
        HideError();
        Select(session.Markers.FirstOrDefault(entry => entry.Label == label
            && Math.Abs(entry.Timestamp - marker.Timestamp) < 0.001));
        UpdateQueue($"Label geändert auf {SampleMarker.LabelName(label)}.");
    }

    private void MoveMarkerClick(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("MarkerList")!;
        if (session is null || list.SelectedItem is not SampleMarker marker) return;
        var timestamp = CurrentSeconds();
        if (!session.Update(marker, timestamp, marker.Label))
        {
            ShowError("An dieser Position gibt es dieses Label schon.");
            return;
        }
        HideError();
        Select(session.Markers.FirstOrDefault(entry => entry.Label == marker.Label
            && Math.Abs(entry.Timestamp - timestamp) < 0.05));
        UpdateQueue($"Markierung auf {Format(timestamp)} verschoben.");
    }

    private void Select(SampleMarker? marker)
    {
        var list = this.FindControl<ListBox>("MarkerList")!;
        list.SelectedItem = marker;
        ShowEditor(marker);
    }

    /// <summary>Draws one coloured tick per marker over the seek bar.</summary>
    private void DrawMarkers()
    {
        var track = this.FindControl<Canvas>("MarkerTrack")!;
        track.Children.Clear();
        var seek = this.FindControl<Slider>("Seek")!;
        var selected = this.FindControl<ListBox>("MarkerList")!.SelectedItem as SampleMarker;
        if (session is null || seek.Maximum <= 0 || track.Bounds.Width <= 0) return;
        foreach (var marker in session.Markers)
        {
            var chosen = ReferenceEquals(marker, selected);
            var tick = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = chosen ? 4 : 2,
                Height = chosen ? 10 : 8,
                Fill = Avalonia.Media.SolidColorBrush.Parse(SampleMarker.LabelColour(marker.Label)),
            };
            ToolTip.SetTip(tick, marker.Display);
            Canvas.SetLeft(tick, Math.Clamp(marker.Timestamp / seek.Maximum * track.Bounds.Width,
                0, Math.Max(0, track.Bounds.Width - tick.Width)));
            Canvas.SetTop(tick, chosen ? 0 : 1);
            track.Children.Add(tick);
        }
    }

    private void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || session.Markers.Count == 0) return;
        var split = this.FindControl<ComboBox>("Split")!.SelectedItem as string;
        if (split is not ("development" or "holdout" or "unassigned"))
        {
            ShowError("Bitte einen Datensatz auswählen.");
            return;
        }
        Requests = session.BuildRequests(this.FindControl<TextBox>("Player")!.Text ?? "", split);
        DraftDestination = destination;
        Close();
    }

    private void UpdateQueue(string status)
    {
        var count = session?.Markers.Count ?? 0;
        this.FindControl<TextBlock>("QueueHint")!.Text = count == 0 ? "Noch keine Markierung" : $"{count} Markierung(en) · {status}";
        var export = this.FindControl<Button>("Export")!;
        export.IsEnabled = count > 0;
        export.Content = count == 1 ? "1 Sample exportieren" : $"{count} Samples exportieren";
    }

    private void ShowError(string message)
    {
        var error = this.FindControl<TextBlock>("Error")!;
        error.Text = message;
        error.IsVisible = true;
    }

    private void HideError() => this.FindControl<TextBlock>("Error")!.IsVisible = false;
    private double CurrentSeconds() => this.FindControl<Slider>("Seek")!.Value;
    private void CancelClick(object? sender, RoutedEventArgs e) { Requests = []; Close(); }

    private void DisposePlayer()
    {
        if (closed) return;
        closed = true;
        timer.Stop();
        var view = this.FindControl<LibVLCSharp.Avalonia.VideoView>("Video");
        if (view is not null) view.MediaPlayer = null;
        player?.Stop();
        media?.Dispose();
        player?.Dispose();
        libVlc?.Dispose();
    }

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
