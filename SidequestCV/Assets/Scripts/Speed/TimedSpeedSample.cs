public readonly struct TimedSpeedSample
{
    public TimedSpeedSample(double localTimestamp, float speedMetersPerSecond)
    {
        LocalTimestamp = localTimestamp;
        SpeedMetersPerSecond = speedMetersPerSecond;
    }

    public double LocalTimestamp { get; }
    public float SpeedMetersPerSecond { get; }
}
