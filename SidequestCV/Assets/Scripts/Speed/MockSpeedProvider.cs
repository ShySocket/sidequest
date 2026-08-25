using System;
using UnityEngine;

public sealed class MockSpeedProvider : MonoBehaviour, IVehicleSpeedProvider
{
    private const float MeaningfulSpeedEpsilon = 0.001f;

    [SerializeField, Range(0f, 40f)]
    private float speedMetersPerSecond;

    [SerializeField]
    private bool trackingAvailable = true;

    private bool isTracking;
    private bool previousTrackingAvailable;
    private float lastEmittedSpeed;

    public bool HasReceivedValidSpeed { get; private set; }
    public bool IsTrackingAvailable => isTracking && trackingAvailable;
    public float LastKnownSpeedMetersPerSecond { get; private set; }
    public GpsTrackingState TrackingState { get; private set; } = GpsTrackingState.Initializing;
    public event Action<VehicleSpeedReading> ValidSpeedReceived;

    private void Update()
    {
        if (!isTracking)
        {
            return;
        }

        if (!trackingAvailable)
        {
            previousTrackingAvailable = false;
            TrackingState = GpsTrackingState.SignalLost;
            return;
        }

        TrackingState = GpsTrackingState.Tracking;
        bool trackingJustReturned = !previousTrackingAvailable;
        previousTrackingAvailable = true;

        if (trackingJustReturned || Mathf.Abs(speedMetersPerSecond - lastEmittedSpeed) > MeaningfulSpeedEpsilon)
        {
            EmitCurrentSpeed();
        }
    }

    public void StartTracking()
    {
        if (isTracking)
        {
            return;
        }

        isTracking = true;
        previousTrackingAvailable = trackingAvailable;
        TrackingState = trackingAvailable ? GpsTrackingState.Tracking : GpsTrackingState.SignalLost;
        if (trackingAvailable)
        {
            EmitCurrentSpeed();
        }
    }

    public void StopTracking()
    {
        isTracking = false;
        previousTrackingAvailable = false;
        TrackingState = GpsTrackingState.SignalLost;
    }

    private void EmitCurrentSpeed()
    {
        lastEmittedSpeed = speedMetersPerSecond;
        LastKnownSpeedMetersPerSecond = speedMetersPerSecond;
        HasReceivedValidSpeed = true;
        ValidSpeedReceived?.Invoke(new VehicleSpeedReading(
            speedMetersPerSecond,
            0f,
            Time.realtimeSinceStartupAsDouble));
    }
}
