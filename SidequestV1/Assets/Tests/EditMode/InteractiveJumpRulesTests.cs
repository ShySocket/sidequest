using NUnit.Framework;

/// <summary>
/// The interactive-jump contract: window edges, the crash deadline, and the
/// no-double-jump trap that makes a too-early jump cost the run.
/// </summary>
/// <remarks>
/// These are the numbers the player's thumb learns, so they are pinned here
/// rather than left to drift with a refactor. The trap test is the one that
/// matters most: it proves the tuned defaults (0.35s window, 0.45s cooldown,
/// 0.75s warning) actually punish a panic jump at the warning, which is the
/// entire point of allowing free jumps at all.
/// </remarks>
public class InteractiveJumpRulesTests
{
    const float WarningLead = 0.75f;
    const float WindowLead = 0.35f;
    const float Cooldown = 0.45f;

    [Test]
    public void WindowOpensAtLeadAndClosesAtTakeoff()
    {
        const float cue = 46.42f;

        Assert.That(InteractiveJumpRules.IsWindowOpen(cue - WindowLead - 0.01f, cue, WindowLead), Is.False);
        Assert.That(InteractiveJumpRules.IsWindowOpen(cue - WindowLead, cue, WindowLead), Is.True);
        Assert.That(InteractiveJumpRules.IsWindowOpen(cue - 0.1f, cue, WindowLead), Is.True);
        Assert.That(InteractiveJumpRules.IsWindowOpen(cue, cue, WindowLead), Is.True);
        Assert.That(InteractiveJumpRules.IsWindowOpen(cue + 0.01f, cue, WindowLead), Is.False);
    }

    [Test]
    public void CrashDeadlineIsHalfTheWindowPastTakeoff()
    {
        Assert.That(InteractiveJumpRules.CrashDeadline(10f, 0.7f), Is.EqualTo(10.35f).Within(1e-4f));
    }

    [Test]
    public void CrashDeadlineFloorsATinyWindow()
    {
        // Matches the old scorer's floor, so choreographed scoring is unmoved.
        Assert.That(InteractiveJumpRules.CrashDeadline(10f, 0.05f), Is.EqualTo(10.12f).Within(1e-4f));
        Assert.That(InteractiveJumpRules.CrashDeadline(10f, 0f), Is.EqualTo(10.12f).Within(1e-4f));
    }

    [Test]
    public void AirborneBallCannotTakeOff()
    {
        Assert.That(InteractiveJumpRules.CanTakeOff(false, 20f, 0f, Cooldown), Is.False);
    }

    [Test]
    public void CooldownBlocksTakeOffRightAfterLanding()
    {
        const float landed = 20f;

        Assert.That(InteractiveJumpRules.CanTakeOff(true, landed + 0.2f, landed, Cooldown), Is.False);
        Assert.That(InteractiveJumpRules.CanTakeOff(true, landed + Cooldown, landed, Cooldown), Is.True);
    }

    [Test]
    public void FreshRunCanAlwaysTakeOff()
    {
        // ResetRun seeds the last landing at negative infinity.
        Assert.That(
            InteractiveJumpRules.CanTakeOff(true, 0f, float.NegativeInfinity, Cooldown),
            Is.True);
    }

    [Test]
    public void PhaseRampsThroughIdleEarlyNowLate()
    {
        const float cue = 30f;

        Assert.That(
            InteractiveJumpRules.PhaseAt(cue - 1f, cue, WarningLead, WindowLead),
            Is.EqualTo(InteractiveJumpRules.Phase.Idle));
        Assert.That(
            InteractiveJumpRules.PhaseAt(cue - 0.6f, cue, WarningLead, WindowLead),
            Is.EqualTo(InteractiveJumpRules.Phase.TooEarly));
        Assert.That(
            InteractiveJumpRules.PhaseAt(cue - 0.2f, cue, WarningLead, WindowLead),
            Is.EqualTo(InteractiveJumpRules.Phase.JumpNow));
        Assert.That(
            InteractiveJumpRules.PhaseAt(cue + 0.05f, cue, WarningLead, WindowLead),
            Is.EqualTo(InteractiveJumpRules.Phase.Late));
    }

    [Test]
    public void PanicJumpAtTheWarningForfeitsTheWindow()
    {
        // The trap, end to end with the tuned numbers. A free jump the moment
        // the amber warning appears (0.75s before the cue) flies a default
        // 1.0s arc - the ball is still airborne when the window opens AND
        // when it closes, so the cue can never be armed and the run crashes.
        const float cue = 30f;
        float panicJumpAt = cue - WarningLead;
        const float freeAirTime = 1.0f;
        float landsAt = panicJumpAt + freeAirTime;

        for (float t = cue - WindowLead; t <= cue; t += 0.05f)
        {
            bool grounded = t >= landsAt;
            Assert.That(
                InteractiveJumpRules.CanTakeOff(grounded, t, landsAt, Cooldown),
                Is.False,
                $"the panic jump should still lock the ball out at t={t}");
        }
    }

    [Test]
    public void ChainedPressBanksTheNextCueMidAir()
    {
        // The level's own chains: the 25.75s bush arc lands at 26.23s, one
        // frame before the 26.24s cue. A press mid-air on that AUTHORED arc
        // must count, or the chain is unwinnable at any timing.
        Assert.That(InteractiveJumpRules.CanChainPress(true, 26.23f, 26.24f), Is.True);
    }

    [Test]
    public void ChainNeverRidesAFreeJump()
    {
        // Being stuck on a free arc is the advertised price of jumping early:
        // the caller passes onScheduledArc=false for one, and no landing
        // time redeems it.
        Assert.That(InteractiveJumpRules.CanChainPress(false, 26.23f, 26.24f), Is.False);
    }

    [Test]
    public void ChainFailsWhenTheArcOutlastsTheTakeoff()
    {
        Assert.That(InteractiveJumpRules.CanChainPress(true, 26.5f, 26.24f), Is.False);
    }

    [Test]
    public void ShortHopWellBeforeTheWarningIsForgiven()
    {
        // Free jumping is allowed "at all times" - a hop that lands with
        // cooldown to spare before the window opens costs nothing.
        const float cue = 30f;
        float landsAt = cue - WindowLead - Cooldown;

        Assert.That(
            InteractiveJumpRules.CanTakeOff(true, cue - WindowLead, landsAt, Cooldown),
            Is.True);
    }
}
