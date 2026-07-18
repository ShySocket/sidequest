using UnityEngine;

public sealed class VehicleSpeedMapper
{
    private readonly AnimationCurve speedCurve;
    private readonly float maximumGameSpeed;

    public VehicleSpeedMapper(AnimationCurve speedCurve, float maximumGameSpeed)
    {
        this.speedCurve = speedCurve;
        this.maximumGameSpeed = Mathf.Max(0f, maximumGameSpeed);
    }

    public float MapToGameSpeed(float physicalSpeedMetersPerSecond)
    {
        if (speedCurve == null || physicalSpeedMetersPerSecond <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp(
            speedCurve.Evaluate(physicalSpeedMetersPerSecond),
            0f,
            maximumGameSpeed);
    }
}
