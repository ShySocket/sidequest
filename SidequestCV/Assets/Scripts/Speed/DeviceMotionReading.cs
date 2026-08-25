using UnityEngine;

public readonly struct DeviceMotionReading
{
    public DeviceMotionReading(
        Vector3 userAccelerationMetersPerSecondSquared,
        Quaternion attitude,
        double localTimestamp)
    {
        UserAccelerationMetersPerSecondSquared = userAccelerationMetersPerSecondSquared;
        Attitude = attitude;
        LocalTimestamp = localTimestamp;
    }

    public Vector3 UserAccelerationMetersPerSecondSquared { get; }
    public Quaternion Attitude { get; }
    public double LocalTimestamp { get; }
}
