using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Replays a recorded drive (see <see cref="SpeedTrace"/>) as if the phone
/// were on the road: GPS readings and motion samples are re-emitted at their
/// recorded moments, stamped with current local time.
/// </summary>
/// <remarks>
/// This is the editor's stand-in for a vehicle. The whole speed pipeline
/// downstream - estimator, filter, mapper, the director's rate shaping - runs
/// unmodified, so what the desk shows is what the road will feel like. Drop a
/// recording at StreamingAssets/<see cref="traceFileName"/> and the
/// controller's Auto mode picks replay over the mock on its own.
/// </remarks>
public sealed class RecordedTraceProvider : MonoBehaviour, IVehicleSpeedProvider
{
    [Tooltip("Trace file inside StreamingAssets, recorded by SpeedTraceRecorder.")]
    [SerializeField] private string traceFileName = "speed-trace.txt";

    [Tooltip("Start the trace over when it runs out, instead of going silent.")]
    [SerializeField] private bool loop;

    private SpeedTrace trace;
    private bool loadAttempted;
    private bool isTracking;
    private double startedAt;
    private double traceOffset;
    private int nextIndex;

    public bool HasReceivedValidSpeed { get; private set; }
    public bool IsTrackingAvailable => isTracking && trace != null;
    public float LastKnownSpeedMetersPerSecond { get; private set; }
    public GpsTrackingState TrackingState { get; private set; } =
        GpsTrackingState.Initializing;
    public event Action<VehicleSpeedReading> ValidSpeedReceived;

    /// <summary>Motion samples re-emitted alongside the GPS readings, mirroring
    /// UnityDeviceMotionProvider's event so the estimator cannot tell replay
    /// from road.</summary>
    public event Action<DeviceMotionReading> MotionReceived;

    /// <summary>
    /// Whether a trace file is present, without starting playback. Cheap
    /// enough for the controller's Auto resolution to call while deciding
    /// between replay and the mock.
    /// </summary>
    public bool HasTrace
    {
        get
        {
            if (trace != null)
            {
                return true;
            }

            // Peek at the file rather than loading it: a missing trace is the
            // normal case at a desk, and the StreamingAssets reader logs an
            // error for every miss.
            string path = Path.Combine(
                Application.streamingAssetsPath, traceFileName);
            return !path.Contains("://") && File.Exists(path);
        }
    }

    public void StartTracking()
    {
        if (isTracking)
        {
            return;
        }

        EnsureLoaded();
        if (trace == null)
        {
            TrackingState = GpsTrackingState.SignalLost;
            return;
        }

        isTracking = true;
        startedAt = Time.realtimeSinceStartupAsDouble;
        traceOffset = 0d;
        nextIndex = 0;
        TrackingState = GpsTrackingState.Tracking;
    }

    public void StopTracking()
    {
        isTracking = false;
        TrackingState = GpsTrackingState.SignalLost;
    }

    private void Update()
    {
        if (!isTracking || trace == null)
        {
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        float elapsed = (float)(now - startedAt - traceOffset);
        var entries = trace.Entries;

        while (nextIndex < entries.Count
            && entries[nextIndex].Time <= elapsed)
        {
            // Stamped at the sample's own scheduled moment, not this frame's
            // clock: several samples flushed in one frame would otherwise
            // share a timestamp, and the estimator drops non-increasing ones.
            Emit(
                entries[nextIndex],
                startedAt + traceOffset + entries[nextIndex].Time);
            nextIndex++;
        }

        if (nextIndex < entries.Count)
        {
            return;
        }

        if (loop)
        {
            // Shift the clock instead of resetting startedAt so timestamps
            // handed downstream keep increasing - the estimator drops
            // anything that does not.
            traceOffset += trace.Duration;
            nextIndex = 0;
            return;
        }

        // The drive is over. Go silent the way a tunnel does, so the
        // downstream stale/missing handling is exercised rather than the
        // last speed being held as gospel forever.
        isTracking = false;
        TrackingState = GpsTrackingState.SignalLost;
    }

    private void Emit(SpeedTrace.Entry entry, double localTime)
    {
        if (entry.Kind == SpeedTrace.SampleKind.Gps)
        {
            LastKnownSpeedMetersPerSecond = entry.Speed;
            HasReceivedValidSpeed = true;
            ValidSpeedReceived?.Invoke(new VehicleSpeedReading(
                entry.Speed,
                entry.Accuracy,
                localTime,
                entry.Course));
        }
        else
        {
            MotionReceived?.Invoke(new DeviceMotionReading(
                entry.Acceleration,
                entry.Attitude,
                localTime));
        }
    }

    private void EnsureLoaded()
    {
        if (trace != null || loadAttempted)
        {
            return;
        }

        loadAttempted = true;
        byte[] bytes = VideoLevel.ReadStreamingAsset(traceFileName,
            "Record one with SpeedTraceRecorder on device, then copy it here.");
        if (bytes == null)
        {
            return;
        }

        trace = SpeedTrace.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        if (trace == null)
        {
            Debug.LogError(
                $"RecordedTraceProvider: '{traceFileName}' held no readable samples.");
        }
    }
}
