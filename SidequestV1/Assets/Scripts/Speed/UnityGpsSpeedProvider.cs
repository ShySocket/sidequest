using System;
using System.Collections;
using UnityEngine;

public sealed class UnityGpsSpeedProvider : MonoBehaviour, IVehicleSpeedProvider
{
    [SerializeField] private RunnerConfiguration configuration;
    [SerializeField] private LocationPermissionService permissionService;

    private Coroutine trackingCoroutine;
    private bool stopRequested;
    private bool hasPreviousCoordinate;
    private double previousLatitude;
    private double previousLongitude;
    private double previousGpsTimestamp;
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

        if (!Input.location.isEnabledByUser)
        {
            TrackingState = GpsTrackingState.LocationServicesDisabled;
            IsTrackingAvailable = false;
            trackingCoroutine = null;
            yield break;
        }

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
            TrackingState = GpsTrackingState.SignalLost;
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

        if (!hasPreviousCoordinate)
        {
            StorePreviousCoordinate(latitude, longitude, gpsTimestamp);
            TrackingState = GpsTrackingState.WaitingForFirstFix;
            return;
        }

        double elapsedGpsSeconds = gpsTimestamp - previousGpsTimestamp;
        if (elapsedGpsSeconds <= 0d)
        {
            return;
        }

        double distanceMeters = GeoDistanceCalculator.DistanceMeters(
            previousLatitude,
            previousLongitude,
            latitude,
            longitude);
        double calculatedSpeed = distanceMeters / elapsedGpsSeconds;
        StorePreviousCoordinate(latitude, longitude, gpsTimestamp);

        if (double.IsNaN(calculatedSpeed)
            || double.IsInfinity(calculatedSpeed)
            || calculatedSpeed < 0d
            || calculatedSpeed > configuration.MaximumAcceptedPhysicalSpeedMetersPerSecond)
        {
            return;
        }

        float acceptedSpeed = (float)calculatedSpeed;
        if (acceptedSpeed < configuration.StopThresholdMetersPerSecond)
        {
            consecutiveLowSpeedReadings++;
            if (consecutiveLowSpeedReadings < configuration.RequiredConsecutiveLowSpeedReadings)
            {
                return;
            }

            acceptedSpeed = 0f;
        }
        else
        {
            consecutiveLowSpeedReadings = 0;
        }

        AcceptSpeed(acceptedSpeed, accuracy, localTime);
    }

    private void StorePreviousCoordinate(double latitude, double longitude, double gpsTimestamp)
    {
        previousLatitude = latitude;
        previousLongitude = longitude;
        previousGpsTimestamp = gpsTimestamp;
        hasPreviousCoordinate = true;
    }

    private void AcceptSpeed(float speed, float accuracy, double localTime)
    {
        HasReceivedValidSpeed = true;
        IsTrackingAvailable = true;
        TrackingState = GpsTrackingState.Tracking;
        LastKnownSpeedMetersPerSecond = speed;
        LastValidLocalSampleTime = localTime;
        lastFreshLocalTime = localTime;
        ValidSpeedReceived?.Invoke(new VehicleSpeedReading(speed, accuracy, localTime));
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
