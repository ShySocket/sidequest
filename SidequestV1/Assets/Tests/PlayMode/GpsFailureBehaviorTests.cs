using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class GpsFailureBehaviorTests
{
    [UnityTest]
    public IEnumerator MockSignalLossHoldsSpeedAndRecoveryUsesNewReading()
    {
        GameObject root = new GameObject("GpsFailureTestRoot");
        root.SetActive(false);
        VehicleSpeedController controller = PlayModeTestFactory.CreateController(root, 10f, out MockSpeedProvider mock, out _);
        root.SetActive(true);

        yield return new WaitForSecondsRealtime(1.5f);
        float initialGameSpeed = controller.GameSpeed;
        Assert.That(initialGameSpeed, Is.GreaterThan(0f));
        Assert.That(mock.LastKnownSpeedMetersPerSecond, Is.EqualTo(10f));

        PlayModeTestFactory.SetField(mock, "trackingAvailable", false);
        yield return null;
        PlayModeTestFactory.SetField(mock, "speedMetersPerSecond", 20f);
        yield return new WaitForSecondsRealtime(1.25f);
        Assert.That(mock.LastKnownSpeedMetersPerSecond, Is.EqualTo(10f));
        Assert.That(controller.IsUsingHeldSpeed, Is.True);
        Assert.That(controller.GameSpeed, Is.GreaterThan(0f));

        PlayModeTestFactory.SetField(mock, "trackingAvailable", true);
        yield return new WaitForSecondsRealtime(1.5f);
        Assert.That(mock.LastKnownSpeedMetersPerSecond, Is.EqualTo(20f));
        Assert.That(controller.IsUsingHeldSpeed, Is.False);
        Assert.That(controller.GameSpeed, Is.GreaterThan(initialGameSpeed));

        Object.Destroy(root);
    }
}
