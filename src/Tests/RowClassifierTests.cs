using System.Text.Json;
using Bf6Highlights;
using OpenCvSharp;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class RowClassifierTests
{
    // train.py stores a reference row with the probabilities PyTorch gave it. Getting the same numbers
    // from ONNX Runtime proves that the app prepares a row exactly like the training did.
    [Fact]
    public void TheShippedModelReproducesTheTrainingOnItsReferenceRow()
    {
        using var classifier = RowClassifier.Load(["BulletWaltz"]);
        Assert.NotNull(classifier);
        var models = Path.Combine(AppContext.BaseDirectory, "models");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(models, "row-classifier.json")));
        var classes = document.RootElement.GetProperty("classes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var expected = document.RootElement.GetProperty("reference").EnumerateArray().Select(p => p.GetSingle()).ToArray();
        using var row = Cv2.ImRead(Path.Combine(models, "row-classifier-reference.png"));

        var actual = classifier.Classify(row);

        for (var index = 0; index < classes.Length; index++)
            Assert.Equal(expected[index], actual[classes[index]], 3);
    }

    // Measured on rows of other players: pings were recognised, kills were taken for deaths, because
    // the model only knows its own training names. So everyone else gets the ping veto alone.
    [Fact]
    public void OtherPlayersOnlyGetThePingVeto()
    {
        using var other = RowClassifier.Load(["SomeoneElse"]);
        using var trained = RowClassifier.Load(["  bulletwaltz ", "SomeoneElse"]);
        Assert.False(other!.Trained);
        Assert.True(trained!.Trained);

        static Dictionary<string, float> Sure(string label) => new()
            { ["kill"] = 0.02f, ["ping"] = 0.02f, ["death"] = 0.02f, ["other"] = 0.02f, [label] = 0.94f };
        Assert.True(RowClassifier.Vetoes(Sure("ping"), trained: false));
        Assert.False(RowClassifier.Vetoes(Sure("death"), trained: false));
        Assert.False(RowClassifier.Vetoes(Sure("other"), trained: false));
        Assert.True(RowClassifier.Vetoes(Sure("death"), trained: true));
        Assert.False(RowClassifier.Vetoes(Sure("kill"), trained: true));
        Assert.False(RowClassifier.Vetoes(new Dictionary<string, float> { ["kill"] = 0.15f, ["ping"] = 0.85f },
            trained: true));
    }
}
