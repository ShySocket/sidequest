using NUnit.Framework;

public sealed class GpsSpeedWindowTests
{
    [Test]
    public void RequiresMultiSecondBaseline()
    {
        GpsSpeedWindow window = new GpsSpeedWindow();

        Assert.That(
            window.AddPosition(37d, -122d, 0d, out _, out _),
            Is.False);
        Assert.That(
            window.AddPosition(37.0001d, -122d, 1d, out _, out _),
            Is.False);
    }

    [Test]
    public void EstimatesSpeedAcrossSeveralFixes()
    {
        GpsSpeedWindow window = new GpsSpeedWindow();
        const double metersPerLatitudeDegree = 111195d;
        const float expectedSpeed = 15f;
        bool producedSpeed = false;
        float speed = 0f;

        for (int second = 0; second <= 6; second++)
        {
            double latitude = 37d
                + expectedSpeed * second / metersPerLatitudeDegree;
            producedSpeed = window.AddPosition(
                latitude,
                -122d,
                second,
                out speed,
                out _);
        }

        Assert.That(producedSpeed, Is.True);
        Assert.That(speed, Is.EqualTo(expectedSpeed).Within(0.15f));
    }

    [Test]
    public void RejectsDuplicateTimestamp()
    {
        GpsSpeedWindow window = new GpsSpeedWindow();
        window.AddPosition(37d, -122d, 1d, out _, out _);

        Assert.That(
            window.AddPosition(37.1d, -122d, 1d, out _, out _),
            Is.False);
        Assert.That(window.SampleCount, Is.EqualTo(1));
    }
}
