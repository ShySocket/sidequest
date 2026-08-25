using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Estimates GPS speed across a multi-second position window. Phone GPS fixes
/// often wander by several metres, so a one-fix-to-the-next derivative is much
/// noisier than the underlying vehicle motion.
/// </summary>
public sealed class GpsSpeedWindow
{
    private const double MinimumBaselineSeconds = 2.5d;
    private const double MaximumBaselineSeconds = 6d;
    private const int MaximumSamples = 32;

    private readonly List<PositionSample> samples =
        new List<PositionSample>(MaximumSamples);
    private readonly List<float> candidateSpeeds =
        new List<float>(MaximumSamples);

    public int SampleCount => samples.Count;

    public bool AddPosition(
        double latitude,
        double longitude,
        double gpsTimestamp,
        out float speedMetersPerSecond,
        out float courseDegrees)
    {
        speedMetersPerSecond = 0f;
        courseDegrees = float.NaN;
        if (samples.Count > 0
            && gpsTimestamp <= samples[samples.Count - 1].Timestamp)
        {
            return false;
        }

        samples.Add(new PositionSample(
            latitude,
            longitude,
            gpsTimestamp));
        Trim(gpsTimestamp);

        PositionSample newest = samples[samples.Count - 1];
        candidateSpeeds.Clear();
        int oldestCandidateIndex = -1;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            double elapsed = newest.Timestamp - samples[i].Timestamp;
            if (elapsed < MinimumBaselineSeconds)
            {
                continue;
            }

            double distance = GeoDistanceCalculator.DistanceMeters(
                samples[i].Latitude,
                samples[i].Longitude,
                newest.Latitude,
                newest.Longitude);
            candidateSpeeds.Add((float)(distance / elapsed));
            if (oldestCandidateIndex < 0)
            {
                oldestCandidateIndex = i;
            }
        }

        if (candidateSpeeds.Count == 0)
        {
            return false;
        }

        candidateSpeeds.Sort();
        int middle = candidateSpeeds.Count / 2;
        speedMetersPerSecond = candidateSpeeds.Count % 2 == 0
            ? 0.5f * (
                candidateSpeeds[middle - 1]
                + candidateSpeeds[middle])
            : candidateSpeeds[middle];
        PositionSample oldest = samples[oldestCandidateIndex];
        courseDegrees = GeoDistanceCalculator.InitialBearingDegrees(
            oldest.Latitude,
            oldest.Longitude,
            newest.Latitude,
            newest.Longitude);
        return true;
    }

    public void Reset()
    {
        samples.Clear();
        candidateSpeeds.Clear();
    }

    private void Trim(double newestTimestamp)
    {
        while (samples.Count > 1
            && (samples.Count > MaximumSamples
                || newestTimestamp - samples[0].Timestamp
                    > MaximumBaselineSeconds))
        {
            samples.RemoveAt(0);
        }
    }

    private readonly struct PositionSample
    {
        public PositionSample(
            double latitude,
            double longitude,
            double timestamp)
        {
            Latitude = latitude;
            Longitude = longitude;
            Timestamp = timestamp;
        }

        public double Latitude { get; }
        public double Longitude { get; }
        public double Timestamp { get; }
    }
}
