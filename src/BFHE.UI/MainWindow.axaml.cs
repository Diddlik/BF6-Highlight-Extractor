using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Bf6Highlights.Ui;

public sealed partial class MainWindow : Window
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov", ".avi"];
    private readonly MainViewModel model = new();
    private bool dialogOpen;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
        model.LoadUserSettings();
        Opened += async (_, _) => await model.CheckUpdatesOnStartAsync();
        var zone = this.FindControl<Border>("DropZone")!;
        DragDrop.SetAllowDrop(zone, true);
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects =
            e.DataTransfer?.Contains(DataFormat.File) == true
                ? DragDropEffects.Copy : DragDropEffects.None);
        zone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (model.Busy || e.DataTransfer?.TryGetFiles() is not { } dropped) return;
            await model.AddVideosAsync(Collect(dropped));
        });
    }

    /// <summary>Files with a supported extension; dropped folders are searched recursively.</summary>
    private static List<string> Collect(IEnumerable<IStorageItem> items)
    {
        var paths = new List<string>();
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (path is null) continue;
            if (Directory.Exists(path))
                paths.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(Supported).Order(StringComparer.OrdinalIgnoreCase));
            else if (Supported(path)) paths.Add(path);
        }
        return paths;
    }

    private static bool Supported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private async void ChooseVideos(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Aufnahmen auswählen", AllowMultiple = true,
                FileTypeFilter = [new("Videos") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi"] }],
            });
            if (files.Count > 0) await model.AddVideosAsync(Collect(files));
        }
        finally { dialogOpen = false; }
    }

    private void RemoveVideo(object? sender, RoutedEventArgs e)
    {
        if (!model.Busy && (sender as Control)?.DataContext is VideoItem video) model.Remove(video);
    }

    private async void CreateSample(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not VideoItem { Valid: true } video || !BeginDialog())
            return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "Überordner für den Trainingsfall",
            });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } parent) return;
            var destination = UniqueSamplePath(parent);
            var player = model.Settings.PlayerNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            var dialog = new SampleWindow(video, destination, player);
            await dialog.ShowDialog(this);
            if (dialog.Request is { } request) await model.ExportSampleAsync(video, request);
        }
        finally { dialogOpen = false; }
    }

    private static string UniqueSamplePath(string parent)
    {
        var stem = "sample-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(parent, stem);
        for (var suffix = 2; Directory.Exists(path) || File.Exists(path); suffix++)
            path = Path.Combine(parent, $"{stem}-{suffix}");
        return path;
    }

    private void ClearVideos(object? sender, RoutedEventArgs e) => model.ClearVideos();

    private async void ChooseOutput(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Ausgabeordner" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
                model.Settings.OutputDirectory = path;
        }
        finally { dialogOpen = false; }
    }

    private async void LoadConfig(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        try { if (await PickConfig("Konfiguration laden") is { } path) model.LoadSettings(path); }
        finally { dialogOpen = false; }
    }

    private async void ImportConfig(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        try { if (await PickConfig("Python-Konfiguration übernehmen") is { } path) model.ImportSettings(path); }
        finally { dialogOpen = false; }
    }

    private void SaveConfigHere(object? sender, RoutedEventArgs e) => model.SaveUserSettings();

    private async void SaveConfig(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Konfiguration speichern", SuggestedFileName = "config.yaml",
                DefaultExtension = "yaml",
                FileTypeChoices = [new("YAML") { Patterns = ["*.yaml", "*.yml"] }],
            });
            if (file?.TryGetLocalPath() is { } path) model.SaveSettings(path);
        }
        finally { dialogOpen = false; }
    }

    private async Task<string?> PickConfig(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new("YAML") { Patterns = ["*.yaml", "*.yml"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void PickRegion(object? sender, RoutedEventArgs e)
    {
        if (!BeginDialog()) return;
        var source = (sender as Control)?.DataContext as VideoItem;
        try
        {
            if (model.FrameForRegion(source) is not { } frame) return;
            var editor = new RegionWindow(frame, model.RegionForEditor(frame.Video));
            await editor.ShowDialog(this);
            if (editor.Region is { } region) model.ApplyRegion(region, frame.Video);
        }
        catch (InvalidOperationException error)
        {
            model.ReportProblem("Videovorschau konnte nicht geöffnet werden: " + error.Message);
        }
        finally { dialogOpen = false; }
    }

    private async void StartAnalysis(object? sender, RoutedEventArgs e) => await model.AnalyzeAsync();

    private async void ExportSelection(object? sender, RoutedEventArgs e) =>
        await model.ExportSelectionAsync();

    private async void PreviewHighlight(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SegmentItem item || !BeginDialog()) return;
        try { await new PreviewWindow(item).ShowDialog(this); }
        finally { dialogOpen = false; }
    }

    private bool BeginDialog()
    {
        if (dialogOpen || model.Busy) return false;
        dialogOpen = true;
        return true;
    }

    private async void CheckUpdates(object? sender, RoutedEventArgs e) =>
        await model.CheckUpdatesAsync();

    private async void ApplyUpdate(object? sender, RoutedEventArgs e) =>
        await model.ApplyUpdateAsync();

    private void GoVideos(object? sender, RoutedEventArgs e) => model.Area = 0;

    private void GoSettings(object? sender, RoutedEventArgs e) => model.Area = 1;

    private void GoAnalysis(object? sender, RoutedEventArgs e) => model.Area = 2;

    private void GoHighlights(object? sender, RoutedEventArgs e) => model.Area = 3;

    private void SelectAll(object? sender, RoutedEventArgs e) => model.SelectAll(true);

    private void SelectNone(object? sender, RoutedEventArgs e) => model.SelectAll(false);

    private void RemoveHighlight(object? sender, RoutedEventArgs e)
    {
        if (!model.Busy && (sender as Control)?.DataContext is SegmentItem item)
            model.RemoveHighlight(item);
    }

    /// <summary>Opens the clips folder of the last export in the file manager.</summary>
    private void OpenExportFolder(object? sender, RoutedEventArgs e)
    {
        var directory = model.LastExportDirectory ?? model.Settings.OutputDirectory;
        if (!Directory.Exists(directory))
        {
            model.Note("Der Ordner existiert noch nicht: " + directory);
            return;
        }
        using var explorer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.GetFullPath(directory)) { UseShellExecute = true });
    }

    private void CancelWork(object? sender, RoutedEventArgs e) => model.Cancel();

    private void SelectionChanged(object? sender, RoutedEventArgs e) => model.SelectionChanged();
}
