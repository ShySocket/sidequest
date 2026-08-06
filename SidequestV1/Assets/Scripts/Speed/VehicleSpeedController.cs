using UnityEngine;

public sealed class VehicleSpeedController : MonoBehaviour
{
    [SerializeField] private RunnerConfiguration configuration;
    [SerializeField] private MockSpeedProvider mockSpeedProvider;
    [SerializeField] private UnityGpsSpeedProvider unityGpsSpeedProvider;
    [SerializeField] private UnityDeviceMotionProvider deviceMotionProvider;
    [SerializeField] private SpeedProviderMode providerMode = SpeedProviderMode.Auto;

    private IVehicleSpeedProvider activeProvider;
    private DelayedSpeedBuffer delayedSpeedBuffer;
    private VehicleSpeedFilter speedFilter;
    private VehicleSpeedMapper speedMapper;
    private MovementEstimator movementEstimator;
    private MovementTuningPanel movementTuningPanel;

    public float LastKnownPhysicalSpeed { get; private set; }
    public float DelayedPhysicalSpeed { get; private set; }
    public float FilteredPhysicalSpeed { get; private set; }
    public float GameSpeed { get; private set; }
    public bool HasReceivedValidSpeed => ActiveProviderMode == SpeedProviderMode.Gps
        ? movementEstimator != null && movementEstimator.HasSpeedEstimate
        : activeProvider != null && activeProvider.HasReceivedValidSpeed;
    public bool IsTrackingAvailable => ActiveProviderMode == SpeedProviderMode.Gps
        ? movementEstimator != null
            && movementEstimator.GpsHealth == global::GpsHealth.Healthy
        : activeProvider != null && activeProvider.IsTrackingAvailable;
    public bool IsUsingHeldSpeed => ActiveProviderMode == SpeedProviderMode.Gps
        ? movementEstimator != null && movementEstimator.IsDeadReckoning
        : HasReceivedValidSpeed && !IsTrackingAvailable;
    public GpsTrackingState TrackingState
    {
        get
        {
            if (activeProvider == null)
            {
                return GpsTrackingState.SignalLost;
            }

            if (ActiveProviderMode == SpeedProviderMode.Gps
                && movementEstimator != null
                && movementEstimator.HasGpsFix
                && movementEstimator.GpsHealth != global::GpsHealth.Healthy)
            {
                return GpsTrackingState.SignalLost;
            }

            return activeProvider.TrackingState;
        }
    }
    public SpeedProviderMode ActiveProviderMode { get; private set; }
    public int BufferedSampleCount => delayedSpeedBuffer != null ? delayedSpeedBuffer.SampleCount : 0;
    public double LastValidLocalSampleTime { get; private set; } = -1d;
    public GpsHealth GpsHealth => movementEstimator != null
        ? movementEstimator.GpsHealth
        : global::GpsHealth.Missing;
    public bool IsDeadReckoning => movementEstimator != null && movementEstimator.IsDeadReckoning;
    public bool SuddenStopDetected =>
        movementEstimator != null && movementEstimator.SuddenStopDetected;
    public bool IsBraking =>
        movementEstimator != null && movementEstimator.IsBraking;
    public bool IsHardBraking =>
        movementEstimator != null && movementEstimator.IsHardBraking;
    public float EstimatorConfidence =>
        movementEstimator != null ? movementEstimator.Confidence : 0f;
    public float ForwardAccelerationMetersPerSecondSquared =>
        movementEstimator != null ? movementEstimator.ForwardAccelerationMps2 : 0f;
    public float RawGpsSpeedMetersPerSecond => unityGpsSpeedProvider != null
        ? unityGpsSpeedProvider.LastKnownSpeedMetersPerSecond
        : 0f;
    public bool IsStationary =>
        movementEstimator == null || movementEstimator.IsStationary;
    public MovementTuningValues CurrentMovementTuning { get; private set; }

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
            configuration.SpeedCurveReferenceMetersPerSecond,
            configuration.MaximumGameSpeed,
            configuration.SpeedCurveLogFactor,
            configuration.SpeedCurveLinearWeight);
        CurrentMovementTuning = MovementTuningPreferences.Load(configuration);
        movementEstimator = new MovementEstimator(
            configuration.CreateMovementEstimatorSettings(
                CurrentMovementTuning));
        movementTuningPanel = GetComponent<MovementTuningPanel>();
        if (movementTuningPanel == null)
        {
            movementTuningPanel = gameObject.AddComponent<MovementTuningPanel>();
        }

        movementTuningPanel.Initialize(this);
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

        if (ActiveProviderMode == SpeedProviderMode.Gps)
        {
            UpdateEstimatedGpsSpeed();
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

    private void UpdateEstimatedGpsSpeed()
    {
        LastKnownPhysicalSpeed = movementEstimator.Update(
            Time.realtimeSinceStartupAsDouble);
        DelayedPhysicalSpeed = LastKnownPhysicalSpeed;

        // A confirmed stop zeroes the game speed on the same frame instead of
        // letting the smoothing filter bleed the last speed out over time.
        if (movementEstimator.SuddenStopDetected || movementEstimator.IsStationary)
        {
            speedFilter.Reset();
            FilteredPhysicalSpeed = 0f;
            GameSpeed = 0f;
            return;
        }

        FilteredPhysicalSpeed = speedFilter.Update(
            DelayedPhysicalSpeed,
            Time.unscaledDeltaTime,
            movementEstimator.HasSpeedEstimate);
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
        if (ActiveProviderMode == SpeedProviderMode.Gps)
        {
            movementEstimator.Reset();
            StartMotionTracking();
        }

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
        StopMotionTracking();
        activeProvider = null;
    }

    private SpeedProviderMode ResolveProviderMode()
    {
        if (providerMode != SpeedProviderMode.Auto)
        {
            return providerMode;
        }

        if (Application.isEditor)
        {
            return SpeedProviderMode.Mock;
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
        if (ActiveProviderMode == SpeedProviderMode.Gps)
        {
            movementEstimator.AddGpsSample(
                reading.SpeedMetersPerSecond,
                reading.HorizontalAccuracyMeters,
                reading.LocalTimestamp,
                reading.CourseDegrees);
            LastKnownPhysicalSpeed = movementEstimator.EstimatedVehicleSpeedMps;
        }
        else
        {
            LastKnownPhysicalSpeed = reading.SpeedMetersPerSecond;
        }

        delayedSpeedBuffer.AddSample(new TimedSpeedSample(
            reading.LocalTimestamp,
            LastKnownPhysicalSpeed));
    }

    private void StartMotionTracking()
    {
        if (deviceMotionProvider == null && unityGpsSpeedProvider != null)
        {
            deviceMotionProvider =
                unityGpsSpeedProvider.GetComponent<UnityDeviceMotionProvider>();
            if (deviceMotionProvider == null)
            {
                deviceMotionProvider =
                    unityGpsSpeedProvider.gameObject.AddComponent<UnityDeviceMotionProvider>();
            }
        }

        if (deviceMotionProvider == null)
        {
            return;
        }

        deviceMotionProvider.MotionReceived += OnMotionReceived;
        deviceMotionProvider.StartTracking(configuration.MotionSampleRateHertz);
    }

    private void StopMotionTracking()
    {
        if (deviceMotionProvider == null)
        {
            return;
        }

        deviceMotionProvider.MotionReceived -= OnMotionReceived;
        deviceMotionProvider.StopTracking();
    }

    private void OnMotionReceived(DeviceMotionReading reading)
    {
        movementEstimator.AddMotionSample(reading);
    }

    public void ApplyMovementTuning(MovementTuningValues values)
    {
        CurrentMovementTuning = values;
        movementEstimator?.UpdateSettings(
            configuration.CreateMovementEstimatorSettings(values));
    }

    public void SaveMovementTuning()
    {
        MovementTuningPreferences.Save(CurrentMovementTuning);
    }

    public void ResetMovementTuning()
    {
        ApplyMovementTuning(MovementTuningPreferences.Reset(configuration));
    }
}
