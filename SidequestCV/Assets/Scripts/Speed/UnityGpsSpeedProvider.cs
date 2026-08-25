using System;
using System.Collections;
using UnityEngine;

public sealed class UnityGpsSpeedProvider : MonoBehaviour, IVehicleSpeedProvider
{
    [SerializeField] private RunnerConfiguration configuration;
    [SerializeField] private LocationPermissionService permissionService;

    private Coroutine trackingCoroutine;
    private bool stopRequested;
    private readonly GpsSpeedWindow speedWindow = new GpsSpeedWindow();
    private bool hasInitialCoordinate;
    private double lastProcessedGpsTimestamp = double.MinValue;
    private double lastFreshLocalTime = -1d;
    private int consecutiveLowSpeedReadings;

    public bool HasReceivedValidSpeed { get; private set; }
    public bool IsTrackingAvailable { get; private set; }
    public float LastKnownSpeedMetersPerSecond { get; private set; }
    public GpsTrackingState TrackingState { get; private set; } = GpsTrackingState.Initializing;
    public double LastLatitude { get; private set; }
    public double LastLongitude { get; private set; }
    public float LastHorizontalAccuracy { get; private set; }
    public float LastCourseDegrees { get; private set; } = float.NaN;
    public double LastValidLocalSampleTime { get; private set; } = -1d;
    public event Action<VehicleSpeedReading> ValidSpeedReceived;

    private void Awake()
    {
        if (configuration == null || permissionService == null)
        {
            Debug.LogError(
                $"{nameof(UnityGpsSpeedProvider)} on {name} requires configuration and permission service references.",
                this);
            enabled = false;
        }
    }

    public void StartTracking()
    {
        if (!enabled || trackingCoroutine != null)
        {
            return;
        }

        stopRequested = false;
        ResetTrackingState();
        trackingCoroutine = StartCoroutine(TrackLocation());
    }

    public void StopTracking()
    {
        stopRequested = true;
        if (trackingCoroutine != null)
        {
            StopCoroutine(trackingCoroutine);
            trackingCoroutine = null;
        }

#if UNITY_IOS || UNITY_ANDROID
        if (Input.location.status == LocationServiceStatus.Running)
        {
            Input.location.Stop();
        }
#endif
        IsTrackingAvailable = false;
    }

    private void OnDisable()
    {
        StopTracking();
    }

    private IEnumerator TrackLocation()
    {
#if UNITY_EDITOR
        if (stopRequested)
        {
            trackingCoroutine = null;
            yield break;
        }

        TrackingState = GpsTrackingState.LocationServicesDisabled;
        IsTrackingAvailable = false;
        trackingCoroutine = null;
        yield break;
#else
        TrackingState = GpsTrackingState.WaitingForPermission;
        bool permissionGranted = false;
        yield return permissionService.RequestPermission(granted => permissionGranted = granted);
        if (!permissionGranted || stopRequested)
        {
            TrackingState = GpsTrackingState.PermissionDenied;
            IsTrackingAvailable = false;
            trackingCoroutine = null;
            yield break;
        }

        // Do not bail out when isEnabledByUser is false: on iOS it stays
        // false until the user answers the system dialog, and that dialog is
        // only presented by Start(). Starting is what asks the question.
        TrackingState = GpsTrackingState.Initializing;
        Input.location.Start(
            configuration.DesiredGpsAccuracyMeters,
            configuration.GpsUpdateDistanceMeters);

        double initializationStart = Time.realtimeSinceStartupAsDouble;
        while (Input.location.status == LocationServiceStatus.Initializing
            && Time.realtimeSinceStartupAsDouble - initializationStart
                < configuration.GpsInitializationTimeoutSeconds)
        {
            yield return null;
        }

        if (Input.location.status != LocationServiceStatus.Running || stopRequested)
        {
            TrackingState = Input.location.isEnabledByUser
                ? GpsTrackingState.SignalLost
                : GpsTrackingState.LocationServicesDisabled;
            IsTrackingAvailable = false;
            Input.location.Stop();
            trackingCoroutine = null;
            yield break;
        }

        TrackingState = GpsTrackingState.WaitingForFirstFix;
        WaitForSecondsRealtime pollWait = new WaitForSecondsRealtime(configuration.GpsPollingIntervalSeconds);
        while (!stopRequested)
        {
            ProcessLocation(Input.location.lastData);
            DetectSignalLoss();
            yield return pollWait;
        }

        Input.location.Stop();
        trackingCoroutine = null;
#endif
    }

