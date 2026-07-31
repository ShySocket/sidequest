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
        /// <summary>Advance at the clip's own rate, scaled by <see cref="speedMultiplier"/>.</summary>
        NativeRate,

        /// <summary>Advance from real vehicle motion via VehicleSpeedController.</summary>
        VehicleSpeed
    }

    const float ResyncThreshold = 1.5f;
    const float MaxPlaybackSpeed = 4f;

    [SerializeField] string levelFileName = "IMG_3775.authored.json";
    [SerializeField] VideoBackground background;
    [SerializeField] PlaybackMode playbackMode = PlaybackMode.NativeRate;

    [Tooltip("Scales playback in NativeRate mode. 1 = the speed the clip was filmed at.")]
    [Range(0.25f, 3f)]
    [SerializeField] float speedMultiplier = 1f;

    [Tooltip("VehicleSpeed mode only: game units per second that equal the clip's own pace.")]
    [SerializeField] float referenceGameSpeed = 6f;

    [SerializeField] VehicleSpeedController vehicleSpeedController;

    VideoLevel level;
    float distance;
    float videoTime;
    bool finished;

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
        }
    }

    void Update()
    {
        if (!IsReady || finished)
        {
            return;
        }

        VideoPlayer player = background.Player;
        if (!player.isPlaying)
        {
            player.Play();
        }

        if (playbackMode == PlaybackMode.NativeRate)
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

    void AdvanceDistance(float deltaTime)
    {
        if (playbackMode == PlaybackMode.NativeRate)
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

        if (background != null && background.IsPrepared)
        {
            IssueSeek(background.Player, 0f);
            background.Player.Play();
        }
    }
}
