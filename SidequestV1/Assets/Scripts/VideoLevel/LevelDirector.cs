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

    [Header("Vehicle playback shaping")]
    [Tooltip("Slowest the video plays while the vehicle counts as moving. " +
        "Below ~0.8 a 30fps clip drops under 24 effective fps and judders.")]
    [SerializeField] float minimumMovingRate = 0.8f;

    [Tooltip("Fastest the video plays, however fast the vehicle goes.")]
    [SerializeField] float maximumMovingRate = 1.2f;

    [Tooltip("Raw vehicle rate (GameSpeed / reference) above which the video " +
        "starts moving.")]
    [SerializeField] float movingEntryRate = 0.15f;

    [Tooltip("Raw vehicle rate below which a moving video stops. Kept under " +
        "the entry rate so stop-and-go creep cannot flap pause/play.")]
    [SerializeField] float stoppedEntryRate = 0.08f;

    [Tooltip("Seconds for the displayed rate to ease up toward a faster target.")]
    [SerializeField] float rateRiseTime = 0.5f;

    [Tooltip("Seconds for the displayed rate to ease down toward a slower target.")]
    [SerializeField] float rateFallTime = 0.3f;

    [Tooltip("The ball, for holding the rate up while a jump or dodge is " +
        "mid-flight. Found in the scene when unset.")]
    [SerializeField] VideoRunnerCharacter character;

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
    bool crashHold;
    float firstFrameAt = -1f;

    // Seeking is asynchronous. Without tracking it, a large drift makes every
    // frame issue a fresh seek that cancels the one still in flight, so the
    // decoder never delivers a frame and the picture freezes while time appears
    // to advance. Measured: 10 seeks in a row left the video on frame 0.
    bool seekPending;
    float seekIssuedAt;
    bool seekHooked;
    float appliedPlaybackSpeed = -1f;

    // Owns the moving-band clamp, stop hysteresis, and the mid-action hold.
    // Its output drives distance AND the video together, so the two cannot
    // disagree about whether the world is moving.
    PlaybackRateShaper rateShaper;

    const float SeekTimeout = 1.5f;

    public VideoLevel Level => level;
    public VehicleSpeedController VehicleController => vehicleSpeedController;
    public float Distance => distance;
    public float VideoTime => videoTime;
    public float Progress => level != null && level.TotalDistance > 0f
        ? Mathf.Clamp01(distance / level.TotalDistance)
        : 0f;
    public bool IsFinished => finished;
    public bool IsCrashHeld => crashHold;
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

        rateShaper = new PlaybackRateShaper(
            rateRiseTime,
            rateFallTime,
            minimumMovingRate,
            maximumMovingRate,
            movingEntryRate,
            stoppedEntryRate);

        if (character == null)
        {
            character = FindFirstObjectByType<VideoRunnerCharacter>();
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

        // A crash at a desk holds the impact frame: distance stops, the video
        // stops, and both stay stopped until Restart. Checked before the
        // keep-playing nudge below, which would otherwise un-pause it.
        if (crashHold)
        {
            if (player.isPlaying)
            {
                player.Pause();
            }

            return;
        }

        if (ResolvedMode == PlaybackMode.NativeRate)
        {
            if (!player.isPlaying)
            {
                player.Play();
            }

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
            // Real motion decides progress here, so the video has to be
            // driven. The shaped rate advances distance AND commands the
            // player, so the level and the picture always agree about
            // whether the world is moving - and drift below can only ever be
            // the decoder lagging its orders, never the trip outrunning the
            // clip.
            float shapedRate = rateShaper.Update(
                RawVehicleRate(),
                character != null && character.IsActionInProgress,
                Time.deltaTime);

            AdvanceDistance(Time.deltaTime, shapedRate);
            videoTime = level.TimeAtDistance(distance);

            if (shapedRate <= 0f)
            {
                // A landed stop is a real pause, not playbackSpeed 0: Pause
                // parks the decoder on a delivered frame instead of leaving
                // it idling against a zero rate.
                if (player.isPlaying)
                {
                    player.Pause();
                }
            }
            else
            {
                if (!player.isPlaying)
                {
                    player.Play();
                }

                SynchronizeVideo(player, videoTime, shapedRate);
            }
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

    void AdvanceDistance(float deltaTime, float nativeRate)
    {
        // Advance along the clip's own timeline, then convert back through
        // distance, which stays the authority everything else reads.
        float advancedTime = level.TimeAtDistance(distance) + deltaTime * nativeRate;
        distance = level.DistanceAtTime(advancedTime);
    }

    void SynchronizeVideo(VideoPlayer player, float targetTime, float desiredRate)
    {
        if (!seekHooked)
        {
            player.seekCompleted += OnSeekCompleted;
            seekHooked = true;
        }

        float drift = (float)player.time - targetTime;

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

    /// <summary>
    /// The unshaped vehicle rate: 1.0 means the car is matching the clip's
    /// own pace. This is the shaper's input, never the player's.
    /// </summary>
    float RawVehicleRate()
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

    /// <summary>The character crashed: hold the world still, or let it ride.</summary>
    /// <remarks>
    /// <paramref name="keepRolling"/> is true in vehicle mode, where the road
    /// outside keeps moving whatever happened on screen - freezing the video
    /// there would desynchronize it from the drive for good. The footage
    /// plays out and the crash is spectated instead.
    /// </remarks>
    public void NotifyCrash(bool keepRolling)
    {
        if (keepRolling)
        {
            return;
        }

        crashHold = true;
        if (background != null && background.IsPrepared)
        {
            background.Player.Pause();
        }
    }

    public void Restart()
    {
        distance = 0f;
        videoTime = 0f;
        finished = false;
        crashHold = false;
        rateShaper?.Reset();
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
