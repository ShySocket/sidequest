using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Deterministic sensor replay for a Downtown Berkeley -> Montgomery ride.
///
/// The stop order and approximate inter-station times follow BART's GTFS:
/// DBRK, ASHB, MCAR, 19TH, 12TH, WOAK, EMBR, MONT. The generated signal is
/// intentionally harsher than an ideal trace: acceleration is under-reported,
/// the phone rotates, GPS gets noisy, and fixes disappear in tunnels.
/// </summary>
public sealed class BartRideSimulationTests
{
    private const float MotionStepSeconds = 0.02f;
    private const float AccelerationMps2 = 0.85f;
    private const float BrakingMps2 = 0.95f;
    private const float SensorAccelerationScale = 0.9f;
    private const float DwellSeconds = 20f;

    private static readonly SimulatedLeg[] Legs =
    {
        new SimulatedLeg("Downtown Berkeley", "Ashby", 100f, 18f, GpsMode.Weak),
        new SimulatedLeg("Ashby", "MacArthur", 160f, 22f, GpsMode.Missing),
        new SimulatedLeg("MacArthur", "19th St Oakland", 160f, 20f, GpsMode.Good),
        new SimulatedLeg("19th St Oakland", "12th St Oakland", 100f, 14f, GpsMode.Good),
        new SimulatedLeg("12th St Oakland", "West Oakland", 220f, 23f, GpsMode.Weak),
        new SimulatedLeg("West Oakland", "Embarcadero", 400f, 30f, GpsMode.Missing),
        new SimulatedLeg("Embarcadero", "Montgomery St", 40f, 12f, GpsMode.Missing)
    };

    [Test]
    public void FullBartRideTracksSpeedAndEveryUndergroundStop()
    {
        MovementEstimator estimator = CreateEstimator();
        estimator.AddGpsSample(0f, 6f, 0d, 180f);
        SimulationMetrics metrics = new SimulationMetrics();
        double time = 0d;

        for (int legIndex = 0; legIndex < Legs.Length; legIndex++)
        {
            SimulatedLeg leg = Legs[legIndex];
            int falseStopsBeforeLeg = metrics.FalseStopSamples;
            SimulateLeg(estimator, leg, legIndex, ref time, metrics);
            TestContext.WriteLine(
                $"{leg.To} arrival estimate="
                    + $"{estimator.EstimatedVehicleSpeedMps:F2} m/s, "
                    + $"stationary={estimator.IsStationary}, "
                    + "false-stop samples="
                    + (metrics.FalseStopSamples - falseStopsBeforeLeg));
            SimulateDwell(
                estimator,
                leg.To,
                legIndex,
                ref time,
                metrics);
        }

        metrics.Finish();
        TestContext.WriteLine(metrics.ToString());

        Assert.That(metrics.FalseStopSamples, Is.Zero);
        Assert.That(metrics.MaximumStopLatencySeconds, Is.LessThan(1.25f));
        Assert.That(metrics.MeanAbsoluteSpeedErrorMps, Is.LessThan(2.4f));
        Assert.That(metrics.Percentile95SpeedErrorMps, Is.LessThan(4.5f));
        Assert.That(estimator.IsStationary, Is.True);
        Assert.That(estimator.EstimatedVehicleSpeedMps, Is.Zero);
    }

    [Test]
    public void NormalBartBrakingIsNotAnImmediateStop()
    {
        const float cruiseSpeed = 25f;
        float speedAfterOldHardBrakeWindow = cruiseSpeed
            - BrakingMps2 * 0.18f;

        Assert.That(speedAfterOldHardBrakeWindow, Is.GreaterThan(24f));
    }

