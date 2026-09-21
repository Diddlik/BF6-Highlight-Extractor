using System.Text.Json;
using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class ReferenceBatchTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bf6-reference-" + Guid.NewGuid().ToString("N"));

    public ReferenceBatchTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task IdenticalSameSourceFramesAreDeduplicated()
    {
        var image = CreateImage();
        CreateSample("sample-1", 1.0, image);
        CreateSample("sample-2", 1.2, image);
        var report = Path.Combine(directory, "report.json");

        var interrupted = await ReferenceDetection.RunAsync(
            directory, Settings(), new(0, 0, 900, 240), report);

        Assert.False(interrupted);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var cases = document.RootElement.GetProperty("cases");
        Assert.Equal(2, cases.GetArrayLength());
        Assert.Equal(1, cases[0].GetProperty("candidates").GetArrayLength());
        Assert.Equal(1, cases[1].GetProperty("duplicates").GetInt32());
        Assert.Empty(cases[1].GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task ExistingReportIsProtected()
    {
        var report = Path.Combine(directory, "report.json");
        await File.WriteAllTextAsync(report, "existing");

        await Assert.ThrowsAsync<IOException>(() => ReferenceDetection.RunAsync(
            directory, Settings(), new(0, 0, 900, 240), report));
        Assert.Equal("existing", await File.ReadAllTextAsync(report));
    }

    [Fact]
    public async Task PreCancelledBatchWritesInterruptedEmptyReport()
    {
        CreateSample("sample-1", 1.0, CreateImage());
        var report = Path.Combine(directory, "report.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = await ReferenceDetection.RunAsync(
            directory, Settings(), new(0, 0, 900, 240), report, cancellation.Token);

        Assert.True(interrupted);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.True(document.RootElement.GetProperty("interrupted").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("cases").EnumerateArray());
    }

    [Fact]
    public async Task MalformedManifestSurfacesIOException()
    {
        var sample = Path.Combine(directory, "broken");
        Directory.CreateDirectory(sample);
        await File.WriteAllTextAsync(Path.Combine(sample, "sample.json"), "{}");

        await Assert.ThrowsAsync<IOException>(() => ReferenceDetection.RunAsync(
            directory, Settings(), new(0, 0, 900, 240), Path.Combine(directory, "report.json")));
    }

    private static DetectionSettings Settings() => new() { PlayerNames = ["BulletWaltz"] };

    private static byte[] CreateImage()
    {
        using var image = new Mat(240, 900, MatType.CV_8UC3, Scalar.White);
        Cv2.PutText(image, "BULLETWALTZ", new(130, 125), HersheyFonts.HersheySimplex, 1.5, Scalar.Black, 3);
        Cv2.PutText(image, "Enemy", new(560, 125), HersheyFonts.HersheySimplex, 1.5, Scalar.Black, 3);
        return image.ToBytes(".png");
    }

    private void CreateSample(string name, double timestamp, byte[] image)
    {
        var sample = Path.Combine(directory, name);
        Directory.CreateDirectory(sample);
        File.WriteAllBytes(Path.Combine(sample, "frame.png"), image);
        var manifest = new
        {
            schema_version = 1,
            timestamp_hint_seconds = timestamp,
            source_fps = 30.0,
            source_sha256 = new string('a', 64),
            source = "recording.mp4",
            label_hint = "kill",
            status = "review",
        };
        File.WriteAllText(Path.Combine(sample, "sample.json"), JsonSerializer.Serialize(manifest));
    }
}
