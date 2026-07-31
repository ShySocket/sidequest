using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class UnityDeviceMotionProvider : MonoBehaviour
{
    private const float GravityMetersPerSecondSquared = 9.80665f;
    private const float DefaultSampleRateHertz = 50f;

    private bool isTracking;

    public bool IsAvailable => LinearAccelerationSensor.current != null;
    public event Action<DeviceMotionReading> MotionReceived;

    public void StartTracking(float sampleRateHertz = DefaultSampleRateHertz)
    {
        if (isTracking)
        {
            return;
        }

        isTracking = true;
        if (LinearAccelerationSensor.current != null)
        {
            LinearAccelerationSensor.current.samplingFrequency =
                Mathf.Max(1f, sampleRateHertz);
            InputSystem.EnableDevice(LinearAccelerationSensor.current);
        }

        if (AttitudeSensor.current != null)
        {
            AttitudeSensor.current.samplingFrequency =
                Mathf.Max(1f, sampleRateHertz);
            InputSystem.EnableDevice(AttitudeSensor.current);
        }
    }

    public void StopTracking()
    {
        if (!isTracking)
        {
            return;
        }

        isTracking = false;
        if (LinearAccelerationSensor.current != null)
        {
            InputSystem.DisableDevice(LinearAccelerationSensor.current);
        }

        if (AttitudeSensor.current != null)
        {
            InputSystem.DisableDevice(AttitudeSensor.current);
        }
    }

    private void Update()
    {
        if (!isTracking || LinearAccelerationSensor.current == null)
        {
            return;
        }

        Vector3 userAcceleration =
            LinearAccelerationSensor.current.acceleration.ReadValue()
            * GravityMetersPerSecondSquared;
        Quaternion attitude = AttitudeSensor.current != null
            ? AttitudeSensor.current.attitude.ReadValue()
            : Quaternion.identity;
        MotionReceived?.Invoke(new DeviceMotionReading(
            userAcceleration,
            attitude,
            Time.realtimeSinceStartupAsDouble));
    }

    private void OnDisable()
    {
        StopTracking();
    }
}
