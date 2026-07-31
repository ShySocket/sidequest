using UnityEngine;

public readonly struct MovementEstimatorSettings
{
    public MovementEstimatorSettings(
        float gpsStaleThresholdSeconds,
        float gpsMissingThresholdSeconds,
        float gpsReturnBlendSeconds,
        float hardBrakingThresholdMetersPerSecondSquared,
        float hardBrakingDurationSeconds,
        float accelerationNoiseDeadZoneMetersPerSecondSquared,
        float gpsWeight,
        float maximumAccelerationMetersPerSecondSquared,
        float maximumSpeedMetersPerSecond,
        float stopHoldSeconds,
        float missingSpeedRetentionPerSecond,
        float maximumGpsAccuracyMeters,
        float stationarySpeedThresholdMetersPerSecond = 0.75f)
    {
        GpsStaleThresholdSeconds = Mathf.Max(0.1f, gpsStaleThresholdSeconds);
        GpsMissingThresholdSeconds = Mathf.Max(
            GpsStaleThresholdSeconds,
            gpsMissingThresholdSeconds);
        GpsReturnBlendSeconds = Mathf.Max(0f, gpsReturnBlendSeconds);
        HardBrakingThresholdMetersPerSecondSquared = Mathf.Min(
            -0.1f,
            hardBrakingThresholdMetersPerSecondSquared);
        HardBrakingDurationSeconds = Mathf.Max(0.02f, hardBrakingDurationSeconds);
        AccelerationNoiseDeadZoneMetersPerSecondSquared = Mathf.Max(
            0f,
            accelerationNoiseDeadZoneMetersPerSecondSquared);
        GpsWeight = Mathf.Clamp01(gpsWeight);
        MaximumAccelerationMetersPerSecondSquared = Mathf.Max(
            0.1f,
            maximumAccelerationMetersPerSecondSquared);
        MaximumSpeedMetersPerSecond = Mathf.Max(0f, maximumSpeedMetersPerSecond);
        StopHoldSeconds = Mathf.Max(0f, stopHoldSeconds);
        MissingSpeedRetentionPerSecond = Mathf.Clamp01(missingSpeedRetentionPerSecond);
        MaximumGpsAccuracyMeters = Mathf.Max(1f, maximumGpsAccuracyMeters);
        StationarySpeedThresholdMetersPerSecond = Mathf.Max(
            0f,
            stationarySpeedThresholdMetersPerSecond);
    }

    public float GpsStaleThresholdSeconds { get; }
    public float GpsMissingThresholdSeconds { get; }
    public float GpsReturnBlendSeconds { get; }
    public float HardBrakingThresholdMetersPerSecondSquared { get; }
    public float HardBrakingDurationSeconds { get; }
    public float AccelerationNoiseDeadZoneMetersPerSecondSquared { get; }
    public float GpsWeight { get; }
    public float MaximumAccelerationMetersPerSecondSquared { get; }
    public float MaximumSpeedMetersPerSecond { get; }
    public float StopHoldSeconds { get; }
    public float MissingSpeedRetentionPerSecond { get; }
    public float MaximumGpsAccuracyMeters { get; }
    public float StationarySpeedThresholdMetersPerSecond { get; }
}
