using NUnit.Framework;
using UnityEngine;

public sealed class MovementEstimatorTests
{
    private static MovementEstimator CreateEstimator()
    {
        return new MovementEstimator(new MovementEstimatorSettings(
            1.5f,
            30f,
            1.5f,
            -2.3f,
            0.18f,
            0.2f,
            0.9f,
            8f,
            80f,
            0.75f,
            0.98f,
            75f));
    }

    [Test]
    public void HealthyGpsCorrectsPredictionWithoutDiscardingAcceleration()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right * 2f, 0.02d));
        for (int i = 2; i <= 10; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right * 2f, i * 0.02d));
        }

        estimator.AddGpsSample(10f, 5f, 0.2d);
        estimator.AddMotionSample(Motion(Vector3.right * 2f, 0.3d));
        float speed = estimator.Update(0.3d);

        Assert.That(estimator.GpsHealth, Is.EqualTo(GpsHealth.Healthy));
        Assert.That(speed, Is.GreaterThan(8.5f).And.LessThan(10.5f));
        Assert.That(estimator.Confidence, Is.GreaterThan(0.9f));
    }

    [Test]
    public void BrakingDoesNotClaimTheTrainStoppedWhileItIsMoving()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right * 3f, 0.02d));
        for (int i = 2; i <= 20; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right * 3f, i * 0.02d));
        }

        estimator.AddGpsSample(10f, 5f, 0.4d);
        for (int i = 21; i <= 45; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.left * 4f, i * 0.02d));
        }

        Assert.That(estimator.Update(0.9d), Is.GreaterThan(6f));
        Assert.That(estimator.IsBraking, Is.True);
        Assert.That(estimator.SuddenStopDetected, Is.False);
    }

    [Test]
    public void LongBrakingProfileStopsAfterAccelerationBecomesQuiet()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right, 0.02d));
        for (int i = 2; i <= 20; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right, i * 0.02d));
        }

        estimator.AddGpsSample(12f, 5f, 0.4d);
        int sample = 21;
        for (; sample <= 620; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.left * 0.6f,
                sample * 0.02d));
        }

        Assert.That(estimator.Update(sample * 0.02d), Is.GreaterThan(0f));
        for (; sample <= 660; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.zero,
                sample * 0.02d));
        }

        Assert.That(estimator.Update(sample * 0.02d), Is.Zero);
        Assert.That(estimator.IsStationary, Is.True);
        Assert.That(estimator.SuddenStopDetected, Is.True);
    }

    [Test]
    public void StaleGpsUsesIntegratedAcceleration()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right * 2f, 0.02d));
        for (int i = 2; i <= 25; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right * 2f, i * 0.02d));
        }

        estimator.AddGpsSample(10f, 5f, 0.5d);
        for (int i = 26; i <= 110; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right, i * 0.02d));
        }

        float speed = estimator.Update(2.2d);
        Assert.That(estimator.GpsHealth, Is.EqualTo(GpsHealth.Stale));
        Assert.That(estimator.IsDeadReckoning, Is.True);
        Assert.That(speed, Is.GreaterThan(10.5f));
    }

    [Test]
    public void MissingGpsDecaysWhenAccelerationIsQuiet()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(10f, 5f, 0d);
        estimator.Update(31d);
        float decayed = estimator.Update(32d);

        Assert.That(estimator.GpsHealth, Is.EqualTo(GpsHealth.Missing));
        Assert.That(decayed, Is.EqualTo(9.8f).Within(0.01f));
        Assert.That(estimator.Confidence, Is.LessThan(0.35f));
    }

    [Test]
    public void ReturningGpsBlendsOverConfiguredDuration()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(10f, 5f, 0d);
        estimator.Update(2d);
        estimator.AddGpsSample(20f, 5f, 2d);

        float start = estimator.Update(2d);
        float middle = estimator.Update(2.75d);
        float end = estimator.Update(3.5d);

        Assert.That(start, Is.EqualTo(10f).Within(0.01f));
        Assert.That(middle, Is.GreaterThan(start).And.LessThan(end));
        Assert.That(end, Is.GreaterThan(18f).And.LessThan(20f));
    }

    [Test]
    public void SingleGpsSpikeWhileStationaryIsIgnored()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.Update(0d);

        estimator.AddGpsSample(12f, 5f, 1d);

        Assert.That(estimator.Update(1d), Is.Zero);
        Assert.That(estimator.IsStationary, Is.True);
    }

    [Test]
    public void RepeatedMovingGpsReadingsReleaseStationaryLock()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddGpsSample(5f, 5f, 1d);
        estimator.AddGpsSample(5f, 5f, 2d);
        estimator.AddGpsSample(5f, 5f, 3d);

        Assert.That(estimator.Update(3d), Is.EqualTo(5f).Within(0.6f));
        Assert.That(estimator.IsStationary, Is.False);
    }

    [Test]
    public void GpsJitterWithChangingCourseDoesNotReleaseStationaryLock()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddGpsSample(6f, 5f, 1d, 0f);
        estimator.AddGpsSample(6f, 5f, 2d, 120f);
        estimator.AddGpsSample(6f, 5f, 3d, 240f);

        Assert.That(estimator.Update(3d), Is.Zero);
        Assert.That(estimator.IsStationary, Is.True);
    }

    [Test]
    public void BriefPhoneBumpDoesNotReleaseStationaryLock()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right * 4f, 0.02d));
        estimator.AddMotionSample(Motion(Vector3.right * 4f, 0.04d));
        estimator.AddMotionSample(Motion(Vector3.left * 4f, 0.06d));
        estimator.AddMotionSample(Motion(Vector3.zero, 0.08d));

        Assert.That(estimator.Update(0.08d), Is.Zero);
        Assert.That(estimator.IsStationary, Is.True);
    }

    [Test]
    public void SustainedDirectionalAccelerationReleasesStationaryLock()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right, 0.02d));
        for (int i = 2; i <= 12; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right, i * 0.02d));
        }

        Assert.That(estimator.Update(0.24d), Is.GreaterThan(0f));
        Assert.That(estimator.IsStationary, Is.False);
    }

    [Test]
    public void PhoneRotationDoesNotChangeForwardAcceleration()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        Quaternion attitude = Quaternion.Euler(0f, 0f, 90f);
        Vector3 referenceAcceleration = Vector3.right;
        Vector3 deviceAcceleration =
            Quaternion.Inverse(attitude) * referenceAcceleration;

        estimator.AddMotionSample(new DeviceMotionReading(
            deviceAcceleration,
            attitude,
            0.02d));
        for (int i = 2; i <= 15; i++)
        {
            estimator.AddMotionSample(new DeviceMotionReading(
                deviceAcceleration,
                attitude,
                i * 0.02d));
        }

        Assert.That(estimator.Update(0.3d), Is.GreaterThan(0f));
        Assert.That(estimator.ForwardAccelerationMps2, Is.GreaterThan(0.5f));
    }

    [Test]
    public void ActiveMotionSensorPreventsArbitraryTunnelSpeedDecay()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(18f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.zero, 0.02d));
        for (int second = 1; second <= 180; second++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.zero,
                second));
            estimator.Update(second);
        }

        Assert.That(estimator.GpsHealth, Is.EqualTo(GpsHealth.Missing));
        Assert.That(
            estimator.EstimatedVehicleSpeedMps,
            Is.EqualTo(18f).Within(0.1f));
    }

    [Test]
    public void AccelerometerCanRestartAfterAnUndergroundStop()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 5f, 0d);
        estimator.AddMotionSample(Motion(Vector3.right, 0.02d));
        for (int i = 2; i <= 20; i++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.right,
                i * 0.02d));
        }

        estimator.AddGpsSample(10f, 5f, 0.4d);
        int sample = 21;
        for (; sample <= 620; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.left,
                sample * 0.02d));
        }

        for (; sample <= 720; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.zero,
                sample * 0.02d));
        }

        Assert.That(estimator.IsStationary, Is.True);
        for (; sample <= 750; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.right,
                sample * 0.02d));
        }

        Assert.That(
            estimator.Update(sample * 0.02d),
            Is.GreaterThan(0f));
        Assert.That(estimator.IsStationary, Is.False);
    }

    [Test]
    public void LaggedGpsWindowSpeedAfterConfirmedStopDoesNotRestartMotion()
    {
        MovementEstimator estimator = CreateEstimator();
        BrakeToConfirmedStop(estimator, out double time);

        // A windowed GPS speed estimate straddles the stop, so it keeps
        // reporting a decaying residual speed with a steady course.
        estimator.AddGpsSample(2f, 5f, time + 0.3d, 0f);
        estimator.AddGpsSample(1.6f, 5f, time + 1.3d, 0f);
        estimator.AddGpsSample(1.2f, 5f, time + 2.3d, 0f);

        Assert.That(estimator.Update(time + 2.3d), Is.Zero);
        Assert.That(estimator.IsStationary, Is.True);
    }

    [Test]
    public void MovingGpsReadingsAfterSuppressionWindowStillReleaseTheStop()
    {
        MovementEstimator estimator = CreateEstimator();
        BrakeToConfirmedStop(estimator, out double time);

        estimator.AddGpsSample(2f, 5f, time + 6.3d, 0f);
        estimator.AddGpsSample(2f, 5f, time + 7.3d, 0f);
        estimator.AddGpsSample(2f, 5f, time + 8.3d, 0f);

        Assert.That(estimator.Update(time + 8.3d), Is.GreaterThan(1f));
        Assert.That(estimator.IsStationary, Is.False);
    }

    private static void BrakeToConfirmedStop(
        MovementEstimator estimator,
        out double time)
    {
        estimator.AddGpsSample(0f, 5f, 0d);
        for (int i = 1; i <= 20; i++)
        {
            estimator.AddMotionSample(Motion(Vector3.right, i * 0.02d));
        }

        estimator.AddGpsSample(12f, 5f, 0.4d);
        int sample = 21;
        for (; sample <= 620; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.left * 0.6f,
                sample * 0.02d));
        }

        for (; sample <= 660; sample++)
        {
            estimator.AddMotionSample(Motion(
                Vector3.zero,
                sample * 0.02d));
        }

        time = sample * 0.02d;
        estimator.Update(time);
        Assert.That(estimator.IsStationary, Is.True);
    }

    private static DeviceMotionReading Motion(Vector3 acceleration, double timestamp)
    {
        return new DeviceMotionReading(acceleration, Quaternion.identity, timestamp);
    }
}
