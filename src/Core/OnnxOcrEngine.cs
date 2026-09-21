using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using RapidOcrNet;
using SkiaSharp;

namespace Bf6Highlights;

public sealed record PixelRegion(int X, int Y, int Width, int Height);
public sealed record OcrLine(string Text, double Confidence, PixelRegion BoundingBox);

public sealed class OnnxOcrEngine : IOcrEngine
{
    private readonly RapidOcr engine = new();

    public OnnxOcrEngine(string? modelDirectory = null)
    {
        var directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "v5");
        try
        {
            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(directory, "model-manifest.json")))
                ?? throw new IOException("Modellmanifest ist leer.");
            string[] names = [RapidOcr.DefaultDetModelPath, RapidOcr.DefaultClsModelPath,
                RapidOcr.DefaultRecModelPath, RapidOcr.DefaultKeysFilePath];
            foreach (var name in names)
            {
                using var file = File.OpenRead(Path.Combine(directory, name));
                if (!manifest.TryGetValue(name, out var expected) ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(file)), expected,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Modell/Zeichensatz beschädigt: {name}. Paketinhalte wiederherstellen.");
            }
            engine.InitModels(Path.Combine(directory, names[0]), Path.Combine(directory, names[1]),
                Path.Combine(directory, names[2]), Path.Combine(directory, names[3]), numThread: 1);
        }
        catch { engine.Dispose(); throw; }
    }

    // One engine per analysis worker. Native sessions are owned until Dispose.
    public async Task<IReadOnlyList<OcrLine>> ReadAsync(string image, PixelRegion? region = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var source = Cv2.ImDecode(await File.ReadAllBytesAsync(image, token), ImreadModes.Color);
        if (source.Empty()) throw new IOException("Bild konnte nicht gelesen werden: " + image);
        region ??= new(0, 0, source.Width, source.Height);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > source.Width || (long)region.Y + region.Height > source.Height)
            throw new ArgumentException("OCR-Bereich liegt außerhalb des Bildes.");
        using var crop = new Mat(source, new Rect(region.X, region.Y, region.Width, region.Height));
        return await ReadAsync(crop, region, token);
    }

    /// <summary>OCR on an already cropped frame; boxes are mapped back with the origin.</summary>
    public async Task<IReadOnlyList<OcrLine>> ReadAsync(Mat crop, PixelRegion origin,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (crop.Empty()) throw new ArgumentException("Leerer Bildausschnitt für OCR.");
        using var bitmap = SKBitmap.Decode(crop.ToBytes(".png"));
        var result = await engine.DetectAsync(bitmap, RapidOcrOptions.Default,
            progress: null, cancellationToken: token);
        return result.TextBlocks.Select(block =>
        {
            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            return new OcrLine(block.Text, block.CharScores is { Length: > 0 }
                ? block.CharScores.Average() : 0,
                new(origin.X + left, origin.Y + top, right - left, bottom - top));
        }).ToArray();
    }

    public void Dispose() => engine.Dispose();
}
