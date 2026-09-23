using Velopack;
using Velopack.Sources;

namespace Bf6Highlights.Ui;

/// <summary>What the window shows about updates after a check.</summary>
public sealed record UpdateState(string Message, string? AvailableVersion = null)
{
    public bool HasUpdate => AvailableVersion is not null;
}

/// <summary>
/// Looks for a newer release on GitHub and installs it. Only the packaged application can
/// update itself; started from a build directory the check reports that and does nothing.
/// </summary>
public sealed class UpdateService(Func<UpdateSettings> settings)
{
    private UpdateManager? manager;
    private UpdateInfo? pending;

    public static string AssemblyVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "unbekannt";

    /// <summary>The version of the installed package, or the assembly version in a build.</summary>
    public string Version => Manager()?.CurrentVersion?.ToString() ?? AssemblyVersion;

    /// <summary>True when the application runs from an installed package.</summary>
    public bool Installed => Manager()?.IsInstalled == true;

    private UpdateManager? Manager()
    {
        if (manager is not null) return manager;
        var current = settings();
        if (!Uri.TryCreate(current.RepositoryUrl, UriKind.Absolute, out var repository)
            || repository.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            // Rebuilt when the repository or the channel changed. GitHub's release list kept showing
            // new releases without their assets for hours, which hid them from GithubSource; the
            // download URL of the latest release is current at once. Prereleases are never "latest".
            IUpdateSource source = current.Prerelease
                ? new GithubSource(current.RepositoryUrl, null, true)
                : new SimpleWebSource(current.RepositoryUrl.TrimEnd('/') + "/releases/latest/download/");
            return manager = new UpdateManager(source);
        }
        catch (Exception error) when (error is IOException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            // Outside an installed package Velopack has no place to work in.
            return null;
        }
    }

    public void Forget()
    {
        manager = null;
        pending = null;
    }

    public async Task<UpdateState> CheckAsync()
    {
        pending = null;
        var updates = Manager();
        if (updates is null)
            return new($"Version {AssemblyVersion}. Aktualisierung gibt es nur in der "
                + "installierten Fassung mit gültiger https-Adresse.");
        if (!updates.IsInstalled)
            return new($"Version {AssemblyVersion}. Aktualisierung gibt es nur in der "
                + "installierten Fassung.");
        try
        {
            pending = await updates.CheckForUpdatesAsync();
            return pending is null
                ? new($"Version {Version} ist aktuell.")
                : new($"Version {pending.TargetFullRelease.Version} steht bereit.",
                    pending.TargetFullRelease.Version.ToString());
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            return new("Suche fehlgeschlagen: " + error.Message);
        }
    }

    /// <summary>Downloads the pending update and restarts into it.</summary>
    public async Task<UpdateState> ApplyAsync(IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        var updates = Manager();
        if (updates is null || pending is null) return new("Es steht keine Aktualisierung bereit.");
        try
        {
            await updates.DownloadUpdatesAsync(pending, percent => progress?.Report(percent),
                cancelToken: token);
            updates.ApplyUpdatesAndRestart(pending.TargetFullRelease);
            return new("Anwendung wird neu gestartet …");
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new("Aktualisierung fehlgeschlagen: " + error.Message);
        }
    }
}
