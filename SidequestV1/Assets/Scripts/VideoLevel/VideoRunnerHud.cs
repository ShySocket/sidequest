using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// On-screen readout for playtesting the authored timeline.
/// </summary>
/// <remarks>
/// IMGUI on purpose: this exists to tune timeline.json, so it needs to show the
/// current video time (the units timeline.json is written in) and whether each
/// cue landed, without any scene wiring to keep in sync.
/// </remarks>
public sealed class VideoRunnerHud : MonoBehaviour
{
    const float UpcomingSeconds = 1.2f;

    const string Controls =
        "SPACE / tap = jump    DOWN or S = dodge    R = restart    "
        + "- / = speed    LEFT / RIGHT = scrub 5s    T = timer    F1 = hide";

    [SerializeField] LevelDirector director;
    [SerializeField] VideoRunnerCharacter character;
    [SerializeField] bool showDebug = true;

    [Tooltip("Large top-left clock, for noting the time of anything worth changing.")]
    [SerializeField] bool showTimer = true;

    GUIStyle heading;
    GUIStyle body;
    GUIStyle cue;
    GUIStyle timer;
    Texture2D chip;
    float outcomeShownAt = -10f;
    string shownOutcome = string.Empty;

    // OnGUI runs at least twice a frame (layout, then repaint), so anything
    // built there is built twice. These are rebuilt on a timer in Update and
    // only read during OnGUI.
    const float TextRefreshInterval = 0.1f;
    float textRefreshedAt = -1f;
    string scoreText = string.Empty;
    string statusText = string.Empty;
    string cueText;
    float cueAlpha;

    // Unlike the rest of the HUD this is rebuilt every frame rather than ten
    // times a second: it is the instrument used to write down when something
    // needs changing, so a stale reading is the one thing it must never show.
    // One short string per frame is a rounding error next to decoding video.
    string timerText = string.Empty;

    void Start()
    {
        if (director == null)
        {
            director = FindFirstObjectByType<LevelDirector>();
        }

        if (character == null)
        {
            character = FindFirstObjectByType<VideoRunnerCharacter>();
        }

        if (character != null)
        {
            character.EventResolved += (_, __) =>
            {
                shownOutcome = character.LastOutcome;
                outcomeShownAt = Time.time;
            };
        }
    }

    void Update()
    {
        if (director == null || Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            director.Restart();
            character?.ResetRun();
        }

        if (Keyboard.current.equalsKey.wasPressedThisFrame)
        {
            director.SpeedMultiplier += 0.25f;
        }

        if (Keyboard.current.minusKey.wasPressedThisFrame)
        {
            director.SpeedMultiplier -= 0.25f;
        }

        if (Keyboard.current.f1Key.wasPressedThisFrame)
        {
            showDebug = !showDebug;
        }

        // Its own toggle, not F1's: the timer stays up while the verbose rows
        // are hidden, and can still be cleared away for a clean capture.
        if (Keyboard.current.tKey.wasPressedThisFrame)
        {
            showTimer = !showTimer;
        }

        // Left/right scrub by 5s, so a cue that looks wrong can be replayed
        // without sitting through the run again.
        if (Keyboard.current.rightArrowKey.wasPressedThisFrame)
        {
            director.SeekToTime(director.VideoTime + 5f);
        }

        if (Keyboard.current.leftArrowKey.wasPressedThisFrame)
        {
            director.SeekToTime(director.VideoTime - 5f);
        }

        RefreshText();
    }

