using NUnit.Framework;
using System.Collections.Generic;

public sealed class CvMappingAndCollisionTests
{
    [Test]
    public void ReferenceDetectionsMapToLanesAndKinds()
    {
        CvFixtures.LoadYoloxRows(out int[] indices, out float[] values);
        CvFixtures.ExpectedDetections expected = CvFixtures.LoadYoloxExpected();
        YoloxPostProcessor processor = new YoloxPostProcessor(
            expected.InputSize, expected.ScoreThreshold, expected.IouThreshold);
        var kept = processor.DecodeSparse(indices, values);
        var source = new List<Detection>();
        foreach (Detection d in kept)
        {
            source.Add(LetterboxMath.ToSource(d, expected.Scale));
        }

        var events = new DetectionEventMapper().Map(
            source, expected.SourceWidth, expected.SourceHeight);

        Assert.That(events.Count, Is.GreaterThanOrEqualTo(3));
        bool sawVehicle = false;
        bool sawPerson = false;
        foreach (DetectionEvent e in events)
        {
            sawVehicle |= e.Kind == ObstacleKind.Vehicle;
            sawPerson |= e.Kind == ObstacleKind.Person;
            Assert.That(e.Lane, Is.InRange(0, 2));
        }

        Assert.That(sawVehicle, Is.True);
        Assert.That(sawPerson, Is.True);
    }

    [Test]
    public void SkylineObjectsAreIgnored()
    {
        var detections = new List<Detection>
        {
            // A traffic light high in the frame: bottom above the cutoff.
            new Detection(CvClassCatalog.CocoTrafficLight, 0.9f, 200f, 80f, 40f, 80f),
            // A tiny distant car.
            new Detection(CvClassCatalog.CocoCar, 0.9f, 240f, 250f, 12f, 8f)
        };

        var events = new DetectionEventMapper().Map(detections, 480, 288);

        Assert.That(events.Count, Is.Zero);
    }

    [Test]
    public void LaneAssignmentFollowsHorizontalPosition()
    {
        var detections = new List<Detection>
        {
            new Detection(CvClassCatalog.CocoCar, 0.9f, 60f, 240f, 80f, 60f),
            new Detection(CvClassCatalog.CocoCar, 0.9f, 240f, 240f, 80f, 60f),
            new Detection(CvClassCatalog.CocoCar, 0.9f, 430f, 240f, 80f, 60f)
        };

        var events = new DetectionEventMapper().Map(detections, 480, 288);

        Assert.That(events.Count, Is.EqualTo(3));
        Assert.That(events[0].Lane, Is.EqualTo(0));
        Assert.That(events[1].Lane, Is.EqualTo(1));
        Assert.That(events[2].Lane, Is.EqualTo(2));
    }

    [Test]
    public void JumpClearsLowObstaclesOnly()
    {
        // Grounded, same lane, overlapping: hit.
        Assert.That(CollisionJudge.IsHit(0.2f, 0.4f, 1, false, 1, 0f), Is.True);
        // Airborne above a jumpable: clear.
        Assert.That(CollisionJudge.IsHit(0.2f, 0.4f, 1, true, 1, 0.9f), Is.False);
        // Airborne above a vehicle: still a hit.
        Assert.That(CollisionJudge.IsHit(0.2f, 1.7f, 1, false, 1, 0.9f), Is.True);
        // Different lane: never a hit.
        Assert.That(CollisionJudge.IsHit(0.2f, 1.7f, 0, false, 1, 0f), Is.False);
        // Far away: no hit.
        Assert.That(CollisionJudge.IsHit(9f, 1.7f, 1, false, 1, 0f), Is.False);
    }
}
