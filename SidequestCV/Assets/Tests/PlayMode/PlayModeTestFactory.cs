using System.Reflection;
using UnityEngine;

internal static class PlayModeTestFactory
{
    internal static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.SetValue(target, value);
    }

    internal static VehicleSpeedController CreateController(
        GameObject root,
        float initialSpeed,
        out MockSpeedProvider mock,
        out UnityGpsSpeedProvider gps)
    {
        RunnerConfiguration configuration = ScriptableObject.CreateInstance<RunnerConfiguration>();
        LocationPermissionService permission = root.AddComponent<LocationPermissionService>();
        mock = root.AddComponent<MockSpeedProvider>();
        SetField(mock, "speedMetersPerSecond", initialSpeed);
        gps = root.AddComponent<UnityGpsSpeedProvider>();
        SetField(gps, "configuration", configuration);
        SetField(gps, "permissionService", permission);
        VehicleSpeedController controller = root.AddComponent<VehicleSpeedController>();
        SetField(controller, "configuration", configuration);
        SetField(controller, "mockSpeedProvider", mock);
        SetField(controller, "unityGpsSpeedProvider", gps);
        SetField(controller, "providerMode", SpeedProviderMode.Mock);
        return controller;
    }
}
