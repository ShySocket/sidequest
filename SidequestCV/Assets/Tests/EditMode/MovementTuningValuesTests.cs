using NUnit.Framework;

public sealed class MovementTuningValuesTests
{
    [Test]
    public void ValuesAreClampedToSafeDriveTestingRanges()
    {
        MovementTuningValues values = new MovementTuningValues(
            -1f,
            2f,
            20f,
            0f,
            500f);

        Assert.That(values.GpsReliance, Is.EqualTo(0f));
        Assert.That(values.AccelerometerReliance, Is.EqualTo(1f));
        Assert.That(values.SuddenStopSensitivity, Is.EqualTo(1f));
        Assert.That(
            values.AccelerationNoiseFilter,
            Is.EqualTo(MovementTuningValues.MaximumNoiseFilter));
        Assert.That(
            values.GpsStaleSeconds,
            Is.EqualTo(MovementTuningValues.MinimumGpsStaleSeconds));
        Assert.That(
            values.GpsLossFallbackSeconds,
            Is.EqualTo(MovementTuningValues.MaximumFallbackSeconds));
    }

    [Test]
    public void HigherStopSensitivityUsesAnEasierBrakingThreshold()
    {
        MovementTuningValues low = new MovementTuningValues(
            0.9f,
            0f,
            0.2f,
            1.5f,
            30f);
        MovementTuningValues high = new MovementTuningValues(
            0.9f,
            1f,
            0.2f,
            1.5f,
            30f);

        Assert.That(
            high.HardBrakingThresholdMetersPerSecondSquared,
            Is.GreaterThan(low.HardBrakingThresholdMetersPerSecondSquared));
    }
}
