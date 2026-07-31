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

    const float ResyncThreshold = 0.30f;
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

        AdvanceDistance(Time.deltaTime);

        float targetTime = level.TimeAtDistance(distance);
        videoTime = targetTime;
        SynchronizeVideo(player, targetTime);

        if (distance >= level.TotalDistance || targetTime >= level.Duration - 0.05f)
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
        float drift = (float)player.time - targetTime;

        if (Mathf.Abs(drift) > ResyncThreshold)
        {
            player.time = targetTime;
            player.playbackSpeed = 1f;
            return;
        }

        // Nudge the rate to close small drift rather than seeking, which would be
        // visible as a hitch.
        float desiredRate = playbackMode == PlaybackMode.NativeRate
            ? speedMultiplier
            : Mathf.Max(0f, CurrentNativeRate());
        player.playbackSpeed = Mathf.Clamp(desiredRate - drift * 0.5f, 0f, MaxPlaybackSpeed);
    }

    float CurrentNativeRate()
    {
        float gameSpeed = vehicleSpeedController != null ? vehicleSpeedController.GameSpeed : 0f;
        return referenceGameSpeed > 0f ? gameSpeed / referenceGameSpeed : 0f;
    }

    public void Restart()
    {
        distance = 0f;
        videoTime = 0f;
        finished = false;

        if (background != null && background.IsPrepared)
        {
            background.Player.time = 0d;
            background.Player.Play();
        }
    }
}