    void RefreshText()
    {
        if (character == null || director.Level == null)
        {
            return;
        }

        // Seconds are the unit timeline.json is written in, so a time read off
        // this display can be typed straight into the timeline. The frame
        // number rides along for frame-by-frame notes.
        float fps = director.Level.Fps;
        timerText = fps > 0f
            ? $"{director.VideoTime:0.00}s   f{director.VideoTime * fps:0}"
            : $"{director.VideoTime:0.00}s";

        // The upcoming-cue scan has to run every frame or the prompt lags, but
        // it is a walk over a short list, not string work.
        cueText = null;
        VideoLevelEvent next = null;
        float bestGap = float.MaxValue;
        foreach (VideoLevelEvent entry in director.Level.Events)
        {
            float gap = entry.time - director.VideoTime;
            if (gap >= 0f && gap < bestGap)
            {
                bestGap = gap;
                next = entry;
            }
        }

        if (next != null && bestGap <= UpcomingSeconds)
        {
            string action = VideoLevelEventTypes.Parse(next.type) == VideoLevelEventType.Dodge
                ? "DODGE"
                : "JUMP";
            cueText = $"{action}  ({next.label})";
            cueAlpha = Mathf.Clamp01(1f - bestGap / UpcomingSeconds);
        }

        if (Time.unscaledTime - textRefreshedAt < TextRefreshInterval)
        {
            return;
        }

        textRefreshedAt = Time.unscaledTime;
        scoreText =
            $"{character.Cleared} cleared   {character.Missed} missed   /  {character.TotalEvents}";
        statusText =
            $"t {director.VideoTime:0.00}s    d {director.Distance:0}    "
            + $"{director.Progress * 100f:0}%    x{director.SpeedMultiplier:0.00}    "
            + $"{character.CurrentStance}";
    }

    void EnsureStyles()
    {
        if (heading != null)
        {
            return;
        }

        heading = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
        heading.normal.textColor = Color.white;

        body = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        body.normal.textColor = new Color(1f, 0.92f, 0.6f);

        cue = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
        cue.alignment = TextAnchor.MiddleCenter;

        timer = new GUIStyle(GUI.skin.label)
        {
            fontSize = 34,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
        };
        timer.normal.textColor = Color.white;

        // The footage is bright daylight, so white text alone disappears against
        // pale concrete. A dark chip behind it keeps the reading legible over
        // every frame of the clip.
        chip = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        chip.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
        chip.Apply();
    }

    void OnDestroy()
    {
        if (chip != null)
        {
            Destroy(chip);
        }
    }

    void OnGUI()
    {
        if (director == null || character == null || director.Level == null)
        {
            return;
        }

        EnsureStyles();

        float top = 12f;
        if (showTimer)
        {
            // Sized to the text so the chip fits three-digit times without
            // either clipping or leaving a wide empty bar.
            Vector2 size = timer.CalcSize(new GUIContent(timerText));
            var box = new Rect(14f, top, size.x + 28f, size.y + 14f);
            GUI.DrawTexture(box, chip);
            GUI.Label(new Rect(box.x + 14f, box.y, size.x, box.height), timerText, timer);
            top = box.yMax + 8f;
        }

        GUI.Label(new Rect(18, top, 600, 34), scoreText, heading);

        if (showDebug)
        {
            GUI.Label(new Rect(18, top + 38f, 600, 24), statusText, body);
            GUI.Label(new Rect(18, top + 60f, 860, 24), Controls, body);
        }

        DrawUpcomingCue();
        DrawOutcome();

        if (director.IsFinished)
        {
            GUI.Label(new Rect(0, Screen.height * 0.42f, Screen.width, 60), "RUN COMPLETE", cue);
        }
    }

    void DrawUpcomingCue()
    {
        if (string.IsNullOrEmpty(cueText))
        {
            return;
        }

        // Fade in as the cue approaches, so the prompt reads as urgency.
        cue.normal.textColor = new Color(1f, 1f, 1f, cueAlpha);
        GUI.Label(new Rect(0, Screen.height * 0.22f, Screen.width, 60), cueText, cue);
    }

    void DrawOutcome()
    {
        float age = Time.time - outcomeShownAt;
        if (age > 0.9f || string.IsNullOrEmpty(shownOutcome))
        {
            return;
        }

        bool success = shownOutcome.StartsWith("CLEAR");
        cue.normal.textColor = success
            ? new Color(0.4f, 1f, 0.5f, 1f - age)
            : new Color(1f, 0.4f, 0.4f, 1f - age);
        GUI.Label(new Rect(0, Screen.height * 0.62f, Screen.width, 60), shownOutcome, cue);
    }
}
