using System.Text.Json;
using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

/// <summary>Change detection must agree with Python; crops are built from the same formulas.</summary>
public sealed class ChangeDetectorTests
{
    public static TheoryData<string, JsonElement> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "reference", "change-detection-golden.json")));
        var cases = new TheoryData<string, JsonElement>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            cases.Add(item.GetProperty("id").GetString()!, item.Clone());
        return cases;
    }

    [Theory, MemberData(nameof(Cases))]
    public void MatchesPython(string id, JsonElement data)
    {
        Assert.Equal(id, data.GetProperty("id").GetString());
        using var detector = new ChangeDetector(data.GetProperty("threshold").GetDouble());
        foreach (var step in data.GetProperty("steps").EnumerateArray())
        {
            using var crop = Crop(step.GetProperty("crop"));
            var (changed, ratio) = detector.Changed(crop);
            Assert.Equal(step.GetProperty("changed").GetBoolean(), changed);
            Assert.Equal(step.GetProperty("ratio").GetDouble(), ratio, 6);
        }
    }

    [Theory]
    [InlineData(60.0, 3, 20)]
    [InlineData(59.94, 3, 20)]
    [InlineData(120.0, 3, 40)]
    [InlineData(30.0, 15, 2)]
    [InlineData(5.0, 15, 1)]
    public void SampleStepMatchesPython(double fps, int samplesPerSecond, int expected) =>
        Assert.Equal(expected, ChangeDetector.SampleStep(fps, samplesPerSecond));

    [Fact]
    public void InvalidInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ChangeDetector(1.5));
        Assert.Throws<ArgumentException>(() => ChangeDetector.SampleStep(0, 3));
        using var detector = new ChangeDetector(0.08);
        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() => detector.Changed(empty));
    }

    private static Mat Crop(JsonElement spec)
    {
        var width = spec.GetProperty("width").GetInt32();
        var height = spec.GetProperty("height").GetInt32();
        var pattern = spec.GetProperty("pattern").GetString();
        var channels = spec.TryGetProperty("channels", out var count) ? count.GetInt32() : 1;
        var gray = new Mat(height, width, MatType.CV_8UC1);
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                gray.Set(row, column, (byte)(pattern switch
                {
                    "constant" => spec.GetProperty("value").GetInt32(),
                    "gradient" => column * spec.GetProperty("a").GetInt32()
                        + row * spec.GetProperty("b").GetInt32() + spec.GetProperty("c").GetInt32(),
                    "blocks" => (column / spec.GetProperty("a").GetInt32()
                        + row / spec.GetProperty("b").GetInt32()) % 2 * spec.GetProperty("value").GetInt32(),
                    _ => throw new ArgumentException("Unbekanntes Muster: " + pattern),
                } % 256));
        if (channels == 1) return gray;
        var colour = new Mat();
        Cv2.Merge([gray, gray, gray], colour);
        gray.Dispose();
        return colour;
    }
}
