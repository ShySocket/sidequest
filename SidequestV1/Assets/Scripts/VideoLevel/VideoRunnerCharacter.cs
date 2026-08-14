using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The character: stands at a fixed column of the video, follows the ground line,
/// and jumps or steps aside on cue.
/// </summary>
/// <remarks>
/// Motion is kinematic rather than physics-driven. The "ground" here is a line
/// sampled from the video, not a collider, and it slides around the screen as the
/// car moves; a Rigidbody2D resting on a collider that teleports every frame
/// behaves badly. Integrating a jump arc above a moving ground line is both
/// simpler and exactly reproducible, which matters when the level is authored to
/// specific moments.
///
/// Because the video scrolls past, the character never moves horizontally - it
/// stays at <see cref="VideoLevel.CharacterColumn"/> and the world comes to it.
/// </remarks>
public sealed class VideoRunnerCharacter : MonoBehaviour
{
    public enum Stance
    {
        Grounded,
        Airborne,
        Dodging,
        Hidden
    }

    [SerializeField] LevelDirector director;
    [SerializeField] VideoBackground background;
    [SerializeField] VideoRunnerBallView view;

    [Header("Jump")]
    [Tooltip("Peak height when the cue records none, as a fraction of frame height.")]
    [SerializeField] float jumpHeight = 0.22f;

    [Tooltip("Bounds on a cue's recorded height, so a huge one stays on screen.")]
    [SerializeField] Vector2 jumpHeightRange = new Vector2(0.12f, 0.50f);

    [Tooltip("Seconds from take-off to landing for a default-height jump.")]
    [SerializeField] float airTime = 1.0f;

    [Header("Dodge")]
    [SerializeField] float dodgeDuration = 0.55f;

    [Tooltip("Step toward the camera, as a fraction of frame height. The lane change.")]
    [SerializeField] float dodgeDepth = 0.09f;

    [Tooltip("Small sideways drift during the dodge, as a fraction of frame width.")]
    [SerializeField] float dodgeDistance = 0.04f;

    [Header("Size")]
    [Tooltip("Ball diameter as a fraction of the video frame's height.")]
    // 0.173 is the tracked marker's own diameter, so the ball matches the size
    // the movement was drawn at.
    [SerializeField] float characterHeight = 0.173f;

    [Tooltip("Diameter multiplier where the ground is highest in frame (furthest away).")]
    [SerializeField] float farScale = 0.72f;

    [Tooltip("Diameter multiplier where the ground is lowest in frame (nearest).")]
    [SerializeField] float nearScale = 1.25f;

    [Tooltip("Ground line heights, normalized, that map to far and near scale.")]
    [SerializeField] Vector2 depthRange = new Vector2(0.45f, 0.95f);

    [Header("Smoothing")]
    [Tooltip("Seconds for the character to settle onto a change in the ground line.")]
    // The path is smoothed when authored, but the ball still benefits from a
    // critically damped follow: it removes the last of the sampling stair-step
    // and makes surface changes read as easing rather than snapping.
    [SerializeField] float groundSmoothing = 0.09f;

    [Tooltip("Seconds for the character to settle onto a change in column.")]
    [SerializeField] float columnSmoothing = 0.14f;

    readonly List<EventState> states = new List<EventState>();

    Stance stance = Stance.Grounded;
    float airHeight;
    float verticalVelocity;
    float activeJumpHeight;
    float activeAirTime;
    int scoredEventCount;

    float smoothedGround = -1f;
    float groundVelocity;
    float takeOffGround = -1f;
    float arcDrop;
    float arcGravity;
    float arcTakeOffSpeed;
    float smoothedColumn = -1f;
    float columnVelocity;
    float dodgeElapsed = -1f;
    float activeDodgeDuration;
    float lastVideoTime = -1f;
    int cleared;
    int missed;
    string lastOutcome = string.Empty;

    public Stance CurrentStance => stance;
    public int Cleared => cleared;
    public int Missed => missed;

    /// <summary>Cues the player is asked to play - hops are choreography only.</summary>
    public int TotalEvents => scoredEventCount;
    public string LastOutcome => lastOutcome;

    /// <summary>Raised with (event, success) as each cue is resolved.</summary>
    public event Action<VideoLevelEvent, bool> EventResolved;

    sealed class EventState
    {
        public VideoLevelEvent Event;
        public VideoLevelEventType Type;
        public bool Started;
        public bool Scored;
        public bool Resolved;
    }

