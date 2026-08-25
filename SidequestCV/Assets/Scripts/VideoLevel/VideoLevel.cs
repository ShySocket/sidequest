using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// A loaded level, with the lookups the game needs every frame.
/// </summary>
/// <remarks>
/// The two directions of the time/distance map are both needed and both hot:
/// the game integrates speed into a distance and asks which video time to show,
/// while native-rate playback goes the other way. Both arrays are sorted, so
/// both lookups are a binary search.
/// </remarks>
public sealed class VideoLevel
{
    readonly VideoLevelData data;
    readonly float[] mapTimes;
    readonly float[] mapDistances;
    readonly float[] pathDistances;
    readonly float[] speedDistances;
    readonly float[] speedValues;
    readonly float[] ambientDistances;
    readonly float[] ambientR;
    readonly float[] ambientG;
    readonly float[] ambientB;
    readonly Vector2 depthRange;

    VideoLevel(VideoLevelData data)
    {
        this.data = data;

        mapTimes = new float[data.timeToDistance.Count];
        mapDistances = new float[data.timeToDistance.Count];
        for (int i = 0; i < data.timeToDistance.Count; i++)
        {
            mapTimes[i] = data.timeToDistance[i].t;
            mapDistances[i] = data.timeToDistance[i].d;
        }

        pathDistances = new float[data.path.Count];
        for (int i = 0; i < data.path.Count; i++)
        {
            pathDistances[i] = data.path[i].d;
        }

        int ambientCount = data.ambient != null ? data.ambient.Count : 0;
        ambientDistances = new float[ambientCount];
        ambientR = new float[ambientCount];
        ambientG = new float[ambientCount];
        ambientB = new float[ambientCount];
        for (int i = 0; i < ambientCount; i++)
        {
            ambientDistances[i] = data.ambient[i].d;
            ambientR[i] = data.ambient[i].r;
            ambientG[i] = data.ambient[i].g;
            ambientB[i] = data.ambient[i].b;
        }

        depthRange = ComputeDepthRange();

        int speedCount = data.screenSpeed != null ? data.screenSpeed.Count : 0;
        speedDistances = new float[speedCount];
        speedValues = new float[speedCount];
        for (int i = 0; i < speedCount; i++)
        {
            speedDistances[i] = data.screenSpeed[i].d;
            speedValues[i] = data.screenSpeed[i].v;
        }
    }

    public IReadOnlyList<VideoLevelEvent> Events => data.events;
    public float TotalDistance => data.totalDistance;

    /// <summary>Diameter the marker was drawn at; 0 when no marker was used.</summary>
    public float MarkerDiameter => data.markerDiameter;
    public float Duration => data.source != null ? data.source.duration : 0f;
    public float CharacterColumn => data.characterColumn;

    /// <summary>-1 travels right-to-left, +1 left-to-right. Never 0.</summary>
    public int TravelDirection => data.travelDirection >= 0 ? 1 : -1;
    /// <summary>The clip to play, which is a cheaper transcode of the one analysed.</summary>
    public string PlaybackFileName => !string.IsNullOrEmpty(data.playbackFile)
        ? data.playbackFile
        : data.source?.file;

    public static VideoLevel LoadFromStreamingAssets(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        string json;

        // On Android StreamingAssets lives inside the compressed APK, so it can
        // only be read through UnityWebRequest; everywhere else it is a file.
        if (path.Contains("://"))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            request.SendWebRequest();
            while (!request.isDone)
            {
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"VideoLevel: could not read {path}: {request.error}");
                return null;
            }

