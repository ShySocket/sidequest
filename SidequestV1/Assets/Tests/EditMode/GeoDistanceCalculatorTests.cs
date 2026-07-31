using NUnit.Framework;

public sealed class GeoDistanceCalculatorTests
{
    [Test]
    public void SamePointReturnsZero()
    {
        Assert.That(GeoDistanceCalculator.DistanceMeters(37d, -122d, 37d, -122d), Is.EqualTo(0d).Within(0.001d));
    }

    [Test]
    public void KnownCoordinatePairIsPlausible()
    {
        double distance = GeoDistanceCalculator.DistanceMeters(37.7749d, -122.4194d, 34.0522d, -118.2437d);
        Assert.That(distance, Is.InRange(550000d, 570000d));
    }

    [Test]
    public void ShortMovementIsSmallAndPositive()
    {
        double distance = GeoDistanceCalculator.DistanceMeters(37d, -122d, 37.00001d, -122d);
        Assert.That(distance, Is.GreaterThan(0.5d).And.LessThan(2d));
    }

    [Test]
    public void DistanceIsSymmetric()
    {
        double forward = GeoDistanceCalculator.DistanceMeters(1d, 2d, 3d, 4d);
        double reverse = GeoDistanceCalculator.DistanceMeters(3d, 4d, 1d, 2d);
        Assert.That(forward, Is.EqualTo(reverse).Within(0.001d));
    }

    [Test]
    public void BearingDueEastIsNinetyDegrees()
    {
        Assert.That(
            GeoDistanceCalculator.InitialBearingDegrees(0d, 0d, 0d, 1d),
            Is.EqualTo(90f).Within(0.01f));
    }
}
