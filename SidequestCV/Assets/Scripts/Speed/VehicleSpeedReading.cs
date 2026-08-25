public readonly struct VehicleSpeedReading
{
    public VehicleSpeedReading(
        float speedMetersPerSecond,
        float horizontalAccuracyMeters,
        double localTimestamp,
        float courseDegrees = float.NaN)
    {
        SpeedMetersPerSecond = speedMetersPerSecond;
        HorizontalAccuracyMeters = horizontalAccuracyMeters;
        LocalTimestamp = localTimestamp;
        CourseDegrees = courseDegrees;
    }

    public float SpeedMetersPerSecond { get; }
    public float HorizontalAccuracyMeters { get; }
    public double LocalTimestamp { get; }
    public float CourseDegrees { get; }
}
