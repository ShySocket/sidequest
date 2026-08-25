using NUnit.Framework;
using UnityEngine;

public sealed class VehicleSpeedMapperTests
{
    private VehicleSpeedMapper mapper;

    [SetUp]
    public void SetUp()
    {
        mapper = new VehicleSpeedMapper(new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(10f, 6f),
            new Keyframe(30f, 10f),
            new Keyframe(80f, 10f)), 10f);
    }

    [Test] public void ZeroMapsToZero() => Assert.That(mapper.MapToGameSpeed(0f), Is.Zero);
    [Test] public void NegativeMapsToZero() => Assert.That(mapper.MapToGameSpeed(-1f), Is.Zero);

    [Test]
    public void IncreasingInputDoesNotProduceLowerSpeed()
    {
        float previous = 0f;
        for (int speed = 0; speed <= 80; speed++)
        {
            float mapped = mapper.MapToGameSpeed(speed);
            Assert.That(mapped, Is.GreaterThanOrEqualTo(previous - 0.001f));
            previous = mapped;
        }
    }

    [Test]
    public void MaximumOutputIsClamped()
    {
        VehicleSpeedMapper clamped = new VehicleSpeedMapper(
            AnimationCurve.Linear(0f, 0f, 10f, 100f),
            10f);
        Assert.That(clamped.MapToGameSpeed(10f), Is.EqualTo(10f));
    }

    [Test]
    public void ExpectedKeysProduceExpectedValues()
    {
        Assert.That(mapper.MapToGameSpeed(10f), Is.EqualTo(6f).Within(0.01f));
        Assert.That(mapper.MapToGameSpeed(30f), Is.EqualTo(10f).Within(0.01f));
    }

    [Test]
    public void SoftenedCurveUsesConfiguredLinearAndLogBlend()
    {
        VehicleSpeedMapper softened = new VehicleSpeedMapper(30f, 10f, 0.08f, 0.85f);
        float linear = 10f / 30f;
        float curved = Mathf.Log(1f + 0.08f * 10f)
            / Mathf.Log(1f + 0.08f * 30f);
        float expected = (0.85f * linear + 0.15f * curved) * 10f;

        Assert.That(softened.MapToGameSpeed(10f), Is.EqualTo(expected).Within(0.001f));
        Assert.That(softened.MapToGameSpeed(80f), Is.EqualTo(10f));
    }
}