            json = request.downloadHandler.text;
        }
        else
        {
            if (!File.Exists(path))
            {
                Debug.LogError(
                    $"VideoLevel: no level file at {path}. Run "
                    + "'uv run analyze author ...' and copy the result into StreamingAssets.");
                return null;
            }

            json = File.ReadAllText(path);
        }

        return Parse(json);
    }

    public static VideoLevel Parse(string json)
    {
        VideoLevelData parsed = JsonUtility.FromJson<VideoLevelData>(json);
        if (parsed == null || parsed.path == null || parsed.path.Count < 2)
        {
            Debug.LogError("VideoLevel: level file is empty or malformed.");
            return null;
        }

        return new VideoLevel(parsed);
    }

    /// <summary>Video time at a distance. This is the query playback is driven by.</summary>
    public float TimeAtDistance(float distance)
    {
        return Interpolate(mapDistances, mapTimes, distance);
    }

    public float DistanceAtTime(float time)
    {
        return Interpolate(mapTimes, mapDistances, time);
    }

    /// <summary>Normalized screen y of the ground at a distance (0 = top of video).</summary>
    public float GroundAtDistance(float distance)
    {
        int index = UpperBound(pathDistances, distance);
        if (index <= 0)
        {
            return data.path[0].y;
        }

        if (index >= data.path.Count)
        {
            return data.path[data.path.Count - 1].y;
        }

        VideoLevelPathPoint a = data.path[index - 1];
        VideoLevelPathPoint b = data.path[index];
        float span = b.d - a.d;
        float alpha = span > 0f ? Mathf.Clamp01((distance - a.d) / span) : 0f;
        return Mathf.Lerp(a.y, b.y, alpha);
    }

    /// <summary>Horizontal position of the character at a distance, 0..1.</summary>
    /// <remarks>
    /// Per-sample rather than one fixed column: the tracked marker traverses the
    /// screen right to left over the run. Levels authored before the marker
    /// existed carry no x, and fall back to the fixed column.
    /// </remarks>
    public float ColumnAtDistance(float distance)
    {
        int index = UpperBound(pathDistances, distance);
        if (index <= 0)
        {
            return Column(data.path[0]);
        }

        if (index >= data.path.Count)
        {
            return Column(data.path[data.path.Count - 1]);
        }

        VideoLevelPathPoint a = data.path[index - 1];
        VideoLevelPathPoint b = data.path[index];
        float span = b.d - a.d;
        float alpha = span > 0f ? Mathf.Clamp01((distance - a.d) / span) : 0f;
        return Mathf.Lerp(Column(a), Column(b), alpha);
    }

    float Column(VideoLevelPathPoint point)
    {
        return point.x > 0f ? point.x : data.characterColumn;
    }

    /// <summary>Light the footage casts where the character stands, ~white in ordinary daylight.</summary>
    public Color AmbientAtDistance(float distance)
    {
        if (ambientDistances.Length == 0)
        {
            return Color.white;
        }

        return new Color(
            Interpolate(ambientDistances, ambientR, distance),
            Interpolate(ambientDistances, ambientG, distance),
            Interpolate(ambientDistances, ambientB, distance));
    }

    /// <summary>Ground-line heights spanning far (x) to near (y), for perspective scale.</summary>
    /// <remarks>
    /// Measured from this level's own path percentiles rather than a tuned
    /// constant, so the ball's size range adapts to how much depth the clip's
    /// path actually covers.
    /// </remarks>
    public Vector2 PathDepthRange => depthRange;

    Vector2 ComputeDepthRange()
    {
        if (data.path == null || data.path.Count < 20)
        {
            return new Vector2(0.45f, 0.95f);
        }

        var heights = new float[data.path.Count];
        for (int i = 0; i < data.path.Count; i++)
        {
            heights[i] = data.path[i].y;
        }

        System.Array.Sort(heights);
        float far = heights[(int)(heights.Length * 0.05f)];
        float near = heights[(int)(heights.Length * 0.95f)];
        // A nearly flat path gives no usable range; keep the tuned default.
        return near - far < 0.08f ? new Vector2(0.45f, 0.95f) : new Vector2(far, near);
    }

    /// <summary>Which surface the character is on: rail, hedge, sidewalk, grass, floor.</summary>
    /// <remarks>
    /// Named by the authored timeline and given an exact height by the extracted
    /// ledges, so it is what the character is genuinely standing on rather than
    /// whatever happened to be nearest.
    /// </remarks>
    public string SurfaceAtDistance(float distance)
    {
        int index = Mathf.Clamp(UpperBound(pathDistances, distance) - 1, 0, data.path.Count - 1);
        string surface = data.path[index].s;
        return string.IsNullOrEmpty(surface) ? "floor" : surface;
    }

    /// <summary>How fast the world slides past, in frame widths per second.</summary>
    /// <remarks>
    /// Zero when the level carries no speed track, which reads as a ball that
    /// slides rather than rolls - visibly wrong, and so preferable to inventing
    /// a rate that quietly disagrees with the footage.
    /// </remarks>
    public float ScreenSpeedAtDistance(float distance)
    {
        return speedDistances.Length == 0 ? 0f : Interpolate(speedDistances, speedValues, distance);
    }

    public bool IsHiddenAtDistance(float distance)
    {
        for (int i = 0; i < data.hidden.Count; i++)
        {
            if (distance >= data.hidden[i].startDistance && distance <= data.hidden[i].endDistance)
            {
                return true;
            }
        }

        return false;
    }

    static float Interpolate(float[] xs, float[] ys, float x)
    {
        if (xs.Length == 0)
        {
            return 0f;
        }

        int index = UpperBound(xs, x);
        if (index <= 0)
        {
            return ys[0];
        }

        if (index >= xs.Length)
        {
            return ys[ys.Length - 1];
        }

        float span = xs[index] - xs[index - 1];
        float alpha = span > 0f ? (x - xs[index - 1]) / span : 0f;
        return Mathf.Lerp(ys[index - 1], ys[index], alpha);
    }

    /// <summary>Index of the first element strictly greater than <paramref name="value"/>.</summary>
    static int UpperBound(float[] values, float value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (values[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
