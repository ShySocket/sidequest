using NUnit.Framework;

public sealed class PlaybackRateShaperTests
{
    private static PlaybackRateShaper CreateShaper()
    {
        return new PlaybackRateShaper(
            riseSmoothingTime: 0.5f,
            fallSmoothingTime: 0.3f,
            actionFloorRate: 0.5f,
            maximumRate: 4f);
    }

    private static float Run(
        PlaybackRateShaper shaper,
        float target,
        bool action,
        float seconds)
    {
        const float step = 1f / 60f;
        float rate = shaper.CurrentRate;
        for (float elapsed = 0f; elapsed < seconds; elapsed += step)
        {
            rate = shaper.Update(target, action, step);
        }

        return rate;
    }

    [Test]
    public void BrakeToZeroFreezesWithinASecond()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        float rate = Run(shaper, 0f, false, 1f);

        Assert.That(rate, Is.EqualTo(0f));
    }

    [Test]
    public void SlowingEasesInsteadOfSnapping()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        float rate = shaper.Update(0f, false, 1f / 60f);

        // One frame into a stop the rate has visibly moved but is nowhere
        // near zero - that in-between is the "player slowing down" the
        // display owes the rider.
        Assert.That(rate, Is.GreaterThan(0.5f).And.LessThan(1f));
    }

    [Test]
    public void SpeedingUpIsSlowerThanSlowingDown()
    {
        PlaybackRateShaper rising = CreateShaper();
        float risen = Run(rising, 1f, false, 0.3f);

        PlaybackRateShaper falling = CreateShaper();
        Run(falling, 1f, false, 5f);
        float fallen = Run(falling, 0f, false, 0.3f);

        // After the same elapsed time, the rise has covered less of its gap
        // than the fall has of its own.
        Assert.That(risen, Is.LessThan(1f - fallen));
    }

    [Test]
    public void ActionInProgressHoldsTheFloorThroughAStop()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        float rate = Run(shaper, 0f, true, 5f);

        Assert.That(rate, Is.EqualTo(0.5f).Within(0.01f));
    }

    [Test]
    public void FloorReleasesOnceTheActionLands()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        Run(shaper, 0f, true, 5f);
        float rate = Run(shaper, 0f, false, 1f);

        Assert.That(rate, Is.EqualTo(0f));
    }

    [Test]
    public void TargetsAreCappedAtTheMaximumRate()
    {
        PlaybackRateShaper shaper = CreateShaper();
        float rate = Run(shaper, 100f, false, 30f);

        Assert.That(rate, Is.LessThanOrEqualTo(4f));
    }

    [Test]
    public void ResetReturnsToStandstill()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        shaper.Reset();

        Assert.That(shaper.CurrentRate, Is.EqualTo(0f));
    }
}
