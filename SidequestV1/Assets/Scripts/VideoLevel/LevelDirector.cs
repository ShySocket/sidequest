using System;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Owns how far through the level the player is, and keeps the video agreeing.
/// </summary>
/// <remarks>
/// Distance is the authority, not video time. Everything in the level file is
/// keyed on distance so it stays correct at any speed, and this is where that
/// pays off: the two playback modes below differ only in how distance advances,
/// and neither needs the level re-authored.
///
/// The video is driven by <see cref="VideoPlayer.playbackSpeed"/> rather than by
/// seeking each frame. Seeking 1080p HEVC every frame stutters badly; letting it
/// play at a scaled rate is smooth, and a corrective seek is only needed when
/// drift grows past <see cref="ResyncThreshold"/>.
/// </remarks>
public sealed class LevelDirector : MonoBehaviour
{
    public enum PlaybackMode
    {
        /// <summary>NativeRate at a desk, VehicleSpeed on a phone. The default.</summary>
        /// <remarks>
        /// First in the enum on purpose: scenes saved before this mode existed
        /// serialized 0, so they migrate to Auto rather than silently pinning
        /// the old behaviour.
        /// </remarks>
        Auto,

        /// <summary>Advance at the clip's own rate, scaled by <see cref="speedMultiplier"/>.</summary>
        NativeRate,

        /// <summary>Advance from real vehicle motion via VehicleSpeedController.</summary>
        VehicleSpeed
    }

    const float ResyncThreshold = 1.5f;
    const float MaxPlaybackSpeed = 4f;

    [SerializeField] string levelFileName = "IMG_3775.authored.json";
    [SerializeField] VideoBackground background;
    [SerializeField] PlaybackMode playbackMode = PlaybackMode.Auto;

    [Tooltip("Scales playback in NativeRate mode. 1 = the speed the clip was filmed at.")]
    [Range(0.25f, 3f)]
    [SerializeField] float speedMultiplier = 1f;

    [Tooltip("VehicleSpeed mode only: game units per second that equal the clip's own pace.")]
    [SerializeField] float referenceGameSpeed = 6f;

    [SerializeField] VehicleSpeedController vehicleSpeedController;

    [Tooltip("Seconds to hold on the first frame before the run starts.")]
    // The first moments after Play are the decoder's worst: buffers are cold
    // and the clock runs ahead of the pictures, so the video looked frozen
    // while the ball already moved - the "laggy on load" complaint. Holding
    // paused on a delivered first frame, then starting, means play begins
    // with the video and the ball moving together from the beginning.
    [SerializeField] float startHold = 0.8f;

    VideoLevel level;
    float distance;
    float videoTime;
    bool finished;
    bool started;
    float firstFrameAt = -1f;

    // Seeking is asynchronous. Without tracking it, a large drift makes every
    // frame issue a fresh seek that cancels the one still in flight, so the
    // decoder never delivers a frame and the picture freezes while time appears
    // to advance. Measured: 10 seeks in a row left the video on frame 0.
    bool seekPending;
    float seekIssuedAt;
    bool seekHooked;
    float appliedPlaybackSpeed = -1f;

    const float SeekTimeout = 1.5f;

    public VideoLevel Level => level;
    public float Distance => distance;
    public float VideoTime => videoTime;
    public float Progress => level != null && level.TotalDistance > 0f
        ? Mathf.Clamp01(distance / level.TotalDistance)
        : 0f;
    public bool IsFinished => finished;
    public bool IsReady => level != null && background != null && background.IsPrepared;
    public float SpeedMultiplier
    {
        get => speedMultiplier;
        set => speedMultiplier = Mathf.Clamp(value, 0f, MaxPlaybackSpeed);
    }

    /// <summary>The mode actually in force after Auto resolves.</summary>
    /// <remarks>
    /// In a vehicle the phone's own motion drives progress; at a desk the clip
    /// plays at its own pace. Mirrors VehicleSpeedController's Auto, which picks
    /// Mock in the editor and GPS on device, so the whole chain follows the
    /// hardware it is running on.
    /// </remarks>
    public PlaybackMode ResolvedMode => playbackMode != PlaybackMode.Auto
        ? playbackMode
        : Application.isMobilePlatform ? PlaybackMode.VehicleSpeed : PlaybackMode.NativeRate;

    public event Action LevelCompleted;

    void Awake()
    {
        level = VideoLevel.LoadFromStreamingAssets(levelFileName);
        if (level == null)
        {
            enabled = false;
            return;
        }

        if (background == null)
        {
            background = FindFirstObjectByType<VideoBackground>();
        }

        if (background != null)
        {
            background.Begin(level.PlaybackFileName);
            LoadOccluderAtlas();
        }

        if (ResolvedMode == PlaybackMode.VehicleSpeed)
        {
            EnsureVehicleController();
        }
    }

