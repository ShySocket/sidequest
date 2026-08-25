using System;
using UnityEngine;

public sealed class VehicleSpeedFilter
{
    private const float MinimumSmoothingTime = 0.0001f;
    private readonly float accelerationSmoothingTime;
    private readonly float decelerationSmoothingTime;

    public VehicleSpeedFilter(float accelerationSmoothingTime, float decelerationSmoothingTime)
    {
        this.accelerationSmoothingTime = Mathf.Max(0f, accelerationSmoothingTime);
        this.decelerationSmoothingTime = Mathf.Max(0f, decelerationSmoothingTime);
    }

    public float CurrentSpeed { get; private set; }

    public float Update(float targetSpeed, float deltaTime, bool hasValidSpeed)
    {
        if (!hasValidSpeed)
        {
            CurrentSpeed = 0f;
            return CurrentSpeed;
        }

        targetSpeed = Mathf.Max(0f, targetSpeed);
        float smoothingTime = targetSpeed > CurrentSpeed
            ? accelerationSmoothingTime
            : decelerationSmoothingTime;

        if (smoothingTime <= MinimumSmoothingTime || deltaTime <= 0f)
        {
            CurrentSpeed = targetSpeed;
            return CurrentSpeed;
        }

        float alpha = 1f - (float)Math.Exp(-deltaTime / smoothingTime);
        CurrentSpeed = Mathf.Lerp(CurrentSpeed, targetSpeed, alpha);
        return CurrentSpeed;
    }

    public void Reset()
    {
        CurrentSpeed = 0f;
    }
}
