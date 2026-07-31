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
    [SerializeField] SpriteRenderer body;

    [Header("Jump")]
    [Tooltip("Peak height as a fraction of the video frame's height.")]
    [SerializeField] float jumpHeight = 0.20f;

    [Tooltip("Seconds from take-off to landing.")]
    [SerializeField] float airTime = 0.95f;

    [Tooltip("A tap this long before landing still buffers the next jump.")]
    [SerializeField] float inputBuffer = 0.15f;

    [Header("Dodge")]
    [SerializeField] float dodgeDuration = 0.55f;

    [Tooltip("Sideways travel as a fraction of the video frame's width.")]
    [SerializeField] float dodgeDistance = 0.10f;

    [Header("Heading")]
    [Tooltip("Degrees the character leans into its direction of travel.")]
    [SerializeField] float leanAngle = 10f;

    [Header("Size")]
    [Tooltip("Character height as a fraction of the video frame's height.")]
    [SerializeField] float characterHeight = 0.13f;

    readonly List<EventState> states = new List<EventState>();

    Stance stance = Stance.Grounded;
    float airHeight;
    float verticalVelocity;
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

        if (body == null)
        {
            body = GetComponentInChildren<SpriteRenderer>();
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
            // A symmetric arc of the configured height and duration:
            //   v0 = 4h / t, g = 8h / t^2
            float takeOff = 4f * jumpHeight / Mathf.Max(airTime, 0.01f);
            verticalVelocity = takeOff;
            airHeight = 0.0001f;
            stance = Stance.Airborne;
            bufferedJumpAt = -1f;
            return;
        }

        if (stance != Stance.Airborne)
        {
            return;
        }

        float gravity = 8f * jumpHeight / Mathf.Max(airTime * airTime, 0.0001f);
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

        float column = director.Level.CharacterColumn;
        float groundY = director.Level.GroundAtDistance(distance);

        int heading = director.Level.TravelDirection;

        if (stance == Stance.Dodging && dodgeElapsed >= 0f)
        {
            // Out and back, so the character returns to its lane on its own.
            // Steps *away* from where obstacles come from: the world sweeps
            // opposite to travel, so oncoming things arrive on the heading side.
            float phase = Mathf.Sin(Mathf.PI * Mathf.Clamp01(dodgeElapsed / dodgeDuration));
            column += dodgeDistance * phase * -heading;
        }

        Vector3 position = background.FrameToWorld(column, groundY);
        float frameHeight = background.FrameHeightInWorld;
        float height = characterHeight * frameHeight;

        // The sprite's pivot is its centre; the ground line is where the feet go.
        position.y += height * 0.5f + airHeight * frameHeight;
        transform.position = position;

        // Mirror to face the way it is going, so directional art works unchanged.
        transform.localScale = new Vector3(height * 0.45f * heading, height, 1f);

        // A plain box cannot show facing, so lean into the heading as well; this
        // reads correctly with or without art on top.
        float lean = stance == Stance.Airborne ? leanAngle * 0.5f : leanAngle;
        transform.localRotation = Quaternion.Euler(0f, 0f, lean * heading);

        if (body != null)
        {
            body.enabled = stance != Stance.Hidden;
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
    }
}
