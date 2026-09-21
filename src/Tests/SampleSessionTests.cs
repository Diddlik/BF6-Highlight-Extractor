using Bf6Highlights.Ui;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class SampleSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bfhe-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildsSixSecondSamplesWithoutOverwritingDestinations()
    {
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "sample");
        Directory.CreateDirectory(first);
        var session = new SampleSession(20, first);

        Assert.True(session.Add(10, "own_kill"));
        Assert.True(session.Add(18, "own_death"));
        var requests = session.BuildRequests(" Player ", "development");

        Assert.Equal(2, requests.Count);
        Assert.Equal((7d, 13d, "sample-2"), (requests[0].Start, requests[0].End, Path.GetFileName(requests[0].Destination)));
        Assert.Equal((15d, 20d, "sample-3"), (requests[1].Start, requests[1].End, Path.GetFileName(requests[1].Destination)));
        Assert.All(requests, request => Assert.Equal("Player", request.Player));
    }

    [Fact]
    public void DuplicateCanBeUndoneAndAddedAgain()
    {
        var session = new SampleSession(10, Path.Combine(directory, "sample"));

        Assert.True(session.Add(5, "headshot"));
        Assert.False(session.Add(5.005, "headshot"));
        Assert.True(session.Undo());
        Assert.True(session.Add(5, "headshot"));
    }

    [Fact]
    public void AMarkerCanBeMovedAndRelabelledBeforeTheExport()
    {
        var session = new SampleSession(60, Path.Combine(directory, "sample"));
        Assert.True(session.Add(20, "own_kill"));
        var marker = session.Markers[0];

        Assert.True(session.Update(marker, 24.5, "own_kill"));
        Assert.Equal(24.5, session.Markers[0].Timestamp);

        Assert.True(session.Update(session.Markers[0], 24.5, "headshot"));
        Assert.Equal("headshot", session.Markers[0].Label);
        Assert.Single(session.Markers);
    }

    [Fact]
    public void AnEditIsRefusedWhenItWouldDuplicateAnotherMarker()
    {
        var session = new SampleSession(60, Path.Combine(directory, "sample"));
        session.Add(10, "own_kill");
        session.Add(30, "own_kill");

        Assert.False(session.Update(session.Markers[1], 10, "own_kill"));
        Assert.Equal(30, session.Markers[1].Timestamp);
        Assert.False(session.Update(session.Markers[1], 999, "kein_label"));
    }

    [Fact]
    public void DraftsSurviveAnInterruptedSessionAndAreDroppedAfterTheExport()
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "sample");
        var source = Path.Combine(directory, "match.mkv");
        File.WriteAllText(source, "video");

        var first = new SampleSession(60, destination, source);
        first.Add(12, "own_kill");
        first.Add(34, "headshot");

        var restored = SampleSession.LoadDrafts(destination, source);
        Assert.Equal(2, restored.Count);
        Assert.Equal([12d, 34d], restored.Select(marker => marker.Timestamp));
        Assert.Equal(["own_kill", "headshot"], restored.Select(marker => marker.Label));

        // Drafts belong to one recording; another source must not inherit them.
        Assert.Empty(SampleSession.LoadDrafts(destination, Path.Combine(directory, "other.mkv")));

        var second = new SampleSession(60, destination, source);
        second.Restore(restored);
        Assert.Equal(2, second.Markers.Count);

        SampleSession.DiscardDrafts(destination);
        Assert.Empty(SampleSession.LoadDrafts(destination, source));
    }

    [Fact]
    public void RemovingTheLastDraftClearsTheFile()
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "sample");
        var source = Path.Combine(directory, "match.mkv");
        File.WriteAllText(source, "video");

        var session = new SampleSession(60, destination, source);
        session.Add(5, "own_kill");
        Assert.Single(SampleSession.LoadDrafts(destination, source));

        session.Remove(session.Markers[0]);
        Assert.Empty(SampleSession.LoadDrafts(destination, source));
    }

    [Fact]
    public void EveryLabelHasItsOwnNameAndColour()
    {
        var colours = SampleExporter.Labels.Select(SampleMarker.LabelColour).ToArray();
        Assert.All(SampleExporter.Labels, label =>
            Assert.NotEqual(label, SampleMarker.LabelName(label)));
        Assert.All(colours, colour => Assert.StartsWith("#", colour));
        Assert.NotEqual(SampleMarker.LabelColour("own_kill"), SampleMarker.LabelColour("own_death"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
