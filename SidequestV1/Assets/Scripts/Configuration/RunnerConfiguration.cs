using UnityEngine;

[CreateAssetMenu(
    fileName = "RunnerConfiguration",
    menuName = "Vehicle Runner/Runner Configuration")]
public sealed class RunnerConfiguration : ScriptableObject
{
    [Header("Speed Delay")]
    [SerializeField, Min(0f)] private float playbackDelaySeconds = 1f;
    [SerializeField, Min(0.1f)] private float speedHistoryDurationSeconds = 5f;

    [Header("Speed Smoothing")]
    [SerializeField, Min(0f)] private float accelerationSmoothingTime = 0.25f;
    [SerializeField, Min(0f)] private float decelerationSmoothingTime = 0.35f;

    [Header("GPS Validation")]
    [SerializeField, Min(0f)] private float desiredGpsAccuracyMeters = 10f;
    [SerializeField, Min(0f)] private float gpsUpdateDistanceMeters = 0.1f;
    [SerializeField, Min(0.1f)] private float gpsInitializationTimeoutSeconds = 45f;
    [SerializeField, Min(0.05f)] private float gpsPollingIntervalSeconds = 0.25f;
    [SerializeField, Min(0f)] private float maximumAcceptedHorizontalAccuracyMeters = 75f;
    [SerializeField, Min(0f)] private float maximumAcceptedPhysicalSpeedMetersPerSecond = 80f;
    [SerializeField, Min(0.1f)] private float gpsStaleTimeoutSeconds = 15f;
    [SerializeField, Min(0f)] private float stopThresholdMetersPerSecond = 0.75f;
    [SerializeField, Min(1)] private int requiredConsecutiveLowSpeedReadings = 1;

    [Header("Movement Estimation")]
    [SerializeField, Min(0.1f)] private float estimatorGpsStaleThresholdSeconds = 1.5f;
    [SerializeField, Min(1f)] private float estimatorGpsMissingThresholdSeconds = 30f;
    [SerializeField, Min(0f)] private float gpsReturnBlendSeconds = 1.5f;
    [SerializeField, Range(-10f, -0.1f)] private float hardBrakingThresholdMetersPerSecondSquared = -2.3f;
    [SerializeField, Min(0.02f)] private float hardBrakingDurationSeconds = 0.18f;
    [SerializeField, Min(0f)] private float accelerationNoiseDeadZoneMetersPerSecondSquared = 0.2f;
    [SerializeField, Range(0f, 1f)] private float healthyGpsWeight = 0.9f;
    [SerializeField, Min(0.1f)] private float maximumAccelerationMetersPerSecondSquared = 8f;
    [SerializeField, Min(0f)] private float hardStopHoldSeconds = 0.75f;
    [SerializeField, Range(0f, 1f)] private float missingSpeedRetentionPerSecond = 0.999f;
    [SerializeField, Range(20f, 100f)] private float motionSampleRateHertz = 50f;

    [Header("Gameplay")]
    [SerializeField, Min(0f)] private float maximumGameSpeed = 10f;
    [SerializeField, Min(0f)] private float jumpVelocity = 8f;
    [SerializeField, Min(0.1f)] private float speedCurveReferenceMetersPerSecond = 30f;
    [SerializeField, Min(0.001f)] private float speedCurveLogFactor = 0.08f;
    [SerializeField, Range(0f, 1f)] private float speedCurveLinearWeight = 0.85f;
    [SerializeField] private AnimationCurve physicalToGameSpeedCurve = CreateDefaultCurve();

    public float PlaybackDelaySeconds => playbackDelaySeconds;
    public float SpeedHistoryDurationSeconds => speedHistoryDurationSeconds;
    public float AccelerationSmoothingTime => accelerationSmoothingTime;
    public float DecelerationSmoothingTime => decelerationSmoothingTime;
    public float DesiredGpsAccuracyMeters => desiredGpsAccuracyMeters;
    public float GpsUpdateDistanceMeters => gpsUpdateDistanceMeters;
    public float GpsInitializationTimeoutSeconds => gpsInitializationTimeoutSeconds;
    public float GpsPollingIntervalSeconds => gpsPollingIntervalSeconds;
    public float MaximumAcceptedHorizontalAccuracyMeters => maximumAcceptedHorizontalAccuracyMeters;
    public float MaximumAcceptedPhysicalSpeedMetersPerSecond => maximumAcceptedPhysicalSpeedMetersPerSecond;
    public float GpsStaleTimeoutSeconds => gpsStaleTimeoutSeconds;
    public float StopThresholdMetersPerSecond => stopThresholdMetersPerSecond;
    public int RequiredConsecutiveLowSpeedReadings => requiredConsecutiveLowSpeedReadings;
    public float EstimatorGpsStaleThresholdSeconds => estimatorGpsStaleThresholdSeconds;
    public float EstimatorGpsMissingThresholdSeconds => estimatorGpsMissingThresholdSeconds;
    public float GpsReturnBlendSeconds => gpsReturnBlendSeconds;
    public float HardBrakingThresholdMetersPerSecondSquared =>
        hardBrakingThresholdMetersPerSecondSquared;
    public float HardBrakingDurationSeconds => hardBrakingDurationSeconds;
    public float AccelerationNoiseDeadZoneMetersPerSecondSquared =>
        accelerationNoiseDeadZoneMetersPerSecondSquared;
    public float HealthyGpsWeight => healthyGpsWeight;
    public float MaximumAccelerationMetersPerSecondSquared =>
        maximumAccelerationMetersPerSecondSquared;
    public float HardStopHoldSeconds => hardStopHoldSeconds;
    public float MissingSpeedRetentionPerSecond => missingSpeedRetentionPerSecond;
    public float MotionSampleRateHertz => motionSampleRateHertz;
    public float MaximumGameSpeed => maximumGameSpeed;
    public float JumpVelocity => jumpVelocity;
    public float SpeedCurveReferenceMetersPerSecond => speedCurveReferenceMetersPerSecond;
    public float SpeedCurveLogFactor => speedCurveLogFactor;
    public float SpeedCurveLinearWeight => speedCurveLinearWeight;
    public AnimationCurve PhysicalToGameSpeedCurve => physicalToGameSpeedCurve;

    public MovementEstimatorSettings CreateMovementEstimatorSettings()
    {
        return CreateMovementEstimatorSettings(
            MovementTuningValues.FromConfiguration(this));
    }

    public MovementEstimatorSettings CreateMovementEstimatorSettings(
        MovementTuningValues tuning)
    {
        return new MovementEstimatorSettings(
            tuning.GpsStaleSeconds,
            tuning.GpsLossFallbackSeconds,
            gpsReturnBlendSeconds,
            tuning.HardBrakingThresholdMetersPerSecondSquared,
            hardBrakingDurationSeconds,
            tuning.AccelerationNoiseFilter,
            tuning.GpsReliance,
            maximumAccelerationMetersPerSecondSquared,
            maximumAcceptedPhysicalSpeedMetersPerSecond,
            hardStopHoldSeconds,
            missingSpeedRetentionPerSecond,
            maximumAcceptedHorizontalAccuracyMeters,
            stopThresholdMetersPerSecond);
    }

    private static AnimationCurve CreateDefaultCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(2f, 2f),
            new Keyframe(5f, 4f),
            new Keyframe(10f, 6f),
            new Keyframe(20f, 8f),
            new Keyframe(30f, 10f),
            new Keyframe(40f, 10f),
            new Keyframe(80f, 10f));
    }
}
