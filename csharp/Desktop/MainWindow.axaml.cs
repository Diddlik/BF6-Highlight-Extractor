using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Bf6Highlights.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov", ".avi"];
    private readonly MainViewModel model = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
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
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Aufnahmen auswählen", AllowMultiple = true,
            FileTypeFilter = [new("Videos") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi"] }],
        });
        if (files.Count > 0) await model.AddVideosAsync(Collect(files));
    }

    private void RemoveVideo(object? sender, RoutedEventArgs e)
    {
        if (!model.Busy && (sender as Control)?.DataContext is VideoItem video) model.Remove(video);
    }

    private void ClearVideos(object? sender, RoutedEventArgs e) => model.ClearVideos();

    private async void ChooseOutput(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Ausgabeordner" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            model.Settings.OutputDirectory = path;
    }

    private async void LoadConfig(object? sender, RoutedEventArgs e)
    {
        if (await PickConfig("Konfiguration laden") is { } path) model.LoadSettings(path);
    }

    private async void ImportConfig(object? sender, RoutedEventArgs e)
    {
        if (await PickConfig("Python-Konfiguration übernehmen") is { } path) model.ImportSettings(path);
    }

    private async void SaveConfig(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Konfiguration speichern", SuggestedFileName = "config.yaml",
            DefaultExtension = "yaml",
            FileTypeChoices = [new("YAML") { Patterns = ["*.yaml", "*.yml"] }],
        });
        if (file?.TryGetLocalPath() is { } path) model.SaveSettings(path);
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

    private async void StartAnalysis(object? sender, RoutedEventArgs e) => await model.AnalyzeAsync();

    private async void ExportSelection(object? sender, RoutedEventArgs e) =>
        await model.ExportSelectionAsync();

    private void CancelWork(object? sender, RoutedEventArgs e) => model.Cancel();

    private void SelectionChanged(object? sender, RoutedEventArgs e) => model.SelectionChanged();
}
