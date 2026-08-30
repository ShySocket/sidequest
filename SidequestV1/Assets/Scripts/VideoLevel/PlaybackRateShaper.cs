using System;
using UnityEngine;

/// <summary>
/// Turns the raw vehicle-derived playback rate into the rate the video is
/// actually driven at.
/// </summary>
/// <remarks>
/// The vehicle speed signal is allowed to be abrupt - a confirmed stop zeroes
/// it on a single frame - because gameplay wants the truth fast. The picture
/// does not: a rate that jumps reads as the video stuttering. This class owns
/// the difference. It eases toward the target asymmetrically (slowing is kept
/// quick so a real brake is felt within the second; speeding up ramps more
/// gently, where jitter would otherwise flutter the footage), snaps fully to
/// zero once a stop has visibly landed so the video pauses cleanly instead of
/// crawling at a rate no decoder plays smoothly, and refuses to reach zero at
/// all while a jump or dodge is mid-flight - those arcs advance on video time,
/// so a frozen video would hang the ball in the air. The floor carries the
/// action to its authored landing, and only then may the world stop.
/// </remarks>
public sealed class PlaybackRateShaper
{
    // Below this target the vehicle is considered stopped for display
    // purposes, and once the eased rate has decayed this low the remaining
    // motion is imperceptible - snapping to zero here is what turns "slowing
    // down" into a clean freeze-frame rather than an endless crawl.
    private const float StopTargetEpsilon = 0.001f;
    private const float SnapToZeroRate = 0.05f;

    private readonly float riseSmoothingTime;
    private readonly float fallSmoothingTime;
    private readonly float actionFloorRate;
    private readonly float maximumRate;

    public PlaybackRateShaper(
        float riseSmoothingTime,
        float fallSmoothingTime,
        float actionFloorRate,
        float maximumRate)
    {
        this.riseSmoothingTime = Mathf.Max(0f, riseSmoothingTime);
        this.fallSmoothingTime = Mathf.Max(0f, fallSmoothingTime);
        this.actionFloorRate = Mathf.Max(0f, actionFloorRate);
        this.maximumRate = Mathf.Max(0f, maximumRate);
    }

    public float CurrentRate { get; private set; }

    public float Update(float targetRate, bool actionInProgress, float deltaTime)
    {
        float target = Mathf.Clamp(targetRate, 0f, maximumRate);
        if (actionInProgress)
        {
            target = Mathf.Max(target, Mathf.Min(actionFloorRate, maximumRate));
        }

        float smoothingTime = target < CurrentRate
            ? fallSmoothingTime
            : riseSmoothingTime;
        if (smoothingTime <= 0f || deltaTime <= 0f)
        {
            CurrentRate = target;
        }
        else
        {
            float alpha = 1f - (float)Math.Exp(-deltaTime / smoothingTime);
            CurrentRate = Mathf.Lerp(CurrentRate, target, alpha);
        }

        if (target <= StopTargetEpsilon && CurrentRate <= SnapToZeroRate)
        {
            CurrentRate = 0f;
        }

        return CurrentRate;
    }

    public void Reset()
    {
        CurrentRate = 0f;
    }
}
