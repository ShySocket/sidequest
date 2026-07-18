using NUnit.Framework;

public sealed class DelayedSpeedBufferTests
{
    [Test]
    public void EmptyBufferReturnsFallback()
    {
        DelayedSpeedBuffer buffer = new DelayedSpeedBuffer(1f, 5f);
        Assert.That(buffer.GetDelayedSpeed(10d, 7f), Is.EqualTo(7f));
    }

    [Test]
    public void ExactDelayedSampleIsReturned()
    {
        DelayedSpeedBuffer buffer = CreateBufferWithSamples();
        Assert.That(buffer.GetDelayedSpeed(3d, 0f), Is.EqualTo(10f));
    }

    [Test]
    public void InterpolationBetweenSamplesWorks()
    {
        DelayedSpeedBuffer buffer = CreateBufferWithSamples();
        Assert.That(buffer.GetDelayedSpeed(2.5d, 0f), Is.EqualTo(5f).Within(0.001f));
    }

    [Test]
    public void NewestSampleIsReturnedWhenTargetIsNewer()
    {
        DelayedSpeedBuffer buffer = CreateBufferWithSamples();
        Assert.That(buffer.GetDelayedSpeed(20d, 0f), Is.EqualTo(20f));
    }

    [Test]
    public void OldSamplesAreRemovedWhileNeighborIsRetained()
    {
        DelayedSpeedBuffer buffer = new DelayedSpeedBuffer(1f, 2f);
        buffer.AddSample(new TimedSpeedSample(0d, 0f));
        buffer.AddSample(new TimedSpeedSample(1d, 1f));
        buffer.AddSample(new TimedSpeedSample(4d, 4f));
        Assert.That(buffer.SampleCount, Is.EqualTo(2));
    }

    [Test]
    public void OutOfOrderSamplesDoNotCorruptHistory()
    {
        DelayedSpeedBuffer buffer = CreateBufferWithSamples();
        buffer.AddSample(new TimedSpeedSample(1.5d, 99f));
        Assert.That(buffer.SampleCount, Is.EqualTo(3));
        Assert.That(buffer.GetDelayedSpeed(2.5d, 0f), Is.EqualTo(5f).Within(0.001f));
    }

    [Test]
    public void SampleCountRemainsBoundedOverTime()
    {
        DelayedSpeedBuffer buffer = new DelayedSpeedBuffer(1f, 5f);
        for (int i = 0; i < 1000; i++)
        {
            buffer.AddSample(new TimedSpeedSample(i * 0.25d, i));
        }

        Assert.That(buffer.SampleCount, Is.LessThanOrEqualTo(22));
    }

    private static DelayedSpeedBuffer CreateBufferWithSamples()
    {
        DelayedSpeedBuffer buffer = new DelayedSpeedBuffer(1f, 5f);
        buffer.AddSample(new TimedSpeedSample(1d, 0f));
        buffer.AddSample(new TimedSpeedSample(2d, 10f));
        buffer.AddSample(new TimedSpeedSample(3d, 20f));
        return buffer;
    }
}
