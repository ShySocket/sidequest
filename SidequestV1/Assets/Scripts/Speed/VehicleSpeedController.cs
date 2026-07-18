using UnityEngine;

public sealed class VehicleSpeedController : MonoBehaviour
{
    [SerializeField] private RunnerConfiguration configuration;
    [SerializeField] private MockSpeedProvider mockSpeedProvider;
    [SerializeField] private UnityGpsSpeedProvider unityGpsSpeedProvider;
    [SerializeField] private SpeedProviderMode providerMode = SpeedProviderMode.Auto;

    private IVehicleSpeedProvider activeProvider;
    private DelayedSpeedBuffer delayedSpeedBuffer;
    private VehicleSpeedFilter speedFilter;
    private VehicleSpeedMapper speedMapper;

    public float LastKnownPhysicalSpeed { get; private set; }
    public float DelayedPhysicalSpeed { get; private set; }
    public float FilteredPhysicalSpeed { get; private set; }
    public float GameSpeed { get; private set; }
    public bool HasReceivedValidSpeed => activeProvider != null && activeProvider.HasReceivedValidSpeed;
    public bool IsTrackingAvailable => activeProvider != null && activeProvider.IsTrackingAvailable;
    public bool IsUsingHeldSpeed => HasReceivedValidSpeed && !IsTrackingAvailable;
    public GpsTrackingState TrackingState => activeProvider != null
        ? activeProvider.TrackingState
        : GpsTrackingState.SignalLost;
    public SpeedProviderMode ActiveProviderMode { get; private set; }
    public int BufferedSampleCount => delayedSpeedBuffer != null ? delayedSpeedBuffer.SampleCount : 0;
    public double LastValidLocalSampleTime { get; private set; } = -1d;

    private void Awake()
    {
        if (configuration == null)
        {
            Debug.LogError($"{nameof(VehicleSpeedController)} on {name} requires a RunnerConfiguration.", this);
            enabled = false;
            return;
        }

        delayedSpeedBuffer = new DelayedSpeedBuffer(
            configuration.PlaybackDelaySeconds,
            configuration.SpeedHistoryDurationSeconds);
        speedFilter = new VehicleSpeedFilter(
            configuration.AccelerationSmoothingTime,
            configuration.DecelerationSmoothingTime);
        speedMapper = new VehicleSpeedMapper(
            configuration.PhysicalToGameSpeedCurve,
            configuration.MaximumGameSpeed);
    }

    private void OnEnable()
    {
        if (configuration == null)
        {
            return;
        }

        SelectAndStartProvider();
    }

    private void OnDisable()
    {
        StopActiveProvider();
    }

    private void Update()
    {
        if (activeProvider == null)
        {
            GameSpeed = 0f;
            return;
        }

        LastKnownPhysicalSpeed = activeProvider.LastKnownSpeedMetersPerSecond;
        DelayedPhysicalSpeed = delayedSpeedBuffer.GetDelayedSpeed(
            Time.realtimeSinceStartupAsDouble,
            LastKnownPhysicalSpeed);
        FilteredPhysicalSpeed = speedFilter.Update(
            DelayedPhysicalSpeed,
            Time.unscaledDeltaTime,
            activeProvider.HasReceivedValidSpeed);
        GameSpeed = speedMapper.MapToGameSpeed(FilteredPhysicalSpeed);
    }

    private void SelectAndStartProvider()
    {
        StopActiveProvider();
        ActiveProviderMode = ResolveProviderMode();
        activeProvider = ActiveProviderMode == SpeedProviderMode.Gps
            ? unityGpsSpeedProvider
            : mockSpeedProvider;

        if (activeProvider == null)
        {
            Debug.LogError(
                $"{nameof(VehicleSpeedController)} on {name} has no provider assigned for {ActiveProviderMode} mode.",
                this);
            enabled = false;
            return;
        }

        activeProvider.ValidSpeedReceived += OnValidSpeedReceived;
        activeProvider.StartTracking();
    }

    private void StopActiveProvider()
    {
        if (activeProvider == null)
        {
            return;
        }

        activeProvider.ValidSpeedReceived -= OnValidSpeedReceived;
        activeProvider.StopTracking();
        activeProvider = null;
    }

    private SpeedProviderMode ResolveProviderMode()
    {
        if (providerMode != SpeedProviderMode.Auto)
        {
            return providerMode;
        }

#if UNITY_IOS || UNITY_ANDROID
        return SpeedProviderMode.Gps;
#else
        return SpeedProviderMode.Mock;
#endif
    }

    private void OnValidSpeedReceived(VehicleSpeedReading reading)
    {
        LastValidLocalSampleTime = reading.LocalTimestamp;
        LastKnownPhysicalSpeed = reading.SpeedMetersPerSecond;
        delayedSpeedBuffer.AddSample(new TimedSpeedSample(
            reading.LocalTimestamp,
            reading.SpeedMetersPerSecond));
    }
}
