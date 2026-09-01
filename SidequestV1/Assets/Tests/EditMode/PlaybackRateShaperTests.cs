using NUnit.Framework;

public sealed class PlaybackRateShaperTests
{
    private static PlaybackRateShaper CreateShaper()
    {
        return new PlaybackRateShaper(
            riseSmoothingTime: 0.5f,
            fallSmoothingTime: 0.3f,
            minimumMovingRate: 0.8f,
            maximumMovingRate: 1.2f,
            movingEntryRate: 0.15f,
            stoppedEntryRate: 0.08f);
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
    public void SlowDrivingHoldsTheBandFloorNotSlowMotion()
    {
        PlaybackRateShaper shaper = CreateShaper();
        float rate = Run(shaper, 0.3f, false, 5f);

        // 0.3x of a 30fps clip is nine frames a second - the choppiness this
        // band exists to forbid. Slow driving plays at the floor instead.
        Assert.That(rate, Is.EqualTo(0.8f).Within(0.01f));
    }

    [Test]
    public void FastDrivingIsCappedAtTheBandCeiling()
    {
        PlaybackRateShaper shaper = CreateShaper();
        float rate = Run(shaper, 3f, false, 30f);

        Assert.That(rate, Is.LessThanOrEqualTo(1.2f));
    }

    [Test]
    public void CrawlBelowTheEntryRateStaysStopped()
    {
        PlaybackRateShaper shaper = CreateShaper();
        float rate = Run(shaper, 0.1f, false, 5f);

        // From rest, a creep under the entry threshold never starts the
        // video: below the band there is no crawl, only a stop.
        Assert.That(rate, Is.EqualTo(0f));
    }

    [Test]
    public void HysteresisRidesOutDipsWithoutFlapping()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);

        // A dip to 0.1 sits between the exit (0.08) and entry (0.15)
        // thresholds: already moving, so the video stays moving at the
        // floor rather than toggling pause/play.
        float dipped = Run(shaper, 0.1f, false, 5f);
        Assert.That(dipped, Is.EqualTo(0.8f).Within(0.01f));

        // Only dropping below the exit threshold lands the stop.
        float stopped = Run(shaper, 0.05f, false, 2f);
        Assert.That(stopped, Is.EqualTo(0f));
    }

    [Test]
    public void ActionHoldsItsTakeoffRateThroughAStop()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        float rate = Run(shaper, 0f, true, 5f);

        // The vehicle stopped mid-jump: the arc keeps the rate it took off
        // with, not the band floor and not a decay.
        Assert.That(rate, Is.EqualTo(1f).Within(0.02f));
    }

    [Test]
    public void LandingIntoAStopPausesOnTheLandingFrame()
    {
        PlaybackRateShaper shaper = CreateShaper();
        Run(shaper, 1f, false, 5f);
        Run(shaper, 0f, true, 5f);
        float rate = shaper.Update(0f, false, 1f / 60f);

        // Finish at speed, then freeze: the first frame after the action
        // ends is already a clean pause, with no ease-down smear.
        Assert.That(rate, Is.EqualTo(0f));
    }

    [Test]
    public void ActionStartedAtAStandstillStillPlaysTheArc()
    {
        PlaybackRateShaper shaper = CreateShaper();
        float rate = Run(shaper, 0f, true, 5f);

        // A free jump at a red light: arcs run on video time, so the video
        // must move at least at the band floor until the ball lands.
        Assert.That(rate, Is.EqualTo(0.8f).Within(0.01f));
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
