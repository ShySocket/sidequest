using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes every GPS reading and motion sample of a live run to a
/// <see cref="SpeedTrace"/> file, so one real drive becomes a desk-replayable
/// fixture (via <see cref="RecordedTraceProvider"/>).
/// </summary>
/// <remarks>
/// Sub-second braking feel cannot be judged from code, and tuning it by
/// driving after every build is a brutal loop. This recorder is the way out:
/// drive once, tune at the desk against the exact signal the road produced.
///
/// It taps the providers' existing events, so it hears precisely what the
/// controller hears and costs nothing when tracking is not running. The file
/// opens lazily on the first sample - in the editor no sensor ever fires, so
/// no empty files pile up - and lands in persistentDataPath/speed-traces/,
/// which iOS exposes through the Files app for copying off the phone.
/// </remarks>
public sealed class SpeedTraceRecorder : MonoBehaviour
{
    [SerializeField] private UnityGpsSpeedProvider gpsProvider;
    [SerializeField] private UnityDeviceMotionProvider motionProvider;

    [Tooltip("Master switch, so a scene can ship with the recorder wired but off.")]
    [SerializeField] private bool record = true;

    // Buffered writes with a periodic flush: per-sample flushing is I/O the
    // phone feels, while losing at most a second of tail on a crash is not.
    private const float FlushIntervalSeconds = 1f;

    private readonly StringBuilder lineBuilder = new StringBuilder(128);
    private StreamWriter writer;
    private double startTime = -1d;
    private float lastFlushAt;

    public string CurrentFilePath { get; private set; }

    private void OnEnable()
    {
        if (gpsProvider != null)
        {
            gpsProvider.ValidSpeedReceived += OnGpsReading;
        }

        if (motionProvider != null)
        {
            motionProvider.MotionReceived += OnMotionReading;
        }
    }

    private void OnDisable()
    {
        if (gpsProvider != null)
        {
            gpsProvider.ValidSpeedReceived -= OnGpsReading;
        }

        if (motionProvider != null)
        {
            motionProvider.MotionReceived -= OnMotionReading;
        }

        CloseFile();
    }

    private void Update()
    {
        if (writer == null || Time.unscaledTime - lastFlushAt < FlushIntervalSeconds)
        {
            return;
        }

        lastFlushAt = Time.unscaledTime;
        writer.Flush();
    }

    private void OnGpsReading(VehicleSpeedReading reading)
    {
        if (!EnsureFile(reading.LocalTimestamp))
        {
            return;
        }

        writer.WriteLine(SpeedTrace.FormatGpsLine(
            lineBuilder,
            (float)(reading.LocalTimestamp - startTime),
            reading.SpeedMetersPerSecond,
            reading.HorizontalAccuracyMeters,
            reading.CourseDegrees,
            gpsProvider != null ? gpsProvider.LastLatitude : 0d,
            gpsProvider != null ? gpsProvider.LastLongitude : 0d));
    }

    private void OnMotionReading(DeviceMotionReading reading)
    {
        if (!EnsureFile(reading.LocalTimestamp))
        {
            return;
        }

        writer.WriteLine(SpeedTrace.FormatMotionLine(
            lineBuilder,
            (float)(reading.LocalTimestamp - startTime),
            reading.UserAccelerationMetersPerSecondSquared,
            reading.Attitude));
    }

    private bool EnsureFile(double firstSampleTime)
    {
        if (!record)
        {
            return false;
        }

        if (writer != null)
        {
            return true;
        }

        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath, "speed-traces");
            Directory.CreateDirectory(directory);
            CurrentFilePath = Path.Combine(
                directory,
                "trace-" + DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            writer = new StreamWriter(CurrentFilePath, false, Encoding.UTF8);
            writer.WriteLine(SpeedTrace.HeaderLine);
            startTime = firstSampleTime;
            lastFlushAt = Time.unscaledTime;
            Debug.Log($"SpeedTraceRecorder: recording to {CurrentFilePath}");
            return true;
        }
        catch (Exception exception)
        {
            // Storage trouble must never take the run down with it.
            Debug.LogError($"SpeedTraceRecorder: could not open file: {exception.Message}");
            record = false;
            writer = null;
            return false;
        }
    }

    private void CloseFile()
    {
        if (writer == null)
        {
            return;
        }

        writer.Flush();
        writer.Dispose();
        writer = null;
        startTime = -1d;
        Debug.Log($"SpeedTraceRecorder: saved {CurrentFilePath}");
    }
}