    void Start()
    {
        if (director == null)
        {
            director = FindFirstObjectByType<LevelDirector>();
        }

        if (background == null)
        {
            background = FindFirstObjectByType<VideoBackground>();
        }

        if (view == null)
        {
            view = GetComponentInChildren<VideoRunnerBallView>();
        }

        // A scene saved before the ball existed has no view component at all, so
        // there would be nothing to self-heal further down. Adding it here means
        // any scene, however old, renders the current character.
        if (view == null)
        {
            view = gameObject.AddComponent<VideoRunnerBallView>();
        }
    }

    void Update()
    {
        if (director == null || !director.IsReady)
        {
            return;
        }

        BuildStatesIfNeeded();
        ReadInput();

        float distance = director.Distance;
        bool hidden = director.Level.IsHiddenAtDistance(distance);

        // Arcs advance in VIDEO time, not wall time. Takeoffs fire at video
        // moments and landings are authored to video moments, so the arc
        // between them has to run on the same clock - integrating with
        // Time.deltaTime made every jump desynchronize from the footage the
        // moment playback ran at any rate other than 1x (fast-forward, or
        // the vehicle-speed mode where the video follows the car).
        float videoDelta = lastVideoTime < 0f
            ? 0f
            : Mathf.Clamp(director.VideoTime - lastVideoTime, 0f, 0.25f);
        lastVideoTime = director.VideoTime;

        UpdateStance(videoDelta, hidden);
        UpdateEvents(distance, hidden);
        UpdateTransform(distance);
    }

    void BuildStatesIfNeeded()
    {
        if (states.Count != 0 || director.Level == null)
        {
            return;
        }

        scoredEventCount = 0;
        foreach (VideoLevelEvent entry in director.Level.Events)
        {
            var type = VideoLevelEventTypes.Parse(entry.type);
            states.Add(new EventState
            {
                Event = entry,
                Type = type
            });
            if (type != VideoLevelEventType.Hop)
            {
                scoredEventCount++;
            }
        }
    }

    void ReadInput()
    {
        // Input no longer moves the character: the choreography is
        // predetermined and identical on every playthrough. A press only
        // scores - inside a cue's window it clears, otherwise the ball
        // flashes red when the scheduled motion fires anyway.
        if (WasJumpPressed() || WasDodgePressed())
        {
            RegisterPress(director.VideoTime);
        }
    }

    void RegisterPress(float videoTime)
    {
        for (int i = 0; i < states.Count; i++)
        {
            EventState state = states[i];
            if (state.Resolved || state.Scored || state.Type == VideoLevelEventType.Hop)
            {
                continue;
            }

            float half = Mathf.Max(state.Event.windowSeconds, 0.24f) * 0.5f;
            if (Mathf.Abs(videoTime - state.Event.time) <= half)
            {
                state.Scored = true;
                return;
            }
        }
    }

