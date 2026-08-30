using System.Text;
using NUnit.Framework;
using UnityEngine;

public sealed class SpeedTraceTests
{
    [Test]
    public void FormattedLinesParseBackToTheSameSamples()
    {
        var builder = new StringBuilder();
        string gps = SpeedTrace.FormatGpsLine(
            builder, 1.25f, 12.5f, 5f, 187.5f, 37.7749d, -122.4194d);
        string motion = SpeedTrace.FormatMotionLine(
            builder,
            1.27f,
            new Vector3(0.1f, -0.2f, 0.3f),
            new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));

        SpeedTrace trace = SpeedTrace.Parse(
            SpeedTrace.HeaderLine + "\n" + gps + "\n" + motion + "\n");

        Assert.That(trace, Is.Not.Null);
        Assert.That(trace.Entries.Count, Is.EqualTo(2));

        SpeedTrace.Entry first = trace.Entries[0];
        Assert.That(first.Kind, Is.EqualTo(SpeedTrace.SampleKind.Gps));
        Assert.That(first.Time, Is.EqualTo(1.25f));
        Assert.That(first.Speed, Is.EqualTo(12.5f));
        Assert.That(first.Accuracy, Is.EqualTo(5f));
        Assert.That(first.Course, Is.EqualTo(187.5f));

        SpeedTrace.Entry second = trace.Entries[1];
        Assert.That(second.Kind, Is.EqualTo(SpeedTrace.SampleKind.Motion));
        Assert.That(second.Time, Is.EqualTo(1.27f));
        Assert.That(second.Acceleration.x, Is.EqualTo(0.1f));
        Assert.That(second.Acceleration.y, Is.EqualTo(-0.2f));
        Assert.That(second.Attitude.w, Is.EqualTo(0.9f));
    }

    [Test]
    public void MissingCourseSurvivesTheRoundTrip()
    {
        var builder = new StringBuilder();
        string gps = SpeedTrace.FormatGpsLine(
            builder, 0f, 3f, 10f, float.NaN, 0d, 0d);

        SpeedTrace trace = SpeedTrace.Parse(gps);

        Assert.That(trace, Is.Not.Null);
        Assert.That(float.IsNaN(trace.Entries[0].Course), Is.True);
    }

    [Test]
    public void CommentsBlanksAndTruncatedLinesAreSkipped()
    {
        string text = SpeedTrace.HeaderLine + "\n"
            + "\n"
            + "g,1,5,10,0,0,0\n"
            + "g,2,not-a-number,10,0,0,0\n"
            + "m,3,0.1\n"
            + "g,4,6";

        SpeedTrace trace = SpeedTrace.Parse(text);

        Assert.That(trace, Is.Not.Null);
        Assert.That(trace.Entries.Count, Is.EqualTo(1));
        Assert.That(trace.Entries[0].Speed, Is.EqualTo(5f));
    }

    [Test]
    public void EmptyTextParsesToNull()
    {
        Assert.That(SpeedTrace.Parse(""), Is.Null);
        Assert.That(SpeedTrace.Parse(SpeedTrace.HeaderLine), Is.Null);
        Assert.That(SpeedTrace.Parse(null), Is.Null);
    }

    [Test]
    public void DurationIsTheLastSampleTime()
    {
        SpeedTrace trace = SpeedTrace.Parse(
            "g,1,5,10,0,0,0\ng,7.5,6,10,0,0,0");

        Assert.That(trace.Duration, Is.EqualTo(7.5f));
    }
}
