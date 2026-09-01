using System;
using UnityEngine;

/// <summary>
/// Turns the raw vehicle-derived playback rate into the rate the video is
/// actually driven at.
/// </summary>
/// <remarks>
/// The raw rate is honest about the vehicle and terrible to watch: a 30fps
/// clip played at 0.3x delivers nine frames a second, which reads as the game
/// stuttering. This class owns the difference between the truth and the
/// picture, with one rule at its core: the video is either moving at a rate a
/// decoder plays smoothly, or it is cleanly stopped - never crawling between.
///
/// Moving means the raw rate clamped into a narrow band around native speed
/// (slow driving reads slightly slow, fast slightly fast, judder never).
/// Whether the vehicle counts as moving is decided with hysteresis on the raw
/// rate, so stop-and-go creep does not flap the video between pause and play.
/// Transitions ease asymmetrically - slowing is kept quick so a real brake is
/// felt within the second; speeding up ramps more gently, where jitter would
/// otherwise flutter the footage - and a landed stop snaps fully to zero so
/// the video pauses instead of decaying forever.
///
/// Jumps and dodges advance on video time, so a frozen video would hang the
/// ball mid-air. While an action is in flight the rate holds at least what it
/// was at takeoff (never below the band floor): the arc plays out at full
/// pace even through a stop, and only once it lands may the world freeze -
/// immediately, on the landing frame.
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
    private readonly float minimumMovingRate;
    private readonly float maximumMovingRate;
    private readonly float movingEntryRate;
    private readonly float stoppedEntryRate;

    private bool moving;
    private bool actionWasInProgress;
    private float actionHoldRate;

    public PlaybackRateShaper(
        float riseSmoothingTime,
        float fallSmoothingTime,
        float minimumMovingRate,
        float maximumMovingRate,
        float movingEntryRate,
        float stoppedEntryRate)
    {
        this.riseSmoothingTime = Mathf.Max(0f, riseSmoothingTime);
        this.fallSmoothingTime = Mathf.Max(0f, fallSmoothingTime);
        this.minimumMovingRate = Mathf.Max(0f, minimumMovingRate);
        this.maximumMovingRate = Mathf.Max(this.minimumMovingRate, maximumMovingRate);
        this.movingEntryRate = Mathf.Max(0f, movingEntryRate);
        this.stoppedEntryRate = Mathf.Clamp(stoppedEntryRate, 0f, this.movingEntryRate);
    }

    public float CurrentRate { get; private set; }

    public float Update(float targetRate, bool actionInProgress, float deltaTime)
    {
        // Hysteresis: it takes movingEntryRate to start the video and a drop
        // below stoppedEntryRate to stop it. The gap is what keeps a creep
        // hovering near the threshold from flapping pause/play every second.
        if (moving)
        {
            moving = targetRate > stoppedEntryRate;
        }
        else
        {
            moving = targetRate >= movingEntryRate;
        }

        // The band: a moving video never plays slower than the floor - below
        // it the decoder judders - and never faster than the ceiling. Stopped
        // is a true zero, not a crawl.
        float target = moving
            ? Mathf.Clamp(targetRate, minimumMovingRate, maximumMovingRate)
            : 0f;

        if (actionInProgress)
        {
            // Latch the takeoff rate: the arc completes at least as fast as
            // it began, so a stop mid-jump never slows the ball in the air.
            // An action begun at a standstill still gets the band floor - the
            // arc runs on video time and must reach its authored landing.
            if (!actionWasInProgress)
            {
                actionHoldRate = Mathf.Max(CurrentRate, minimumMovingRate);
            }

            target = Mathf.Clamp(
                Mathf.Max(target, actionHoldRate), 0f, maximumMovingRate);
        }
        else if (actionWasInProgress && target <= StopTargetEpsilon)
        {
            // The arc landed into a stop: pause on the landing frame. Easing
            // down here would smear the crisp "finish at speed, then freeze"
            // into a fade nobody asked for.
            actionWasInProgress = false;
            CurrentRate = 0f;
            return CurrentRate;
        }

        actionWasInProgress = actionInProgress;

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
        moving = false;
        actionWasInProgress = false;
        actionHoldRate = 0f;
    }
}
