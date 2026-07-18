public readonly struct VehicleSpeedReading
{
    public VehicleSpeedReading(
        float speedMetersPerSecond,
        float horizontalAccuracyMeters,
        double localTimestamp)
    {
        SpeedMetersPerSecond = speedMetersPerSecond;
        HorizontalAccuracyMeters = horizontalAccuracyMeters;
        LocalTimestamp = localTimestamp;
    }

    public float SpeedMetersPerSecond { get; }
    public float HorizontalAccuracyMeters { get; }
    public double LocalTimestamp { get; }
}