    private void ProcessLocation(LocationInfo location)
    {
        double gpsTimestamp = location.timestamp;
        if (gpsTimestamp <= lastProcessedGpsTimestamp)
        {
            return;
        }

        lastProcessedGpsTimestamp = gpsTimestamp;
        double latitude = location.latitude;
        double longitude = location.longitude;
        float accuracy = location.horizontalAccuracy;
        if (latitude < -90d || latitude > 90d
            || longitude < -180d || longitude > 180d
            || accuracy < 0f
            || accuracy > configuration.MaximumAcceptedHorizontalAccuracyMeters)
        {
            return;
        }

        double localTime = Time.realtimeSinceStartupAsDouble;
        LastLatitude = latitude;
        LastLongitude = longitude;
        LastHorizontalAccuracy = accuracy;
        lastFreshLocalTime = localTime;

        if (!hasInitialCoordinate)
        {
            hasInitialCoordinate = true;
            speedWindow.AddPosition(
                latitude,
                longitude,
                gpsTimestamp,
                out _,
                out _);
            AcceptSpeed(0f, accuracy, localTime);
            return;
        }

        if (!speedWindow.AddPosition(
            latitude,
            longitude,
            gpsTimestamp,
            out float calculatedSpeed,
            out float courseDegrees))
        {
            return;
        }

        if (float.IsNaN(calculatedSpeed)
            || float.IsInfinity(calculatedSpeed)
            || calculatedSpeed < 0f
            || calculatedSpeed
                > configuration.MaximumAcceptedPhysicalSpeedMetersPerSecond)
        {
            // Do not let a large multipath jump poison the next several
            // windowed estimates. Restart from the current coordinate.
            speedWindow.Reset();
            speedWindow.AddPosition(
                latitude,
                longitude,
                gpsTimestamp,
                out _,
                out _);
            return;
        }

        // Every windowed estimate is forwarded immediately. Holding low-speed
        // readings back until several arrive in a row added seconds of stop
        // latency, and the movement estimator already rejects isolated jitter
        // in both directions.
        float acceptedSpeed = calculatedSpeed;
        if (acceptedSpeed < configuration.StopThresholdMetersPerSecond)
        {
            consecutiveLowSpeedReadings++;
            if (consecutiveLowSpeedReadings
                >= configuration.RequiredConsecutiveLowSpeedReadings)
            {
                acceptedSpeed = 0f;
            }
        }
        else
        {
            consecutiveLowSpeedReadings = 0;
        }

        AcceptSpeed(acceptedSpeed, accuracy, localTime, courseDegrees);
    }

    private void ResetTrackingState()
    {
        speedWindow.Reset();
        hasInitialCoordinate = false;
        lastProcessedGpsTimestamp = double.MinValue;
        lastFreshLocalTime = -1d;
        consecutiveLowSpeedReadings = 0;
        HasReceivedValidSpeed = false;
        IsTrackingAvailable = false;
        LastKnownSpeedMetersPerSecond = 0f;
        LastCourseDegrees = float.NaN;
        LastValidLocalSampleTime = -1d;
    }

    private void AcceptSpeed(
        float speed,
        float accuracy,
        double localTime,
        float courseDegrees = float.NaN)
    {
        HasReceivedValidSpeed = true;
        IsTrackingAvailable = true;
        TrackingState = GpsTrackingState.Tracking;
        LastKnownSpeedMetersPerSecond = speed;
        LastValidLocalSampleTime = localTime;
        LastCourseDegrees = courseDegrees;
        lastFreshLocalTime = localTime;
        ValidSpeedReceived?.Invoke(new VehicleSpeedReading(
            speed,
            accuracy,
            localTime,
            courseDegrees));
    }

    private void DetectSignalLoss()
    {
        if (lastFreshLocalTime < 0d)
        {
            return;
        }

        if (Time.realtimeSinceStartupAsDouble - lastFreshLocalTime
            < configuration.GpsStaleTimeoutSeconds)
        {
            return;
        }

        IsTrackingAvailable = false;
        TrackingState = GpsTrackingState.SignalLost;
    }
}