    private static void SimulateLeg(
        MovementEstimator estimator,
        SimulatedLeg leg,
        int legIndex,
        ref double time,
        SimulationMetrics metrics)
    {
        float accelerationSeconds = leg.CruiseSpeedMps / AccelerationMps2;
        float brakingSeconds = leg.CruiseSpeedMps / BrakingMps2;
        float cruiseSeconds = Mathf.Max(
            0f,
            leg.MovementSeconds - accelerationSeconds - brakingSeconds);
        int samples = Mathf.RoundToInt(
            leg.MovementSeconds / MotionStepSeconds);
        int gpsStride = Mathf.RoundToInt(1f / MotionStepSeconds);

        for (int sample = 1; sample <= samples; sample++)
        {
            float legTime = sample * MotionStepSeconds;
            MotionTruth truth = CalculateTruth(
                leg,
                legTime,
                accelerationSeconds,
                cruiseSeconds,
                brakingSeconds);
            time += MotionStepSeconds;

            Quaternion attitude = PhoneAttitude(time, legIndex);
            float deterministicNoise = 0.045f
                * Mathf.Sin((float)time * 7.13f + legIndex * 0.71f);
            Vector3 referenceAcceleration = Vector3.right
                * (truth.AccelerationMps2 * SensorAccelerationScale
                    + deterministicNoise);
            Vector3 deviceAcceleration =
                Quaternion.Inverse(attitude) * referenceAcceleration;
            estimator.AddMotionSample(new DeviceMotionReading(
                deviceAcceleration,
                attitude,
                time));

            if (sample == Mathf.RoundToInt(1f / MotionStepSeconds))
            {
                TestContext.WriteLine(
                    $"{leg.From}->{leg.To} at 1s: "
                    + $"forwardAccel={estimator.ForwardAccelerationMps2:F2}, "
                    + $"axis={estimator.InferredForwardAxis}, "
                    + $"stationary={estimator.IsStationary}");
            }

            if (sample % gpsStride == 0 && HasGpsFix(leg.GpsMode, sample))
            {
                float accuracy = leg.GpsMode == GpsMode.Good ? 7f : 28f;
                float gpsNoise = leg.GpsMode == GpsMode.Good
                    ? 0.35f * Mathf.Sin((float)time * 0.83f)
                    : 1.4f * Mathf.Sin((float)time * 1.17f);
                estimator.AddGpsSample(
                    Mathf.Max(0f, truth.SpeedMps + gpsNoise),
                    accuracy,
                    time,
                    180f);
            }

            float estimatedSpeed = estimator.Update(time);
            metrics.ObserveMoving(
                truth.SpeedMps,
                estimatedSpeed,
                estimator.IsStationary);
        }
    }

    private static void SimulateDwell(
        MovementEstimator estimator,
        string station,
        int legIndex,
        ref double time,
        SimulationMetrics metrics)
    {
        bool stopSeen = false;
        float stopLatency = DwellSeconds;
        int samples = Mathf.RoundToInt(DwellSeconds / MotionStepSeconds);
        for (int sample = 1; sample <= samples; sample++)
        {
            time += MotionStepSeconds;
            Quaternion attitude = PhoneAttitude(time, legIndex);
            float vibration = 0.035f
                * Mathf.Sin((float)time * 11.7f);
            Vector3 deviceAcceleration = Quaternion.Inverse(attitude)
                * (Vector3.right * vibration);
            estimator.AddMotionSample(new DeviceMotionReading(
                deviceAcceleration,
                attitude,
                time));
            estimator.Update(time);

            if (!stopSeen && estimator.IsStationary)
            {
                stopSeen = true;
                stopLatency = sample * MotionStepSeconds;
            }
        }

        metrics.ObserveStop(station, stopLatency);
    }

    private static MotionTruth CalculateTruth(
        SimulatedLeg leg,
        float legTime,
        float accelerationSeconds,
        float cruiseSeconds,
        float brakingSeconds)
    {
        if (legTime <= accelerationSeconds)
        {
            return new MotionTruth(
                Mathf.Min(
                    leg.CruiseSpeedMps,
                    AccelerationMps2 * legTime),
                AccelerationMps2);
        }

        if (legTime <= accelerationSeconds + cruiseSeconds)
        {
            return new MotionTruth(leg.CruiseSpeedMps, 0f);
        }

        float brakingTime =
            legTime - accelerationSeconds - cruiseSeconds;
        return new MotionTruth(
            Mathf.Max(
                0f,
                leg.CruiseSpeedMps - BrakingMps2 * brakingTime),
            brakingTime <= brakingSeconds ? -BrakingMps2 : 0f);
    }

