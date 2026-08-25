using NUnit.Framework;

public sealed class VehicleSpeedFilterTests
{
    [Test]
    public void NoValidSpeedReturnsZero()
    {
        VehicleSpeedFilter filter = new VehicleSpeedFilter(0.25f, 0.35f);
        Assert.That(filter.Update(10f, 1f, false), Is.Zero);
    }

    [Test]
    public void AccelerationApproachesTarget()
    {
        VehicleSpeedFilter filter = new VehicleSpeedFilter(0.25f, 0.35f);
        float first = filter.Update(10f, 0.1f, true);
        float second = filter.Update(10f, 0.1f, true);
        Assert.That(first, Is.GreaterThan(0f).And.LessThan(10f));
        Assert.That(second, Is.GreaterThan(first).And.LessThan(10f));
    }

    [Test]
    public void DecelerationApproachesTarget()
    {
        VehicleSpeedFilter filter = new VehicleSpeedFilter(0f, 0.35f);
        filter.Update(10f, 0.1f, true);
        float result = filter.Update(0f, 0.1f, true);
        Assert.That(result, Is.GreaterThan(0f).And.LessThan(10f));
    }

    [Test]
    public void NegativeTargetsAreTreatedAsZero()
    {
        VehicleSpeedFilter filter = new VehicleSpeedFilter(0f, 0f);
        Assert.That(filter.Update(-10f, 0.1f, true), Is.Zero);
    }

    [Test]
    public void DifferentSmoothingValuesProduceDifferentResponses()
    {
        VehicleSpeedFilter fast = new VehicleSpeedFilter(0.1f, 0.1f);
        VehicleSpeedFilter slow = new VehicleSpeedFilter(1f, 1f);
        Assert.That(fast.Update(10f, 0.1f, true), Is.GreaterThan(slow.Update(10f, 0.1f, true)));
    }

    [Test]
    public void ZeroSmoothingTimeIsFiniteAndImmediate()
    {
        VehicleSpeedFilter filter = new VehicleSpeedFilter(0f, 0f);
        Assert.That(filter.Update(10f, 0.1f, true), Is.EqualTo(10f));
    }
}
