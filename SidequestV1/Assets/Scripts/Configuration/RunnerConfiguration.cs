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
    [SerializeField, Min(0f)] private float desiredGpsAccuracyMeters = 5f;
    [SerializeField, Min(0f)] private float gpsUpdateDistanceMeters = 1f;
    [SerializeField, Min(0.1f)] private float gpsInitializationTimeoutSeconds = 20f;
    [SerializeField, Min(0.05f)] private float gpsPollingIntervalSeconds = 0.25f;
    [SerializeField, Min(0f)] private float maximumAcceptedHorizontalAccuracyMeters = 25f;
    [SerializeField, Min(0f)] private float maximumAcceptedPhysicalSpeedMetersPerSecond = 80f;
    [SerializeField, Min(0.1f)] private float gpsStaleTimeoutSeconds = 3f;
    [SerializeField, Min(0f)] private float stopThresholdMetersPerSecond = 0.75f;
    [SerializeField, Min(1)] private int requiredConsecutiveLowSpeedReadings = 3;

    [Header("Gameplay")]
    [SerializeField, Min(0f)] private float maximumGameSpeed = 10f;
    [SerializeField, Min(0f)] private float jumpVelocity = 8f;
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
    public float MaximumGameSpeed => maximumGameSpeed;
    public float JumpVelocity => jumpVelocity;
    public AnimationCurve PhysicalToGameSpeedCurve => physicalToGameSpeedCurve;

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
