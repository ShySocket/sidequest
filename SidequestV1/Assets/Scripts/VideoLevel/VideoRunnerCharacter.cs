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

    [Tooltip("How far ahead to look for the cue this jump is aimed at.")]
    [SerializeField] float cueLookahead = 1.4f;

    [Tooltip("A tap this long before landing still buffers the next jump.")]
    [SerializeField] float inputBuffer = 0.15f;

    [Header("Dodge")]
    [SerializeField] float dodgeDuration = 0.55f;

    [Tooltip("Sideways travel as a fraction of the video frame's width.")]
    [SerializeField] float dodgeDistance = 0.10f;

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

    float smoothedGround = -1f;
    float groundVelocity;
    float smoothedColumn = -1f;
    float columnVelocity;
    float dodgeElapsed = -1f;
    float bufferedJumpAt = -1f;
    int cleared;
    int missed;
    string lastOutcome = string.Empty;

    public Stance CurrentStance => stance;
    public int Cleared => cleared;
    public int Missed => missed;
    public int TotalEvents => states.Count;
    public string LastOutcome => lastOutcome;

    /// <summary>Raised with (event, success) as each cue is resolved.</summary>
    public event Action<VideoLevelEvent, bool> EventResolved;

    sealed class EventState
    {
        public VideoLevelEvent Event;
        public VideoLevelEventType Type;
        public bool Entered;
        public bool Satisfied;
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

        UpdateStance(Time.deltaTime, hidden);
        UpdateEvents(distance, hidden);
        UpdateTransform(distance);
    }

    void BuildStatesIfNeeded()
    {
        if (states.Count != 0 || director.Level == null)
        {
            return;
        }

        foreach (VideoLevelEvent entry in director.Level.Events)
        {
            states.Add(new EventState
            {
                Event = entry,
                Type = VideoLevelEventTypes.Parse(entry.type)
            });
        }
    }

    void ReadInput()
    {
        if (WasJumpPressed())
        {
            bufferedJumpAt = Time.time;
        }

        if (WasDodgePressed() && stance == Stance.Grounded)
        {
            dodgeElapsed = 0f;
            stance = Stance.Dodging;
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
            if (dodgeElapsed >= dodgeDuration)
            {
                dodgeElapsed = -1f;
                stance = Stance.Grounded;
            }

            return;
        }

        bool jumpQueued = bufferedJumpAt >= 0f && Time.time - bufferedJumpAt <= inputBuffer;

        if (stance == Stance.Grounded && jumpQueued)
        {
            activeJumpHeight = HeightForJump();

            // A symmetric arc of the chosen height and duration:
            //   v0 = 4h / t, g = 8h / t^2
            verticalVelocity = 4f * activeJumpHeight / Mathf.Max(ArcDuration(), 0.01f);
            airHeight = 0.0001f;
            stance = Stance.Airborne;
            bufferedJumpAt = -1f;
            return;
        }

        if (stance != Stance.Airborne)
        {
            return;
        }

        float arc = ArcDuration();
        float gravity =
            8f * Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(arc * arc, 0.0001f);
        verticalVelocity -= gravity * deltaTime;
        airHeight += verticalVelocity * deltaTime;

        if (airHeight <= 0f)
        {
            airHeight = 0f;
            verticalVelocity = 0f;
            stance = Stance.Grounded;
        }
    }

    void UpdateEvents(float distance, bool hidden)
    {
        for (int i = 0; i < states.Count; i++)
        {
            EventState state = states[i];
            if (state.Resolved)
            {
                continue;
            }

            float half = Mathf.Max(state.Event.windowDistance, 0.01f);
            bool inside = Mathf.Abs(distance - state.Event.distance) <= half;

            if (inside)
            {
                state.Entered = true;
                if (Satisfies(state.Type))
                {
                    state.Satisfied = true;
                }

                continue;
            }

            // Only resolve once the window is behind us, so a cue cleared at any
            // point inside its window still counts.
            if (!state.Entered || distance <= state.Event.distance)
            {
                continue;
            }

            state.Resolved = true;
            bool success = state.Satisfied || hidden;
            if (success)
            {
                cleared++;
            }
            else
            {
                missed++;
            }

            lastOutcome = $"{(success ? "CLEAR" : "MISS")}  {state.Event.label}";
            EventResolved?.Invoke(state.Event, success);
        }
    }

    /// <summary>Take-off speed of the jump in progress, for normalizing stretch.</summary>
    float TakeOffSpeed()
    {
        return 4f * Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(ArcDuration(), 0.01f);
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
        return airTime * Mathf.Sqrt(
            Mathf.Max(activeJumpHeight, 0.01f) / Mathf.Max(jumpHeight, 0.01f));
    }

    /// <summary>
    /// How high this jump should go, taken from the cue it is aimed at.
    /// </summary>
    /// <remarks>
    /// Heights come from the tracked marker, so a jump the designer drew small
    /// stays small. Without this every jump used one tuned constant and the
    /// drawn variation was lost.
    /// </remarks>
    float HeightForJump()
    {
        float distance = director.Distance;
        float best = jumpHeight;
        float bestGap = float.MaxValue;

        for (int i = 0; i < states.Count; i++)
        {
            EventState state = states[i];
            if (state.Resolved || state.Event.height <= 0f)
            {
                continue;
            }

            float gap = state.Event.distance - distance;
            if (gap < -state.Event.windowDistance || gap > cueLookahead * state.Event.windowDistance * 4f)
            {
                continue;
            }

            if (Mathf.Abs(gap) < bestGap)
            {
                bestGap = Mathf.Abs(gap);
                best = state.Event.height;
            }
        }

        return Mathf.Clamp(best, jumpHeightRange.x, jumpHeightRange.y);
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
            // Out and back, so the character returns to its lane on its own.
            // Steps *away* from where obstacles come from: the world sweeps
            // opposite to travel, so oncoming things arrive on the heading side.
            float phase = Mathf.Sin(Mathf.PI * Mathf.Clamp01(dodgeElapsed / dodgeDuration));
            column += dodgeDistance * phase * -heading;
        }

        Vector3 groundPosition = background.FrameToWorld(column, groundY);
        float frameHeight = background.FrameHeightInWorld;

        // Nearer ground sits lower in frame, so the ball grows as the run line
        // descends. Without this it appears to swim as the road nears and recedes.
        float depth = Mathf.InverseLerp(depthRange.x, depthRange.y, groundY);
        // The level records the diameter the movement was drawn at; prefer it
        // over the serialized field, which a scene saved earlier would pin.
        float authored = director.Level.MarkerDiameter;
        float baseDiameter = authored > 0f ? authored : characterHeight;
        float diameter = baseDiameter * frameHeight * Mathf.Lerp(farScale, nearScale, depth);

        transform.position = groundPosition;

        if (view != null)
        {
            view.Apply(
                new VideoRunnerBallView.Pose
                {
                    GroundPosition = groundPosition,
                    AirHeight = airHeight * frameHeight,
                    Diameter = diameter,
                    ScreenSpeed = director.Level.ScreenSpeedAtDistance(distance)
                        * background.FrameWidthInWorld,
                    VerticalSpeed01 = TakeOffSpeed() > 0f
                        ? Mathf.Abs(verticalVelocity) / TakeOffSpeed()
                        : 0f,
                    JumpHeight = Mathf.Max(activeJumpHeight, jumpHeight) * frameHeight,
                    Heading = heading,
                    Hidden = stance == Stance.Hidden
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
        bufferedJumpAt = -1f;
        stance = Stance.Grounded;
        lastOutcome = string.Empty;
        smoothedGround = -1f;
        smoothedColumn = -1f;
        groundVelocity = 0f;
        columnVelocity = 0f;
    }
}
