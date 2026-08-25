using System.Collections.Generic;
using UnityEngine;

public sealed class DelayedSpeedBuffer
{
    private readonly double playbackDelaySeconds;
    private readonly double historyDurationSeconds;
    private readonly List<TimedSpeedSample> samples = new List<TimedSpeedSample>(32);

    public DelayedSpeedBuffer(float playbackDelaySeconds, float historyDurationSeconds)
    {
        this.playbackDelaySeconds = Mathf.Max(0f, playbackDelaySeconds);
        this.historyDurationSeconds = Mathf.Max(0.1f, historyDurationSeconds);
    }

    public int SampleCount => samples.Count;

    public void AddSample(TimedSpeedSample sample)
    {
        if (samples.Count > 0 && sample.LocalTimestamp <= samples[samples.Count - 1].LocalTimestamp)
        {
            return;
        }

        samples.Add(sample);
        TrimHistory(sample.LocalTimestamp);
    }

    public float GetDelayedSpeed(double currentLocalTime, float fallbackLastKnownSpeed)
    {
        if (samples.Count == 0)
        {
            return Mathf.Max(0f, fallbackLastKnownSpeed);
        }

        double targetTime = currentLocalTime - playbackDelaySeconds;
        if (targetTime <= samples[0].LocalTimestamp)
        {
            return samples[0].SpeedMetersPerSecond;
        }

        int newestIndex = samples.Count - 1;
        if (targetTime >= samples[newestIndex].LocalTimestamp)
        {
            return samples[newestIndex].SpeedMetersPerSecond;
        }

        for (int i = 1; i < samples.Count; i++)
        {
            TimedSpeedSample after = samples[i];
            if (after.LocalTimestamp < targetTime)
            {
                continue;
            }

            TimedSpeedSample before = samples[i - 1];
            double duration = after.LocalTimestamp - before.LocalTimestamp;
            if (duration <= double.Epsilon)
            {
                return after.SpeedMetersPerSecond;
            }

            float t = (float)((targetTime - before.LocalTimestamp) / duration);
            return Mathf.Lerp(before.SpeedMetersPerSecond, after.SpeedMetersPerSecond, t);
        }

        return samples[newestIndex].SpeedMetersPerSecond;
    }

    public void Clear()
    {
        samples.Clear();
    }

    private void TrimHistory(double newestTimestamp)
    {
        double cutoff = newestTimestamp - historyDurationSeconds;
        int removeCount = 0;
        while (removeCount + 1 < samples.Count && samples[removeCount + 1].LocalTimestamp < cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            samples.RemoveRange(0, removeCount);
        }
    }
}
