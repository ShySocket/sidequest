using UnityEngine;

/// <summary>
/// The timing law of interactive jumps, in one pure place.
/// </summary>
/// <remarks>
/// Interactive mode turns the press into the jump itself: a cue only fires if
/// the player pressed inside its window, and a cue nobody pressed for crashes
/// the run. That makes these few comparisons the entire contract between the
/// player and the level - window edges, the crash deadline, and what makes a
/// too-early jump unrecoverable - so they live here as statics the EditMode
/// tests can hold to account without a scene.
///
/// All times are VIDEO seconds, the unit cues are authored in.
/// </remarks>
public static class InteractiveJumpRules
{
    /// <summary>Where the ball is in a cue's timing ramp.</summary>
    public enum Phase
    {
        /// <summary>No cue near: free jumping, no tint.</summary>
        Idle,

        /// <summary>Warned but the window is shut: a jump now is the trap.</summary>
        TooEarly,

        /// <summary>The window is open: this press clears the obstacle.</summary>
        JumpNow,

        /// <summary>Past takeoff unpressed: the crash is already in the post.</summary>
        Late
    }

    /// <summary>Floor on a cue's scoring window, matching the old scorer's.</summary>
    public const float MinWindowSeconds = 0.24f;

    /// <summary>Grace on a chained press's landing-before-takeoff requirement.</summary>
    public const float ChainSlack = 0.05f;

    /// <summary>True while a press counts for the cue: the green part of the ramp.</summary>
    /// <remarks>
    /// The window CLOSES at the authored takeoff, not symmetrically around it:
    /// the arc only matches the footage when it starts on time, so a press is
    /// either banked before takeoff or it never happened. Anything later is a
    /// free jump straight into the obstacle.
    /// </remarks>
    public static bool IsWindowOpen(float videoTime, float cueTime, float windowLead)
    {
        return videoTime >= cueTime - windowLead && videoTime <= cueTime;
    }

    /// <summary>Video moment an unpressed cue becomes a crash.</summary>
    /// <remarks>
    /// The same moment the old scorer called a miss: half the cue's window past
    /// takeoff, when the obstacle is crossing the column. The beat between
    /// takeoff and here is what lets a late press visibly jump INTO the thing
    /// rather than the run ending on an invisible line.
    /// </remarks>
    public static float CrashDeadline(float cueTime, float windowSeconds)
    {
        return cueTime + Mathf.Max(windowSeconds, MinWindowSeconds) * 0.5f;
    }

    /// <summary>Can the ball leave the ground right now?</summary>
    /// <remarks>
    /// Two gates, and both exist to make "too early" a real mistake: a ball in
    /// the air has no second jump, and a ball that just landed is still
    /// gathering itself for <paramref name="cooldown"/> seconds. Without the
    /// cooldown, a panic jump at the warning lands in time to jump again and
    /// mashing becomes the best strategy.
    /// </remarks>
    public static bool CanTakeOff(
        bool grounded, float videoTime, float lastLandingTime, float cooldown)
    {
        return grounded && videoTime - lastLandingTime >= cooldown;
    }

    /// <summary>Can a press mid-air bank the next cue anyway?</summary>
    /// <remarks>
    /// The level chains arcs on purpose - the 16.35s hop lands 0.25s before
    /// the 17.0s suv cue, and the bush chain at 25.75-29.2s lands each arc a
    /// frame before the next takeoff - so demanding a grounded ball inside
    /// every window would make those cues unwinnable at any timing. A press
    /// while riding an AUTHORED arc that touches down by the cue's takeoff is
    /// therefore banked, exactly like a grounded one. A free jump never
    /// chains: its arc was nobody's schedule, and being stuck on it is the
    /// advertised price of jumping early.
    /// </remarks>
    public static bool CanChainPress(
        bool onScheduledArc, float scheduledLandingTime, float cueTime)
    {
        return onScheduledArc && scheduledLandingTime <= cueTime + ChainSlack;
    }

    /// <summary>The colour ramp's phase for a cue at <paramref name="cueTime"/>.</summary>
    public static Phase PhaseAt(
        float videoTime, float cueTime, float warningLead, float windowLead)
    {
        if (videoTime > cueTime)
        {
            return Phase.Late;
        }

        if (videoTime >= cueTime - windowLead)
        {
            return Phase.JumpNow;
        }

        return videoTime >= cueTime - warningLead ? Phase.TooEarly : Phase.Idle;
    }
}