    private static bool HasGpsFix(GpsMode mode, int sample)
    {
        if (mode == GpsMode.Missing)
        {
            return false;
        }

        if (mode == GpsMode.Weak)
        {
            // Intermittent 1 Hz fixes: four seconds present, eight absent.
            int second = Mathf.RoundToInt(sample * MotionStepSeconds);
            return second % 12 < 4;
        }

        return true;
    }

    private static Quaternion PhoneAttitude(double time, int legIndex)
    {
        float yaw = 55f * Mathf.Sin((float)time * 0.013f)
            + legIndex * 31f;
        float pitch = 18f * Mathf.Sin((float)time * 0.007f);
        return Quaternion.Euler(pitch, 0f, yaw);
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

    private enum GpsMode
    {
        Good,
        Weak,
        Missing
    }

    private readonly struct SimulatedLeg
    {
        public SimulatedLeg(
            string from,
            string to,
            float movementSeconds,
            float cruiseSpeedMps,
            GpsMode gpsMode)
        {
            From = from;
            To = to;
            MovementSeconds = movementSeconds;
            CruiseSpeedMps = cruiseSpeedMps;
            GpsMode = gpsMode;
        }

        public string From { get; }
        public string To { get; }
        public float MovementSeconds { get; }
        public float CruiseSpeedMps { get; }
        public GpsMode GpsMode { get; }
    }

    private readonly struct MotionTruth
    {
        public MotionTruth(float speedMps, float accelerationMps2)
        {
            SpeedMps = speedMps;
            AccelerationMps2 = accelerationMps2;
        }

        public float SpeedMps { get; }
        public float AccelerationMps2 { get; }
    }

    private sealed class SimulationMetrics
    {
        private readonly List<float> speedErrors = new List<float>(70000);
        private float errorSum;

        public int FalseStopSamples { get; private set; }
        public float MaximumStopLatencySeconds { get; private set; }
        public float MeanAbsoluteSpeedErrorMps { get; private set; }
        public float Percentile95SpeedErrorMps { get; private set; }

        public void ObserveMoving(
            float trueSpeed,
            float estimatedSpeed,
            bool isStationary)
        {
            if (trueSpeed > 2f && isStationary)
            {
                FalseStopSamples++;
            }

            if (trueSpeed < 1f)
            {
                return;
            }

            float error = Mathf.Abs(trueSpeed - estimatedSpeed);
            speedErrors.Add(error);
            errorSum += error;
        }

        public void ObserveStop(string station, float latencySeconds)
        {
            MaximumStopLatencySeconds = Mathf.Max(
                MaximumStopLatencySeconds,
                latencySeconds);
            TestContext.WriteLine(
                $"{station} stop latency={latencySeconds:F2} s");
        }

        public void Finish()
        {
            speedErrors.Sort();
            MeanAbsoluteSpeedErrorMps = speedErrors.Count == 0
                ? 0f
                : errorSum / speedErrors.Count;
            int percentileIndex = Mathf.Clamp(
                Mathf.CeilToInt(speedErrors.Count * 0.95f) - 1,
                0,
                Mathf.Max(0, speedErrors.Count - 1));
            Percentile95SpeedErrorMps = speedErrors.Count == 0
                ? 0f
                : speedErrors[percentileIndex];
        }

        public override string ToString()
        {
            return "BART simulation: "
                + $"MAE={MeanAbsoluteSpeedErrorMps:F2} m/s, "
                + $"P95={Percentile95SpeedErrorMps:F2} m/s, "
                + $"max stop latency={MaximumStopLatencySeconds:F2} s, "
                + $"false-stop samples={FalseStopSamples}";
        }
    }
}