    /// <summary>
    /// Hand the occluder silhouette atlas to the background, when the level
    /// ships one. Without it, occluders fall back to plain rectangles.
    /// </summary>
    void LoadOccluderAtlas()
    {
        string fileName = level.OccluderMaskFileName;
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        byte[] bytes = VideoLevel.ReadStreamingAsset(fileName,
            "The level names an occluder atlas; copy-to-unity.sh ships it.");
        if (bytes == null)
        {
            return;
        }

        // Not readable/compressed like an imported asset: it is sampled as a
        // plain alpha source, and LoadImage keeps it point-exact.
        var atlas = new Texture2D(2, 2, TextureFormat.R8, false)
        {
            name = "OccluderAtlas",
            wrapMode = TextureWrapMode.Clamp
        };
        if (!atlas.LoadImage(bytes))
        {
            Debug.LogError($"LevelDirector: could not decode occluder atlas '{fileName}'.");
            return;
        }

        background.SetOccluderMask(atlas);
    }

    /// <summary>
    /// Find or build the vehicle-speed stack, so a scene that never wired one
    /// still moves with the vehicle on device.
    /// </summary>
    /// <remarks>
    /// Built inactive first: VehicleSpeedController reads its configuration in
    /// Awake and disables itself when missing, so every field has to be set by
    /// reflection before activation - the same order PlayModeTestFactory uses.
    /// A freshly created RunnerConfiguration carries the tuned serialized
    /// defaults, which is exactly what an unwired scene should get.
    /// </remarks>
    void EnsureVehicleController()
    {
        if (vehicleSpeedController != null)
        {
            return;
        }

        vehicleSpeedController = FindFirstObjectByType<VehicleSpeedController>();
        if (vehicleSpeedController != null)
        {
            return;
        }

        var holder = new GameObject("VehicleSpeed (auto)");
        holder.SetActive(false);

        var mock = holder.AddComponent<MockSpeedProvider>();
        var permission = holder.AddComponent<LocationPermissionService>();
        var gps = holder.AddComponent<UnityGpsSpeedProvider>();
        var motion = holder.AddComponent<UnityDeviceMotionProvider>();
        var controller = holder.AddComponent<VehicleSpeedController>();

        var configuration = ScriptableObject.CreateInstance<RunnerConfiguration>();
        SetPrivateField(gps, "configuration", configuration);
        SetPrivateField(gps, "permissionService", permission);
        SetPrivateField(controller, "configuration", configuration);
        SetPrivateField(controller, "mockSpeedProvider", mock);
        SetPrivateField(controller, "unityGpsSpeedProvider", gps);
        SetPrivateField(controller, "deviceMotionProvider", motion);

        holder.SetActive(true);
        vehicleSpeedController = controller;
    }

    static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(
            name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
        {
            Debug.LogError($"LevelDirector: no field '{name}' on {target.GetType().Name}");
            return;
        }

        field.SetValue(target, value);
    }

    void Update()
    {
        if (!IsReady || finished)
        {
            return;
        }

        VideoPlayer player = background.Player;
        if (!started && !HoldForStart(player))
        {
            return;
        }

        if (!player.isPlaying)
        {
            player.Play();
        }

        if (ResolvedMode == PlaybackMode.NativeRate)
        {
            // The video leads and distance follows it. Forcing the decoder to a
            // computed time instead means any moment it cannot keep up - a fast
            // multiplier, a frame hitch - shows up as drift, and drift used to
            // trigger a seek that never completed, freezing playback outright.
            // Reading its clock cannot drift by construction.
            SetPlaybackSpeed(player, speedMultiplier);
            videoTime = (float)player.time;
            distance = level.DistanceAtTime(videoTime);
        }
        else
        {
            // Real motion decides progress here, so the video has to be driven.
            AdvanceDistance(Time.deltaTime);
            videoTime = level.TimeAtDistance(distance);
            SynchronizeVideo(player, videoTime);
        }

        if (distance >= level.TotalDistance || videoTime >= level.Duration - 0.05f)
        {
            finished = true;
            player.Pause();
            LevelCompleted?.Invoke();
        }
    }

    /// <summary>
    /// Hold at the clip's first frame until the decoder is genuinely ready.
    /// </summary>
    /// <returns>True once the run may begin.</returns>
    /// <remarks>
    /// Play is issued once so the decoder delivers frame 0, then the player is
    /// paused on it for <see cref="startHold"/> seconds. Distance never
    /// advances during the hold, so the ball stands at the start of the level
    /// on a visible frame instead of climbing an invisible one.
    /// </remarks>
    bool HoldForStart(VideoPlayer player)
    {
        if (firstFrameAt < 0f)
        {
            if (player.frame < 0)
            {
                // No frame delivered yet: ask for one and keep waiting.
                if (!player.isPlaying)
                {
                    player.Play();
                }

                return false;
            }

            player.Pause();
            firstFrameAt = Time.unscaledTime;
            return false;
        }

        if (Time.unscaledTime - firstFrameAt < startHold)
        {
            return false;
        }

        started = true;
        return true;
    }

