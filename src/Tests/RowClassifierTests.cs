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

    // The model has only seen the names it was trained for; anyone else keeps the OCR alone.
    [Fact]
    public void OtherPlayersDoNotGetTheModel()
    {
        Assert.Null(RowClassifier.Load(["SomeoneElse"]));
        using var trained = RowClassifier.Load(["  bulletwaltz ", "SomeoneElse"]);
        Assert.NotNull(trained);
    }
}
