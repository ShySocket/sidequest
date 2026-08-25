using UnityEngine;

public sealed class VehicleSpeedMapper
{
    private readonly AnimationCurve speedCurve;
    private readonly float maximumGameSpeed;
    private readonly float referenceSpeedMetersPerSecond;
    private readonly float logFactor;
    private readonly float linearWeight;
    private readonly bool usesSoftenedCurve;

    public VehicleSpeedMapper(AnimationCurve speedCurve, float maximumGameSpeed)
    {
        this.speedCurve = speedCurve;
        this.maximumGameSpeed = Mathf.Max(0f, maximumGameSpeed);
    }

    public VehicleSpeedMapper(
        float referenceSpeedMetersPerSecond,
        float maximumGameSpeed,
        float logFactor,
        float linearWeight)
    {
        this.referenceSpeedMetersPerSecond = Mathf.Max(
            0.1f,
            referenceSpeedMetersPerSecond);
        this.maximumGameSpeed = Mathf.Max(0f, maximumGameSpeed);
        this.logFactor = Mathf.Max(0.001f, logFactor);
        this.linearWeight = Mathf.Clamp01(linearWeight);
        usesSoftenedCurve = true;
    }

    public float MapToGameSpeed(float physicalSpeedMetersPerSecond)
    {
        if (physicalSpeedMetersPerSecond <= 0f)
        {
            return 0f;
        }

        if (usesSoftenedCurve)
        {
            float linear = physicalSpeedMetersPerSecond / referenceSpeedMetersPerSecond;
            float curved = Mathf.Log(1f + logFactor * physicalSpeedMetersPerSecond)
                / Mathf.Log(1f + logFactor * referenceSpeedMetersPerSecond);
            float normalized = linearWeight * linear
                + (1f - linearWeight) * curved;
            return Mathf.Clamp(normalized * maximumGameSpeed, 0f, maximumGameSpeed);
        }

        if (speedCurve == null)
        {
            return 0f;
        }

        return Mathf.Clamp(
            speedCurve.Evaluate(physicalSpeedMetersPerSecond),
            0f,
            maximumGameSpeed);
    }
}
