using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class OcrTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bf6-ocr-" + Guid.NewGuid().ToString("N"));
    public OcrTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task CpuRecognizesTextAndReturnsBoxesInOriginalCoordinates()
    {
        var file = Path.Combine(directory, "Bild ü.png");
        using (var image = new Mat(240, 900, MatType.CV_8UC3, Scalar.White))
        {
            Cv2.PutText(image, "BULLETWALTZ", new Point(130, 125), HersheyFonts.HersheySimplex,
                1.5, Scalar.Black, 3);
            await File.WriteAllBytesAsync(file, image.ToBytes(".png"));
        }
        using var engine = new OnnxOcrEngine();
        var lines = await engine.ReadAsync(file, new PixelRegion(100, 60, 600, 120));
        var line = Assert.Single(lines);
        Assert.Equal("BULLETWALTZ", line.Text.ToUpperInvariant());
        Assert.InRange(line.Confidence, 0.7f, 1);
        Assert.InRange(line.BoundingBox.X, 120, 150);
        Assert.InRange(line.BoundingBox.Y, 80, 110);
        Assert.True(line.BoundingBox.Width > 200);
    }

    [Fact]
    public async Task EmptyImageReturnsNoInventedTextAndInvalidRegionFails()
    {
        var file = Path.Combine(directory, "empty.png");
        using (var image = new Mat(100, 300, MatType.CV_8UC3, Scalar.White)) image.SaveImage(file);
        using var engine = new OnnxOcrEngine();
        Assert.Empty(await engine.ReadAsync(file));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ReadAsync(file, new(250, 0, 100, 50)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ReadAsync(file, token: cancel.Token));
    }

    [Fact]
    public void MissingModelFilesFailBeforeInference()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "models/v5/model-manifest.json"),
            Path.Combine(directory, "model-manifest.json"));
        Assert.Throws<FileNotFoundException>(() => new OnnxOcrEngine(directory));
    }

    [Fact]
    public void CorruptModelFailsChecksumValidation()
    {
        var models = Path.Combine(AppContext.BaseDirectory, "models/v5");
        foreach (var file in Directory.GetFiles(models)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(directory, RapidOcrNet.RapidOcr.DefaultDetModelPath), "corrupt");
        Assert.Contains("beschädigt", Assert.Throws<IOException>(() => new OnnxOcrEngine(directory)).Message);
    }
}
