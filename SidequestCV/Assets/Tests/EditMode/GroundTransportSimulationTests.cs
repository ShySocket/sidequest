using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Deterministic road-transport sensor replays. These are deliberately not
/// tied to a route: they exercise motion patterns shared by cars and buses.
/// </summary>
public sealed class GroundTransportSimulationTests
{
    private const float StepSeconds = 0.02f;

    [Test]
    public void CarHardStopIsDetectedDuringGpsOutage()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        EstablishSpeedAndAxis(estimator, 10f, 0f, ref time);

        bool hardBrakeSeen = false;
        for (int sample = 0; sample < 100; sample++)
        {
            AddMotion(estimator, Vector3.left * 4.5f, ref time);
            estimator.Update(time);
            hardBrakeSeen |= estimator.IsHardBraking;
        }

        double physicalStopTime = time;
        while (!estimator.IsStationary && time < physicalStopTime + 1.5d)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        Assert.That(hardBrakeSeen, Is.True);
        Assert.That(estimator.IsStationary, Is.True);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.Zero);
        Assert.That(time - physicalStopTime, Is.LessThan(0.8d));
    }

    [Test]
    public void PartialEmergencyBrakeDoesNotClaimTheCarStopped()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        EstablishSpeedAndAxis(estimator, 20f, 0f, ref time);

        for (int sample = 0; sample < 40; sample++)
        {
            AddMotion(estimator, Vector3.left * 4.5f, ref time);
            estimator.Update(time);
        }

        for (int sample = 0; sample < 75; sample++)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        Assert.That(estimator.IsStationary, Is.False);
        Assert.That(
            estimator.EstimatedVehicleSpeedMps,
            Is.GreaterThan(14f));
        Assert.That(estimator.SuddenStopDetected, Is.False);
    }

    [Test]
    public void LongServiceBrakeToRollingSpeedDoesNotClaimTheBusStopped()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        EstablishSpeedAndAxis(estimator, 14f, 0f, ref time);

        for (int sample = 0; sample < 250; sample++)
        {
            AddMotion(estimator, Vector3.left * 1.2f, ref time);
            estimator.Update(time);
        }

        for (int sample = 0; sample < 75; sample++)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        Assert.That(estimator.IsStationary, Is.False);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.GreaterThan(6f));
        Assert.That(estimator.SuddenStopDetected, Is.False);
    }

    [Test]
    public void BusStopAndRestartWorkWithGpsCompletelyMissing()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        estimator.AddGpsSample(0f, 5f, time, 0f);

        for (int sample = 0; sample < 285; sample++)
        {
            AddMotion(estimator, Vector3.right * 1.4f, ref time);
            estimator.Update(time);
        }

        while (time < 32d)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        Assert.That(estimator.GpsHealth, Is.EqualTo(GpsHealth.Missing));
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.GreaterThan(7f));

        for (int sample = 0; sample < 270; sample++)
        {
            AddMotion(estimator, Vector3.left * 1.5f, ref time);
            estimator.Update(time);
        }

        for (int sample = 0; sample < 40; sample++)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        Assert.That(estimator.IsStationary, Is.True);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.Zero);

        while (estimator.SuddenStopDetected)
        {
            AddMotion(estimator, Vector3.zero, ref time);
            estimator.Update(time);
        }

        for (int sample = 0; sample < 20; sample++)
        {
            AddMotion(estimator, Vector3.back * 1.4f, ref time);
            estimator.Update(time);
        }

        Assert.That(estimator.IsStationary, Is.False);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.GreaterThan(0f));
        Assert.That(
            Vector3.Dot(estimator.InferredForwardAxis, Vector3.back),
            Is.GreaterThan(0.8f));
    }

    [Test]
    public void GpsCourseChangeKeepsAccelerationAlignedAfterRoadTurn()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        EstablishSpeedAndAxis(estimator, 10f, 0f, ref time);

        time += 1d;
        estimator.AddGpsSample(10f, 5f, time, 90f);
        float speedBeforeAcceleration = estimator.Update(time);

        for (int sample = 0; sample < 50; sample++)
        {
            AddMotion(estimator, Vector3.back, ref time);
            estimator.Update(time);
        }

        Assert.That(
            Vector3.Dot(estimator.InferredForwardAxis, Vector3.back),
            Is.GreaterThan(0.8f));
        Assert.That(
            estimator.EstimatedVehicleSpeedMps,
            Is.GreaterThan(speedBeforeAcceleration + 0.5f));
    }

    [Test]
    public void AlternatingPhoneMotionDoesNotReleaseStationaryLock()
    {
        MovementEstimator estimator = CreateEstimator();
        double time = 0d;
        estimator.AddGpsSample(0f, 5f, time);

        for (int burst = 0; burst < 20; burst++)
        {
            Vector3 acceleration = burst % 2 == 0
                ? Vector3.right * 3f
                : Vector3.left * 3f;
            for (int sample = 0; sample < 4; sample++)
            {
                AddMotion(estimator, acceleration, ref time);
                estimator.Update(time);
            }
        }

        Assert.That(estimator.IsStationary, Is.True);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.Zero);
    }

    private static void EstablishSpeedAndAxis(
        MovementEstimator estimator,
        float speedMetersPerSecond,
        float courseDegrees,
        ref double time)
    {
        estimator.AddGpsSample(0f, 5f, time, courseDegrees);
        for (int sample = 0; sample < 20; sample++)
        {
            AddMotion(estimator, Vector3.right * 2f, ref time);
            estimator.Update(time);
        }

        estimator.AddGpsSample(
            speedMetersPerSecond,
            5f,
            time,
            courseDegrees);
        estimator.Update(time);
    }

    private static void AddMotion(
        MovementEstimator estimator,
        Vector3 acceleration,
        ref double time)
    {
        time += StepSeconds;
        estimator.AddMotionSample(new DeviceMotionReading(
            acceleration,
            Quaternion.identity,
            time));
    }

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
            0.999f,
            75f,
            0.75f));
    }
}
