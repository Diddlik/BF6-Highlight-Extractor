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

public sealed partial class PreviewWindow : Window
{
    private readonly SegmentItem? item;
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private Media? media;
    private readonly DispatcherTimer timer;
    private bool updating;
    private bool seeking;
    private bool resumeAfterSeek;
    private bool initialSeekPending;
    private bool closed;

    public PreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += Tick;
        KeyDown += KeyPressed;
        Closed += (_, _) => DisposePlayer();
        Opened += (_, _) => this.FindControl<Button>("PlayPause")!.Focus();
        var seek = this.FindControl<Slider>("Seek")!;
        seek.PropertyChanged += SeekChanged;
        seek.AddHandler(InputElement.PointerPressedEvent, SeekStarted,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        seek.AddHandler(InputElement.PointerReleasedEvent, SeekFinished,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        seek.PointerCaptureLost += SeekFinished;
    }

    public PreviewWindow(SegmentItem segment) : this()
    {
        item = segment ?? throw new ArgumentNullException(nameof(segment));
        this.FindControl<TextBlock>("TitleText")!.Text = segment.SourceName;
        this.FindControl<TextBlock>("StatusText")!.Text =
            $"{segment.Kind} · {segment.Range}";
        var seek = this.FindControl<Slider>("Seek")!;
        seek.Maximum = Math.Max(segment.VideoDurationSeconds, segment.Segment.EndSeconds);
        seek.Value = Math.Clamp(segment.Segment.StartSeconds, 0, seek.Maximum);
        this.FindControl<TextBlock>("DurationText")!.Text = Format(seek.Maximum);
        initialSeekPending = true;
        StartPlayer(segment.SourcePath);
    }

    private void StartPlayer(string sourcePath)
    {
        try
        {
            Core.Initialize();
            libVlc = new LibVLC();
            player = new MediaPlayer(libVlc);
            this.FindControl<LibVLCSharp.Avalonia.VideoView>("Video")!.MediaPlayer = player;
            media = new Media(libVlc, sourcePath, FromType.FromPath);
            player.Play(media);
            timer.Start();
        }
        catch (Exception error) when (error is ArgumentException or IOException or VLCException)
        {
            this.FindControl<TextBlock>("StatusText")!.Text = "Video konnte nicht geöffnet werden: " + error.Message;
            this.FindControl<Button>("PlayPause")!.IsEnabled = false;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (closed || player is null) return;
        var length = player.Length;
        if (length > 0)
        {
            var duration = length / 1000d;
            var seek = this.FindControl<Slider>("Seek")!;
            if (Math.Abs(seek.Maximum - duration) > 0.01) seek.Maximum = duration;
            if (initialSeekPending)
            {
                initialSeekPending = false;
                player.Time = (long)Math.Round(item!.Segment.StartSeconds * 1000d);
                player.Pause();
            }
        }

        var seconds = seeking ? CurrentSeconds() : Math.Max(0, player.Time / 1000d);
        if (!seeking && player.IsPlaying
            && item is { } current && seconds >= current.Segment.EndSeconds)
        {
            player.Pause();
            seconds = current.Segment.EndSeconds;
        }
        var slider = this.FindControl<Slider>("Seek")!;
        if (!seeking)
        {
            updating = true;
            slider.Value = Math.Clamp(seconds, slider.Minimum, slider.Maximum);
            updating = false;
        }
        this.FindControl<TextBlock>("CurrentText")!.Text = Format(seconds);
        this.FindControl<Button>("PlayPause")!.Content = player.IsPlaying ? "Pause" : "Abspielen";
    }

    private void SeekChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (updating || player is null || e.Property != RangeBase.ValueProperty) return;
        var seconds = this.FindControl<Slider>("Seek")!.Value;
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
        if (player.IsPlaying) player.Pause();
        else
        {
            if (item is { } segment && CurrentSeconds() >= segment.Segment.EndSeconds - 0.05)
                player.Time = (long)Math.Round(segment.Segment.StartSeconds * 1000d);
            player.Play();
        }
    }

    private void StartClick(object? sender, RoutedEventArgs e)
    {
        if (item is null) return;
        item.SetStart(CurrentSeconds());
        UpdateStatus();
    }

    private void EndClick(object? sender, RoutedEventArgs e)
    {
        if (item is null) return;
        item.SetEnd(CurrentSeconds());
        UpdateStatus();
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; return; }
        if (e.Key is Key.Left or Key.Right)
        {
            var delta = e.Key == Key.Left ? -5 : 5;
            var slider = this.FindControl<Slider>("Seek")!;
            slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }
    }

    private double CurrentSeconds() => this.FindControl<Slider>("Seek")!.Value;

    private void UpdateStatus() => this.FindControl<TextBlock>("StatusText")!.Text =
        $"{item!.Kind} · {item.Range}";

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

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
        media = null;
        player = null;
        libVlc = null;
    }

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
}
