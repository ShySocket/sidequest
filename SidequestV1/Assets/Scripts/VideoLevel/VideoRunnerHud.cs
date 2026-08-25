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
    [SerializeField] LevelDirector director;
    [SerializeField] VideoRunnerCharacter character;

    [Tooltip("Large top-left clock, for noting the time of anything worth changing.")]
    [SerializeField] bool showTimer = true;

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
    string gpsText = string.Empty;

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
        if (director == null)
        {
            return;
        }

        // No keyboard on a phone; the readouts still have to refresh there.
        if (Keyboard.current == null)
        {
            RefreshText();
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

        // Lets the clock be cleared away for a clean capture.
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

        if (Time.unscaledTime - textRefreshedAt < TextRefreshInterval)
        {
            return;
        }

        textRefreshedAt = Time.unscaledTime;
        RefreshGpsText();
    }

    /// <summary>
    /// GPS connection and speed estimate, same wording as the RunnerPrototype
    /// indicator. Empty when nothing drives progress from vehicle speed
    /// (NativeRate at a desk), so the line only appears when it means something.
    /// </summary>
    void RefreshGpsText()
    {
        VehicleSpeedController vehicle = director.VehicleController;
        if (vehicle == null)
        {
            gpsText = string.Empty;
            return;
        }

        string status = GpsStatusIndicator.GetStatusText(
            vehicle.ActiveProviderMode, vehicle.TrackingState);

        float speed = vehicle.LastKnownPhysicalSpeed;
        string speedLabel;
        if (vehicle.IsUsingHeldSpeed)
        {
            speedLabel = $"Holding {speed:0.0} m/s";
        }
        else if (vehicle.ActiveProviderMode == SpeedProviderMode.Mock)
        {
            speedLabel = $"Mock {speed:0.0} m/s";
        }
        else if (vehicle.TrackingState == GpsTrackingState.Tracking)
        {
            speedLabel = $"Live {speed:0.0} m/s";
        }
        else
        {
            speedLabel = "Speed 0.0 m/s";
        }

        gpsText = $"{status}    {speedLabel}    game {vehicle.GameSpeed:0.0}";
    }

    void EnsureStyles()
    {
        if (body != null)
        {
            return;
        }

        // Bigger than the old debug rows: this is now read at a glance from a
        // phone in a mount, not squinted at on a desktop monitor.
        body = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
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

        if (!string.IsNullOrEmpty(gpsText))
        {
            Vector2 size = body.CalcSize(new GUIContent(gpsText));
            var box = new Rect(14f, top, size.x + 20f, size.y + 10f);
            GUI.DrawTexture(box, chip);
            GUI.Label(new Rect(box.x + 10f, box.y + 5f, size.x, size.y), gpsText, body);
        }

        DrawOutcome();

        if (director.IsFinished)
        {
            GUI.Label(new Rect(0, Screen.height * 0.42f, Screen.width, 60), "RUN COMPLETE", cue);
        }
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