    void AdvanceDistance(float deltaTime)
    {
        if (ResolvedMode == PlaybackMode.NativeRate)
        {
            // Advance along the clip's own timeline, then convert back. Going
            // through distance rather than setting video time directly keeps this
            // mode on exactly the same code path as VehicleSpeed.
            float nextTime = level.TimeAtDistance(distance) + deltaTime * speedMultiplier;
            distance = level.DistanceAtTime(nextTime);
            return;
        }

        float gameSpeed = vehicleSpeedController != null ? vehicleSpeedController.GameSpeed : 0f;
        float nativeRate = referenceGameSpeed > 0f ? gameSpeed / referenceGameSpeed : 0f;
        float advancedTime = level.TimeAtDistance(distance) + deltaTime * nativeRate;
        distance = level.DistanceAtTime(advancedTime);
    }

    void SynchronizeVideo(VideoPlayer player, float targetTime)
    {
        if (!seekHooked)
        {
            player.seekCompleted += OnSeekCompleted;
            seekHooked = true;
        }

        float drift = (float)player.time - targetTime;
        float desiredRate = Mathf.Max(0f, CurrentNativeRate());

        // Always set the rate, even mid-seek. An earlier version returned early
        // while a seek was pending, so a seek that never completed left the rate
        // frozen and the video stuck for good.
        SetPlaybackSpeed(player, desiredRate - drift * 0.5f);

        bool seekInFlight = seekPending && Time.unscaledTime - seekIssuedAt < SeekTimeout;
        if (seekInFlight)
        {
            return;
        }

        seekPending = false;

        // Rate correction handles ordinary drift. Seeking is reserved for gaps
        // too large to close that way, which rate alone would take many seconds
        // to absorb.
        if (Mathf.Abs(drift) > ResyncThreshold)
        {
            IssueSeek(player, targetTime);
        }
    }

    /// <summary>
    /// Assign playback speed only when it changes.
    /// </summary>
    /// <remarks>
    /// Every assignment reaches into the native player, and writing the same
    /// value 60 times a second is work for nothing.
    /// </remarks>
    void SetPlaybackSpeed(VideoPlayer player, float rate)
    {
        float clamped = Mathf.Clamp(rate, 0f, MaxPlaybackSpeed);
        if (Mathf.Abs(clamped - appliedPlaybackSpeed) < 0.01f)
        {
            return;
        }

        appliedPlaybackSpeed = clamped;
        player.playbackSpeed = clamped;
    }

    void IssueSeek(VideoPlayer player, float targetTime)
    {
        seekPending = true;
        seekIssuedAt = Time.unscaledTime;

        // By frame rather than by time: frame seeks land on an exact decodable
        // frame, where a time seek can be rounded to somewhere nearby.
        if (player.frameRate > 0f)
        {
            player.frame = (long)(targetTime * player.frameRate);
        }
        else
        {
            player.time = targetTime;
        }
    }

    void OnSeekCompleted(VideoPlayer source)
    {
        seekPending = false;
    }

    float CurrentNativeRate()
    {
        float gameSpeed = vehicleSpeedController != null ? vehicleSpeedController.GameSpeed : 0f;
        return referenceGameSpeed > 0f ? gameSpeed / referenceGameSpeed : 0f;
    }

    /// <summary>
    /// Jump to a point in the clip, in video seconds.
    /// </summary>
    /// <remarks>
    /// Takes seconds rather than distance because that is the unit timeline.json
    /// is written in, so a cue can be checked by typing in the same number that
    /// authored it instead of replaying from the start.
    /// </remarks>
    public void SeekToTime(float seconds)
    {
        if (level == null)
        {
            return;
        }

        float clamped = Mathf.Clamp(seconds, 0f, level.Duration);
        distance = level.DistanceAtTime(clamped);
        videoTime = clamped;
        finished = false;

        if (background != null && background.IsPrepared)
        {
            IssueSeek(background.Player, clamped);
        }
    }

    public void Restart()
    {
        distance = 0f;
        videoTime = 0f;
        finished = false;
        // Restart re-runs the start hold: the seek back to frame 0 goes
        // through the same cold path as the first load.
        started = false;
        firstFrameAt = -1f;

        if (background != null && background.IsPrepared)
        {
            IssueSeek(background.Player, 0f);
            background.Player.Play();
        }
    }
}
