using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// The recorded drive format: one line per GPS or motion sample, timestamped
/// relative to the start of the recording.
/// </summary>
/// <remarks>
/// This is the bridge between a real drive and a desk: SpeedTraceRecorder
/// writes it on the phone, RecordedTraceProvider replays it in the editor
/// through the exact estimator/filter/mapper pipeline the device runs. Feel
/// tuning that used to cost a build and a drive per attempt becomes a slider
/// change against the same braking the road actually produced.
///
/// Plain comma-separated text rather than JSON: it is written 50+ times a
/// second on a phone (no per-line serializer garbage), trivially inspectable,
/// and safe to truncate - a recording cut off mid-line by a battery death
/// loses only that line. All numbers are invariant-culture. Lines:
///   g,&lt;t&gt;,&lt;speed m/s&gt;,&lt;accuracy m&gt;,&lt;course deg&gt;,&lt;lat&gt;,&lt;lon&gt;
///   m,&lt;t&gt;,&lt;ax&gt;,&lt;ay&gt;,&lt;az&gt;,&lt;qx&gt;,&lt;qy&gt;,&lt;qz&gt;,&lt;qw&gt;
/// Lat/lon ride along for future work on the speed window itself; replay only
/// needs the reading the window produced. '#' lines are comments.
/// </remarks>
public sealed class SpeedTrace
{
    public const string HeaderLine = "# sidequest speed trace v1";

    public enum SampleKind
    {
        Gps,
        Motion
    }

    public readonly struct Entry
    {
        public Entry(
            float time,
            SampleKind kind,
            float speed,
            float accuracy,
            float course,
            Vector3 acceleration,
            Quaternion attitude)
        {
            Time = time;
            Kind = kind;
            Speed = speed;
            Accuracy = accuracy;
            Course = course;
            Acceleration = acceleration;
            Attitude = attitude;
        }

        public float Time { get; }
        public SampleKind Kind { get; }
        public float Speed { get; }
        public float Accuracy { get; }
        public float Course { get; }
        public Vector3 Acceleration { get; }
        public Quaternion Attitude { get; }
    }

    private readonly List<Entry> entries;

    private SpeedTrace(List<Entry> entries)
    {
        this.entries = entries;
    }

    public IReadOnlyList<Entry> Entries => entries;
    public float Duration =>
        entries.Count > 0 ? entries[entries.Count - 1].Time : 0f;

    /// <summary>Parse a trace; null when no sample line survives.</summary>
    public static SpeedTrace Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var parsed = new List<Entry>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split(',');
            if (fields[0] == "g" && fields.Length >= 5)
            {
                if (TryParse(fields[1], out float time)
                    && TryParse(fields[2], out float speed)
                    && TryParse(fields[3], out float accuracy)
                    && TryParse(fields[4], out float course))
                {
                    parsed.Add(new Entry(
                        time,
                        SampleKind.Gps,
                        speed,
                        accuracy,
                        course,
                        Vector3.zero,
                        Quaternion.identity));
                }
            }
            else if (fields[0] == "m" && fields.Length >= 9)
            {
                if (TryParse(fields[1], out float time)
                    && TryParse(fields[2], out float ax)
                    && TryParse(fields[3], out float ay)
                    && TryParse(fields[4], out float az)
                    && TryParse(fields[5], out float qx)
                    && TryParse(fields[6], out float qy)
                    && TryParse(fields[7], out float qz)
                    && TryParse(fields[8], out float qw))
                {
                    parsed.Add(new Entry(
                        time,
                        SampleKind.Motion,
                        0f,
                        0f,
                        float.NaN,
                        new Vector3(ax, ay, az),
                        new Quaternion(qx, qy, qz, qw)));
                }
            }
        }

        return parsed.Count > 0 ? new SpeedTrace(parsed) : null;
    }

    public static string FormatGpsLine(
        StringBuilder builder,
        float time,
        float speed,
        float accuracy,
        float course,
        double latitude,
        double longitude)
    {
        builder.Clear();
        builder.Append("g,");
        AppendFloat(builder, time).Append(',');
        AppendFloat(builder, speed).Append(',');
        AppendFloat(builder, accuracy).Append(',');
        AppendFloat(builder, course).Append(',');
        builder.Append(latitude.ToString("R", CultureInfo.InvariantCulture))
            .Append(',');
        builder.Append(longitude.ToString("R", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public static string FormatMotionLine(
        StringBuilder builder,
        float time,
        Vector3 acceleration,
        Quaternion attitude)
    {
        builder.Clear();
        builder.Append("m,");
        AppendFloat(builder, time).Append(',');
        AppendFloat(builder, acceleration.x).Append(',');
        AppendFloat(builder, acceleration.y).Append(',');
        AppendFloat(builder, acceleration.z).Append(',');
        AppendFloat(builder, attitude.x).Append(',');
        AppendFloat(builder, attitude.y).Append(',');
        AppendFloat(builder, attitude.z).Append(',');
        AppendFloat(builder, attitude.w);
        return builder.ToString();
    }

    private static StringBuilder AppendFloat(StringBuilder builder, float value)
    {
        return builder.Append(
            value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static bool TryParse(string field, out float value)
    {
        return float.TryParse(
            field,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }
}
