using UnityEngine;

public readonly struct MovementTuningValues
{
    public const float MinimumNoiseFilter = 0.05f;
    public const float MaximumNoiseFilter = 1.2f;
    public const float MinimumGpsStaleSeconds = 0.5f;
    public const float MaximumGpsStaleSeconds = 5f;
    public const float MinimumFallbackSeconds = 5f;
    public const float MaximumFallbackSeconds = 120f;

    private const float LeastSensitiveBrakingThreshold = -4.5f;
    private const float MostSensitiveBrakingThreshold = -1.2f;

    public MovementTuningValues(
        float gpsReliance,
        float suddenStopSensitivity,
        float accelerationNoiseFilter,
        float gpsStaleSeconds,
        float gpsLossFallbackSeconds)
    {
        GpsReliance = Mathf.Clamp01(gpsReliance);
        SuddenStopSensitivity = Mathf.Clamp01(suddenStopSensitivity);
        AccelerationNoiseFilter = Mathf.Clamp(
            accelerationNoiseFilter,
            MinimumNoiseFilter,
            MaximumNoiseFilter);
        GpsStaleSeconds = Mathf.Clamp(
            gpsStaleSeconds,
            MinimumGpsStaleSeconds,
            MaximumGpsStaleSeconds);
        GpsLossFallbackSeconds = Mathf.Max(
            GpsStaleSeconds,
            Mathf.Clamp(
                gpsLossFallbackSeconds,
                MinimumFallbackSeconds,
                MaximumFallbackSeconds));
    }

    public float GpsReliance { get; }
    public float SuddenStopSensitivity { get; }
    public float AccelerationNoiseFilter { get; }
    public float GpsStaleSeconds { get; }
    public float GpsLossFallbackSeconds { get; }
    public float AccelerometerReliance => 1f - GpsReliance;
    public float HardBrakingThresholdMetersPerSecondSquared => Mathf.Lerp(
        LeastSensitiveBrakingThreshold,
        MostSensitiveBrakingThreshold,
        SuddenStopSensitivity);

    public static MovementTuningValues FromConfiguration(
        RunnerConfiguration configuration)
    {
        float sensitivity = Mathf.InverseLerp(
            LeastSensitiveBrakingThreshold,
            MostSensitiveBrakingThreshold,
            configuration.HardBrakingThresholdMetersPerSecondSquared);
        return new MovementTuningValues(
            configuration.HealthyGpsWeight,
            sensitivity,
            configuration.AccelerationNoiseDeadZoneMetersPerSecondSquared,
            configuration.EstimatorGpsStaleThresholdSeconds,
            configuration.EstimatorGpsMissingThresholdSeconds);
    }
}

public static class MovementTuningPreferences
{
    private const string Prefix = "MovementTuning.";
    private const string GpsRelianceKey = Prefix + "GpsReliance";
    private const string StopSensitivityKey = Prefix + "StopSensitivity";
    private const string NoiseFilterKey = Prefix + "NoiseFilter";
    private const string StaleSecondsKey = Prefix + "StaleSeconds";
    private const string FallbackSecondsKey = Prefix + "FallbackSeconds";

    public static MovementTuningValues Load(RunnerConfiguration configuration)
    {
        MovementTuningValues defaults =
            MovementTuningValues.FromConfiguration(configuration);
        return new MovementTuningValues(
            PlayerPrefs.GetFloat(GpsRelianceKey, defaults.GpsReliance),
            PlayerPrefs.GetFloat(
                StopSensitivityKey,
                defaults.SuddenStopSensitivity),
            PlayerPrefs.GetFloat(
                NoiseFilterKey,
                defaults.AccelerationNoiseFilter),
            PlayerPrefs.GetFloat(StaleSecondsKey, defaults.GpsStaleSeconds),
            PlayerPrefs.GetFloat(
                FallbackSecondsKey,
                defaults.GpsLossFallbackSeconds));
    }

    public static void Save(MovementTuningValues values)
    {
        PlayerPrefs.SetFloat(GpsRelianceKey, values.GpsReliance);
        PlayerPrefs.SetFloat(
            StopSensitivityKey,
            values.SuddenStopSensitivity);
        PlayerPrefs.SetFloat(
            NoiseFilterKey,
            values.AccelerationNoiseFilter);
        PlayerPrefs.SetFloat(StaleSecondsKey, values.GpsStaleSeconds);
        PlayerPrefs.SetFloat(
            FallbackSecondsKey,
            values.GpsLossFallbackSeconds);
        PlayerPrefs.Save();
    }

    public static MovementTuningValues Reset(RunnerConfiguration configuration)
    {
        PlayerPrefs.DeleteKey(GpsRelianceKey);
        PlayerPrefs.DeleteKey(StopSensitivityKey);
        PlayerPrefs.DeleteKey(NoiseFilterKey);
        PlayerPrefs.DeleteKey(StaleSecondsKey);
        PlayerPrefs.DeleteKey(FallbackSecondsKey);
        PlayerPrefs.Save();
        return MovementTuningValues.FromConfiguration(configuration);
    }
}
