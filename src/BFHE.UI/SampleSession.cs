using System.Collections.ObjectModel;
using System.Text.Json;

namespace Bf6Highlights.Ui;

public sealed record SampleMarker(double Timestamp, string Label)
{
    public string Display => $"{Format(Timestamp)}  {LabelName(Label)}";

    public static string LabelName(string label) => label switch
    {
        "own_kill" => "Eigener Kill",
        "own_death" => "Eigener Tod",
        "headshot" => "Headshot",
        "foreign_kill" => "Fremder Kill",
        "no_event" => "Kein Ereignis",
        "no_event_marker" => "Markierung ohne Kill",
        "multiple_kills" => "Mehrere Kills",
        _ => label,
    };

    /// <summary>Colour of the marker on the timeline; the list stays the readable form.</summary>
    public static string LabelColour(string label) => label switch
    {
        "own_kill" => "#E07B24",
        "headshot" => "#F0C05C",
        "multiple_kills" => "#D2582F",
        "own_death" => "#E4695A",
        "foreign_kill" => "#5AAFD4",
        _ => "#9AA0A6",
    };

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");
}

/// <summary>
/// The markers of one labelling session. They exist only here until the export; nothing on
/// disk changes before that. Drafts are kept next to the destination so a crash does not
/// cost the labelling work.
/// </summary>
public sealed class SampleSession(double duration, string firstDestination, string source = "")
{
    private const string DraftFile = ".bf6-sample-drafts.json";

    public ObservableCollection<SampleMarker> Markers { get; } = [];

    public bool Add(double timestamp, string label)
    {
        if (!Valid(timestamp, label)) return false;
        timestamp = Clamp(timestamp);
        if (Occupied(timestamp, label, null)) return false;
        Markers.Add(new(timestamp, label));
        Save();
        return true;
    }

    /// <summary>Moves a draft or gives it another label; exported samples are never touched.</summary>
    public bool Update(SampleMarker marker, double timestamp, string label)
    {
        var index = Markers.IndexOf(marker);
        if (index < 0 || !Valid(timestamp, label)) return false;
        timestamp = Clamp(timestamp);
        if (Occupied(timestamp, label, marker)) return false;
        Markers[index] = new(timestamp, label);
        Save();
        return true;
    }

    public bool Undo()
    {
        if (Markers.Count == 0) return false;
        Markers.RemoveAt(Markers.Count - 1);
        Save();
        return true;
    }

    public void Remove(SampleMarker marker)
    {
        if (Markers.Remove(marker)) Save();
    }

    public IReadOnlyList<SampleRequest> BuildRequests(string player, string split)
    {
        var requests = new List<SampleRequest>(Markers.Count);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in Markers)
        {
            var start = Math.Max(0, marker.Timestamp - 3);
            var end = Math.Min(duration, marker.Timestamp + 3);
            requests.Add(new(start, end, marker.Timestamp, marker.Label,
                NextDestination(reserved), player.Trim(), split));
        }
        return requests;
    }

    private bool Valid(double timestamp, string label) =>
        double.IsFinite(timestamp) && duration > 0 && SampleExporter.Labels.Contains(label);

    private double Clamp(double timestamp) => Math.Clamp(timestamp,
        Math.Min(0.001, duration / 2), Math.Max(0.001, duration - 0.001));

    private bool Occupied(double timestamp, string label, SampleMarker? ignore) =>
        Markers.Any(marker => !ReferenceEquals(marker, ignore) && marker.Label == label
            && Math.Abs(marker.Timestamp - timestamp) < 0.01);

    private string NextDestination(HashSet<string> reserved)
    {
        var path = firstDestination;
        for (var suffix = 2; Directory.Exists(path) || File.Exists(path) || !reserved.Add(path); suffix++)
            path = firstDestination + "-" + suffix;
        return path;
    }

    // --- drafts on disk -------------------------------------------------------------

    private static string DraftPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, DraftFile);

    /// <summary>Markers of an interrupted session for this recording, newest state first.</summary>
    public static IReadOnlyList<SampleMarker> LoadDrafts(string destination, string source)
    {
        var path = DraftPath(destination);
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("source").GetString() != Path.GetFullPath(source)) return [];
            return [.. root.GetProperty("markers").EnumerateArray()
                .Select(item => new SampleMarker(item.GetProperty("timestamp").GetDouble(),
                    item.GetProperty("label").GetString() ?? ""))
                .Where(marker => SampleExporter.Labels.Contains(marker.Label))];
        }
        catch (Exception error) when (error is JsonException or IOException
            or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Removes the draft file once its markers have been exported.</summary>
    public static void DiscardDrafts(string destination)
    {
        try { File.Delete(DraftPath(destination)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Restore(IEnumerable<SampleMarker> markers)
    {
        foreach (var marker in markers) Add(marker.Timestamp, marker.Label);
    }

    private void Save()
    {
        if (source.Length == 0) return;
        var path = DraftPath(firstDestination);
        try
        {
            if (Markers.Count == 0) { File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                source = Path.GetFullPath(source),
                saved_at = DateTimeOffset.Now.ToString("O"),
                markers = Markers.Select(marker => new { timestamp = marker.Timestamp, label = marker.Label }),
            }, new JsonSerializerOptions { WriteIndented = true });
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, payload);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            // Losing the draft file must never interrupt the labelling itself.
        }
    }
}
