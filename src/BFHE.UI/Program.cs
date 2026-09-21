using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using LibVLCSharp.Shared;
using Velopack;

namespace Bf6Highlights.Ui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles the install, update and uninstall hooks of the packaged application and
        // exits early for those; it must run before anything else touches the machine.
        VelopackApp.Build().Run();
        Core.Initialize();
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // Selection controls follow the warm accent of the design instead of the system blue.
        foreach (var (key, value) in new (string, string)[]
        {
            ("SystemAccentColor", "#E07B24"), ("SystemAccentColorLight1", "#EA8C3A"),
            ("SystemAccentColorLight2", "#F0A05C"), ("SystemAccentColorLight3", "#F5B77F"),
            ("SystemAccentColorDark1", "#C56A18"), ("SystemAccentColorDark2", "#A85811"),
            ("SystemAccentColorDark3", "#8A470B"),
        }) Resources[key] = Color.Parse(value);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
