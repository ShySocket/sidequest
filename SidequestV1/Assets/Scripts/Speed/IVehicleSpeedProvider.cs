public interface IVehicleSpeedProvider
{
    bool HasReceivedValidSpeed { get; }
    bool IsTrackingAvailable { get; }
    float LastKnownSpeedMetersPerSecond { get; }
    GpsTrackingState TrackingState { get; }
    event System.Action<VehicleSpeedReading> ValidSpeedReceived;
    void StartTracking();
    void StopTracking();
}
