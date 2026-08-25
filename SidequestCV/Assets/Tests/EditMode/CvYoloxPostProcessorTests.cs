using NUnit.Framework;

/// <summary>
/// Replays the recorded YOLOX-tiny output for the reference street photo and
/// checks that the C# decode + NMS reproduces the Python onnxruntime
/// reference implementation exactly.
/// </summary>
public sealed class CvYoloxPostProcessorTests
{
    [Test]
    public void DecodeAndNmsMatchesPythonReference()
    {
        CvFixtures.LoadYoloxRows(out int[] indices, out float[] values);
        CvFixtures.ExpectedDetections expected = CvFixtures.LoadYoloxExpected();

        YoloxPostProcessor processor = new YoloxPostProcessor(
            expected.InputSize,
            expected.ScoreThreshold,
            expected.IouThreshold);
        var kept = processor.DecodeSparse(indices, values);

        Assert.That(kept.Count, Is.EqualTo(expected.Detections.Length));
        for (int i = 0; i < kept.Count; i++)
        {
            Detection actual = LetterboxMath.ToSource(kept[i], expected.Scale);
            Detection reference = expected.Detections[i];
            Assert.That(actual.ClassId, Is.EqualTo(reference.ClassId));
            Assert.That(actual.Score, Is.EqualTo(reference.Score).Within(0.001f));
            Assert.That(actual.CenterX, Is.EqualTo(reference.CenterX).Within(0.5f));
            Assert.That(actual.CenterY, Is.EqualTo(reference.CenterY).Within(0.5f));
            Assert.That(actual.Width, Is.EqualTo(reference.Width).Within(1f));
            Assert.That(actual.Height, Is.EqualTo(reference.Height).Within(1f));
        }
    }

    [Test]
    public void ReferenceSceneContainsTheBusAndPedestrians()
    {
        CvFixtures.LoadYoloxRows(out int[] indices, out float[] values);
        CvFixtures.ExpectedDetections expected = CvFixtures.LoadYoloxExpected();
        YoloxPostProcessor processor = new YoloxPostProcessor(
            expected.InputSize, expected.ScoreThreshold, expected.IouThreshold);
        var kept = processor.DecodeSparse(indices, values);

        int buses = 0;
        int persons = 0;
        foreach (Detection d in kept)
        {
            if (d.ClassId == CvClassCatalog.CocoBus)
            {
                buses++;
            }

            if (d.ClassId == CvClassCatalog.CocoPerson)
            {
                persons++;
            }
        }

        Assert.That(buses, Is.EqualTo(1));
        Assert.That(persons, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void NmsSuppressesOverlappingSameClassBoxes()
    {
        var candidates = new System.Collections.Generic.List<Detection>
        {
            new Detection(2, 0.9f, 100f, 100f, 50f, 50f),
            new Detection(2, 0.8f, 104f, 102f, 50f, 50f),
            new Detection(2, 0.7f, 300f, 100f, 50f, 50f),
            new Detection(0, 0.6f, 101f, 101f, 50f, 50f)
        };

        var kept = YoloxPostProcessor.Nms(candidates, 0.45f);

        Assert.That(kept.Count, Is.EqualTo(3));
        Assert.That(kept[0].Score, Is.EqualTo(0.9f).Within(0.0001f));
    }

    [Test]
    public void LetterboxScaleUsesTheTighterAxis()
    {
        Assert.That(LetterboxMath.ComputeScale(810, 1080, 416), Is.EqualTo(416f / 1080f).Within(1e-5f));
        Assert.That(LetterboxMath.ComputeScale(1920, 1080, 416), Is.EqualTo(416f / 1920f).Within(1e-5f));
    }
}
