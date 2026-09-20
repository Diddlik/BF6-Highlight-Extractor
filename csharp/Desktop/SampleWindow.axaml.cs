using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Bf6Highlights.Desktop;

public sealed record SampleRequest(
    double Start, double End, double Timestamp, string Label, string Destination,
    string Player, string Split);

public sealed partial class SampleWindow : Window
{
    private double duration;

    public SampleRequest? Request { get; private set; }

    public SampleWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += OnKeyDown;
        Opened += (_, _) => this.FindControl<TextBox>("Start")!.Focus();
    }

    public SampleWindow(VideoItem video, string destination, string player = "") : this()
    {
        if (video.Metadata is not { } metadata)
            throw new ArgumentException("Das Video besitzt keine gültigen Metadaten.", nameof(video));

        duration = metadata.DurationSeconds;
        this.FindControl<TextBlock>("VideoHint")!.Text =
            $"{video.FileName} · Dauer {duration.ToString("0.###", CultureInfo.InvariantCulture)} s";
        var timestamp = duration / 2;
        this.FindControl<TextBox>("Start")!.Text = Math.Max(0, timestamp - 3).ToString("G17", CultureInfo.InvariantCulture);
        this.FindControl<TextBox>("Timestamp")!.Text = timestamp.ToString("G17", CultureInfo.InvariantCulture);
        this.FindControl<TextBox>("End")!.Text = Math.Min(duration, timestamp + 3).ToString("G17", CultureInfo.InvariantCulture);
        this.FindControl<ComboBox>("Label")!.ItemsSource = SampleExporter.Labels;
        this.FindControl<ComboBox>("Label")!.SelectedIndex = 0;
        this.FindControl<TextBox>("Player")!.Text = player;
        this.FindControl<ComboBox>("Split")!.ItemsSource = new[] { "development", "holdout", "unassigned" };
        this.FindControl<ComboBox>("Split")!.SelectedIndex = 0;
        this.FindControl<TextBox>("Destination")!.Text = destination;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void AcceptClick(object? sender, RoutedEventArgs e)
    {
        var startText = this.FindControl<TextBox>("Start")!.Text ?? "";
        var timestampText = this.FindControl<TextBox>("Timestamp")!.Text ?? "";
        var endText = this.FindControl<TextBox>("End")!.Text ?? "";
        var destination = (this.FindControl<TextBox>("Destination")!.Text ?? "").Trim();
        var label = this.FindControl<ComboBox>("Label")!.SelectedItem as string;
        var player = (this.FindControl<TextBox>("Player")!.Text ?? "").Trim();
        var split = this.FindControl<ComboBox>("Split")!.SelectedItem as string;

        if (!TryNumber(startText, out var start) || !TryNumber(timestampText, out var timestamp)
            || !TryNumber(endText, out var end))
        {
            ShowError("Start, Zeitmarke und Ende müssen endliche Zahlen sein.");
            return;
        }
        if (start < 0 || start >= timestamp || timestamp >= end || end > duration)
        {
            ShowError("Erwartet wird: 0 ≤ Start < Zeitmarke < Ende ≤ Videolänge.");
            return;
        }
        if (string.IsNullOrWhiteSpace(label) || !SampleExporter.Labels.Contains(label))
        {
            ShowError("Bitte ein gültiges Label auswählen.");
            return;
        }
        if (destination.Length == 0)
        {
            ShowError("Ein Zielordner ist erforderlich.");
            return;
        }
        if (split is not ("development" or "holdout" or "unassigned"))
        {
            ShowError("Bitte Entwicklung, Holdout oder unassigned auswählen.");
            return;
        }

        Request = new SampleRequest(start, end, timestamp, label, destination, player, split);
        Close();
    }

    private void ShowError(string message)
    {
        var error = this.FindControl<TextBlock>("Error")!;
        error.Text = message;
        error.IsVisible = true;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Request = null;
        Close();
    }
}
