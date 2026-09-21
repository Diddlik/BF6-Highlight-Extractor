using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bf6Highlights;

/// <summary>
/// Keeps the personal profiles in the user folder. A run never overwrites an earlier profile;
/// it writes a new numbered version next to it, so a comparison stays possible.
/// </summary>
public static class PersonalProfileStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Directory => Path.Combine(
        Path.GetDirectoryName(ConfigurationFile.DefaultUserConfigPath)!, "profiles");

    /// <summary>
    /// Which profile the analysis uses. It lives beside the profiles, not in the configuration:
    /// activating must not rewrite the user's own detection settings.
    /// </summary>
    private static string ActiveFile => Path.Combine(Directory, "active.txt");

    public static string? ActivePath
    {
        get
        {
            try
            {
                if (!File.Exists(ActiveFile)) return null;
                var path = File.ReadAllText(ActiveFile).Trim();
                return path.Length > 0 && File.Exists(path) ? path : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Activates a profile, or switches back to the standard detection with null.</summary>
    public static void Activate(string? path)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (path is null) { File.Delete(ActiveFile); return; }
        if (!File.Exists(path)) throw new ConfigurationException("Profil nicht gefunden: " + path);
        var temporary = ActiveFile + ".tmp";
        File.WriteAllText(temporary, Path.GetFullPath(path));
        File.Move(temporary, ActiveFile, overwrite: true);
    }

    /// <summary>Writes the profile as a new version and returns its path.</summary>
    public static string Save(PersonalProfile profile, string? directory = null)
    {
        var target = directory ?? Directory;
        System.IO.Directory.CreateDirectory(target);
        var stem = Sanitize(profile.Name);
        var path = Path.Combine(target, stem + ".json");
        for (var version = 2; File.Exists(path); version++)
            path = Path.Combine(target, $"{stem}-{version}.json");

        var temporary = Path.Combine(target, $".bf6-profile-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Format));
            // Never replaces an existing file; a parallel run keeps its own version.
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
        return path;
    }

    public static PersonalProfile Load(string path)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<PersonalProfile>(File.ReadAllText(path), Format)
                ?? throw new ConfigurationException("Profildatei ist leer: " + path);
            if (profile.Format > PersonalProfile.FormatVersion)
                throw new ConfigurationException(
                    $"Das Profil stammt aus einer neueren Fassung (Format {profile.Format}).");
            if (profile.SourceWidth <= 0 || profile.SourceHeight <= 0
                || !double.IsFinite(profile.MinimumConfidence)
                || profile.MinimumConfidence is < 0 or > 1
                || !double.IsFinite(profile.NameThreshold)
                || profile.NameThreshold is < 0 or > 100)
                throw new ConfigurationException("Das Profil enthält ungültige Werte.");
            return profile;
        }
        catch (Exception error) when (error is JsonException or IOException
            or ArgumentException or NotSupportedException)
        {
            throw new ConfigurationException("Profil konnte nicht gelesen werden: " + error.Message, error);
        }
    }

    /// <summary>All readable profiles, newest first; a broken file is skipped, not fatal.</summary>
    public static IReadOnlyList<(string Path, PersonalProfile Profile)> List(string? directory = null)
    {
        var target = directory ?? Directory;
        if (!System.IO.Directory.Exists(target)) return [];
        var profiles = new List<(string, PersonalProfile)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(target, "*.json"))
        {
            try { profiles.Add((file, Load(file))); }
            catch (ConfigurationException) { }
        }
        return [.. profiles.OrderByDescending(entry => entry.Item2.CreatedAt)];
    }

    /// <summary>
    /// The configuration to analyse with. A missing, broken or unsuitable profile never blocks
    /// the analysis; the reason is reported and the standard detection is used.
    /// </summary>
    public static (Configuration Configuration, string Note) Apply(Configuration configuration,
        string? profilePath, int videoWidth, int videoHeight)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
            return (configuration, "Standarderkennung");
        PersonalProfile profile;
        try { profile = Load(profilePath); }
        catch (ConfigurationException error)
        {
            return (configuration, "Standarderkennung, weil das Profil nicht lesbar ist: " + error.Message);
        }
        if (!profile.Fits(videoWidth, videoHeight))
            return (configuration, $"Standarderkennung, weil das Profil für "
                + $"{profile.SourceWidth}×{profile.SourceHeight} gilt, die Aufnahme aber "
                + $"{videoWidth}×{videoHeight} ist.");
        return (profile.ApplyTo(configuration), $"Persönliches Profil „{profile.Name}“");
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string([.. name.Trim()
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)]);
        return cleaned.Length == 0 ? "profil" : cleaned;
    }
}