    static bool WasJumpPressed()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            return true;
        }

        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            return true;
        }

        return Touchscreen.current != null
            && Touchscreen.current.primaryTouch.press.wasPressedThisFrame;
    }

    static bool WasDodgePressed()
    {
        if (Keyboard.current == null)
        {
            return false;
        }

        return Keyboard.current.downArrowKey.wasPressedThisFrame
            || Keyboard.current.sKey.wasPressedThisFrame
            || Keyboard.current.leftShiftKey.wasPressedThisFrame;
    }

    void UpdateStance(float deltaTime, bool hidden)
    {
        if (hidden)
        {
            stance = Stance.Hidden;
            airHeight = 0f;
            verticalVelocity = 0f;
            dodgeElapsed = -1f;
            return;
        }

        if (stance == Stance.Hidden)
        {
            stance = Stance.Grounded;
        }

        if (stance == Stance.Dodging)
        {
            dodgeElapsed += deltaTime;
            if (dodgeElapsed >= ActiveDodgeDuration())
            {
                dodgeElapsed = -1f;
                stance = Stance.Grounded;
            }

            return;
        }

        // Arcs are started by the schedule (StartScheduledJump), never by input.
        if (stance != Stance.Airborne)
        {
            return;
        }

        float arc = ArcDuration();
        float gravity = arcGravity > 0f
            ? arcGravity
            : 8f * Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(arc * arc, 0.0001f);
        verticalVelocity -= gravity * deltaTime;
        airHeight += verticalVelocity * deltaTime;

        // airHeight is measured above the TAKE-OFF line, so touchdown is where
        // the parabola meets the LIVE line - below zero when the landing spot
        // sits lower than the take-off, above it when the ground rose. Only
        // while FALLING: a parabola cannot land on the way up, and the ground
        // smoothing advances on wall time while the arc advances on video
        // time - on a rising line (the 17.0s suv takeoff) one wall frame of
        // smoothing creep exceeded the just-seeded airHeight and killed the
        // jump the instant it started.
        float floor = takeOffGround >= 0f ? takeOffGround - smoothedGround : 0f;
        if (verticalVelocity < 0f && airHeight <= floor)
        {
            airHeight = 0f;
            verticalVelocity = 0f;
            stance = Stance.Grounded;
        }
    }

    void UpdateEvents(float distance, bool hidden)
    {
        float videoTime = director.VideoTime;

        for (int i = 0; i < states.Count; i++)
        {
            EventState state = states[i];
            if (state.Resolved)
            {
                continue;
            }

            // The choreography fires at exactly the written time, every
            // playthrough, whatever the player does.
            if (!state.Started && videoTime >= state.Event.time)
            {
                state.Started = true;
                if (state.Type == VideoLevelEventType.Dodge)
                {
                    dodgeElapsed = 0f;
                    activeDodgeDuration = state.Event.duration;
                    stance = Stance.Dodging;
                }
                else
                {
                    StartScheduledJump(state.Event);
                }

                // A hop is pure choreography: nothing to score, nothing to
                // miss, no red flash, no entry in the tally.
                if (state.Type == VideoLevelEventType.Hop)
                {
                    state.Resolved = true;
                    continue;
                }
            }

            // Score once the window has fully passed, so a slightly-late press
            // still counts.
            float half = Mathf.Max(state.Event.windowSeconds, 0.24f) * 0.5f;
            if (!state.Started || videoTime <= state.Event.time + half)
            {
                continue;
            }

            state.Resolved = true;
            bool success = state.Scored || hidden;
            if (success)
            {
                cleared++;
            }
            else
            {
                missed++;
                // The miss is shown on the ball itself: it flashes red while
                // the scheduled motion carries on regardless.
                view?.Flash(0.6f);
            }

            lastOutcome = $"{(success ? "CLEAR" : "MISS")}  {state.Event.label}";
            EventResolved?.Invoke(state.Event, success);
        }
    }

    void StartScheduledJump(VideoLevelEvent cue)
    {
        // A jump interrupts whatever the ball was doing; the schedule owns it.
        dodgeElapsed = -1f;
        // An authored cue is trusted rather than clamped to the tuned range:
        // its numbers were audited against the frames (the people at 24s take
        // more height than any tuned jump), and the tuned range lives in a
        // saved scene that would silently pin old limits. The wide clamp only
        // guards against a corrupt level file.
        activeJumpHeight = cue.height > 0f
            ? Mathf.Clamp(cue.height, 0.05f, 0.8f)
            : Mathf.Clamp(jumpHeight, jumpHeightRange.x, jumpHeightRange.y);
        activeAirTime = cue.airTime;

        // The arc is ONE parabola in screen space, from the take-off point to
        // the landing point, peaking `height` above the take-off line. Its
        // acceleration is constant - that is what "ballistic" looks like -
        // and for a flat landing (drop 0) the constants reduce exactly to the
        // old 8h/T^2 and 4h/T. A landing below the take-off (the 7.15s
        // rail-to-road vault) simply falls farther than it rose, under the
        // same gravity, instead of blending the terrain drop into the descent
        // (which kicked in right after the apex and read as a lurch).
        takeOffGround = smoothedGround;
        float duration = Mathf.Max(ArcDuration(), 0.01f);
        float landDistance = director.Level.DistanceAtTime(cue.time + duration);
        // Never land above your own apex: a corrupt prediction would make the
        // square root below meaningless.
        arcDrop = Mathf.Max(
            director.Level.GroundAtDistance(landDistance) - takeOffGround,
            -0.95f * activeJumpHeight);
        float root = Mathf.Sqrt(2f * activeJumpHeight)
            + Mathf.Sqrt(2f * (activeJumpHeight + arcDrop));
        arcGravity = root * root / (duration * duration);
        arcTakeOffSpeed = Mathf.Sqrt(2f * arcGravity * activeJumpHeight);
        verticalVelocity = arcTakeOffSpeed;
        airHeight = 0.0001f;
        stance = Stance.Airborne;
    }

    /// <summary>
    /// How far through its descent the arc is, 0 while rising and 1 at touchdown.
    /// The share of the landing spot's depth the ball has taken on.
    /// </summary>
    /// <remarks>
    /// The descent runs from the apex (`height` above the take-off line) down
    /// to the landing spot (`arcDrop` below it). Height falls with the square
    /// of time, so the square root of the fallen fraction is the descent's own
    /// clock - linear in time - and smoothing it starts and finishes the size
    /// change gently instead of at full rate.
    /// </remarks>
    float DescentFraction()
    {
        if (stance != Stance.Airborne || verticalVelocity >= 0f)
        {
            return 0f;
        }

        float height = Mathf.Max(activeJumpHeight, 0.0001f);
        float fallen = Mathf.Clamp01(
            (height - airHeight) / Mathf.Max(height + arcDrop, 0.0001f));
        return Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(fallen));
    }

    /// <summary>Seconds the dodge in progress takes: the cue's own, or the tuned default.</summary>
    float ActiveDodgeDuration()
    {
        return activeDodgeDuration > 0f ? activeDodgeDuration : dodgeDuration;
    }

    /// <summary>Take-off speed of the jump in progress, for normalizing stretch.</summary>
    float TakeOffSpeed()
    {
        return arcTakeOffSpeed > 0f
            ? arcTakeOffSpeed
            : 4f * Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(ArcDuration(), 0.01f);
    }

    /// <summary>
    /// Seconds from take-off to landing for the jump in progress.
    /// </summary>
    /// <remarks>
    /// Scales with the square root of height, as a real ballistic arc does, so a
    /// tall jump hangs longer instead of being flung upward at an implausible
    /// speed to fit a fixed duration.
    /// </remarks>
    float ArcDuration()
    {
        // The cue's own air time wins when it carries one: it was sized offline
        // so the arc outlasts the obstacle's crossing of the column, which the
        // sqrt rule knows nothing about.
        if (activeAirTime > 0f)
        {
            // Floor at 0.3s: the annotated bush hops are 0.4s and the chain hop
            // 0.34s, and a higher floor silently doubled them.
            return Mathf.Clamp(activeAirTime, 0.3f, 3.4f);
        }

        return airTime * Mathf.Sqrt(
            Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(jumpHeight, 0.01f));
    }


    bool Satisfies(VideoLevelEventType type)
    {
        return type switch
        {
            VideoLevelEventType.Dodge => stance == Stance.Dodging,
            _ => stance == Stance.Airborne
        };
    }

    void UpdateTransform(float distance)
    {
        if (background == null || director.Level == null)
        {
            return;
        }

        float column = director.Level.ColumnAtDistance(distance);
        float groundY = director.Level.GroundAtDistance(distance);

        // Critically damped follow, so the ball eases onto changes in the ground
        // line and its lane instead of tracking every sample exactly.
        if (smoothedGround < 0f)
        {
            smoothedGround = groundY;
            smoothedColumn = column;
        }

        smoothedGround = Mathf.SmoothDamp(
            smoothedGround, groundY, ref groundVelocity, groundSmoothing);
        smoothedColumn = Mathf.SmoothDamp(
            smoothedColumn, column, ref columnVelocity, columnSmoothing);

        groundY = smoothedGround;
        column = smoothedColumn;

        int heading = director.Level.TravelDirection;

        if (stance == Stance.Dodging && dodgeElapsed >= 0f)
        {
            // A dodge is a lane change toward the camera, not a slide along the
            // road: on screen that is a step DOWN, and the depth scaling grows
            // the ball as it comes nearer, which is what sells the sidestep.
            // The ball then passes in front of the sign. Out and back, so it
            // returns to its lane on its own.
            float phase = Mathf.Sin(
                Mathf.PI * Mathf.Clamp01(dodgeElapsed / ActiveDodgeDuration()));
            groundY += dodgeDepth * phase;
            column += dodgeDistance * phase * -heading;
        }

        Vector3 groundPosition = background.FrameToWorld(column, groundY);
        float frameHeight = background.FrameHeightInWorld;

        // Nearer ground sits lower in frame, so the ball grows as the run line
        // descends. Without this it appears to swim as the road nears and recedes.
        // The range comes from this level's own path percentiles, so the size
        // sweep matches how much depth the clip's path actually covers.
        //
        // A jump changes height, not where down the road the ball is: a
        // rail-to-road drop mid-arc is terrain, and letting it leak into the
        // airborne ball made the 7.15s vault sag mid-RISE (the line dives
        // faster than the lift grows) and then hover at a doubled apex - and
        // grew the ball 37% while rising, which reads as flying at the camera.
        // The flight itself is the single parabola StartScheduledJump set up,
        // measured above the take-off line; only the SIZE eases from the
        // take-off spot's depth to the landing spot's across the descent,
        // arriving exactly at touchdown. The shadow stays on the live terrain
        // line below and reads the altitude honestly.
        float sizeGround = stance == Stance.Airborne && takeOffGround >= 0f
            ? Mathf.Lerp(takeOffGround, groundY, DescentFraction())
            : groundY;
        Vector2 range = director.Level.PathDepthRange;
        float depth = Mathf.InverseLerp(range.x, range.y, sizeGround);

        // The ball's height above the LIVE ground line: its rendered bottom is
        // takeOffGround - airHeight, expressed here relative to groundY because
        // that is where the shadow sits. Floored so a mid-arc ground step up
        // can never push it below the terrain and fake a landing.
        float arcAir = stance == Stance.Airborne && takeOffGround >= 0f
            ? Mathf.Max(airHeight + (groundY - takeOffGround), 0.0001f)
            : airHeight;
        // The level records the diameter the movement was drawn at; prefer it
        // over the serialized field, which a scene saved earlier would pin.
        float authored = director.Level.MarkerDiameter;
        float baseDiameter = authored > 0f ? authored : characterHeight;
        float diameter = baseDiameter * frameHeight * Mathf.Lerp(farScale, nearScale, depth);

        transform.position = groundPosition;

        // Pass behind foreground poles: a strip of the video is re-drawn in
        // front of the ball wherever the level says so, letting only the
        // object's silhouette occlude when the level ships one. The strip is
        // shown ONLY while it can actually cover the ball - it is invisible
        // against the background by construction, so any imperfection in it
        // becomes visible exactly when it touches the ball, and the gate
        // bounds that to moments when something really is in front.
        if (background != null)
        {
            bool shown = false;
            if (director.Level.TryGetForeground(
                distance, out Rect strip, out Rect maskUv, out bool masked))
            {
                float ry = diameter * 0.5f / frameHeight;
                float rx = diameter * 0.5f / background.FrameWidthInWorld;
                float centreY = groundY - arcAir - ry;
                shown = strip.xMin <= column + rx && strip.xMax >= column - rx
                    && strip.yMin <= centreY + ry && strip.yMax >= centreY - ry;
                if (shown)
                {
                    background.ShowForeground(strip, maskUv, masked);
                }
            }

            if (!shown)
            {
                background.HideForeground();
            }
        }

        if (view != null)
        {
            view.Apply(
                new VideoRunnerBallView.Pose
                {
                    GroundPosition = groundPosition,
                    AirHeight = arcAir * frameHeight,
                    Diameter = diameter,
                    ScreenSpeed = director.Level.ScreenSpeedAtDistance(distance)
                        * background.FrameWidthInWorld,
                    VerticalSpeed01 = TakeOffSpeed() > 0f
                        ? Mathf.Abs(verticalVelocity) / TakeOffSpeed()
                        : 0f,
                    JumpHeight = Mathf.Max(activeJumpHeight, jumpHeight) * frameHeight,
                    Heading = heading,
                    Hidden = stance == Stance.Hidden,
                    Surface = director.Level.SurfaceAtDistance(distance),
                    Ambient = director.Level.AmbientAtDistance(distance)
                },
                Time.deltaTime);
        }
    }

    public void ResetRun()
    {
        states.Clear();
        cleared = 0;
        missed = 0;
        airHeight = 0f;
        verticalVelocity = 0f;
        dodgeElapsed = -1f;
        stance = Stance.Grounded;
        lastOutcome = string.Empty;
        smoothedGround = -1f;
        smoothedColumn = -1f;
        takeOffGround = -1f;
        arcDrop = 0f;
        arcGravity = 0f;
        arcTakeOffSpeed = 0f;
        groundVelocity = 0f;
        columnVelocity = 0f;
        lastVideoTime = -1f;
    }
}
