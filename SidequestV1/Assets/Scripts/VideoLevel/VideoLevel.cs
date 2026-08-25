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

    /// <summary>Frame rate of the source clip, for reporting frame numbers.</summary>
    public float Fps => data.source != null ? data.source.fps : 0f;
    public float CharacterColumn => data.characterColumn;

    /// <summary>-1 travels right-to-left, +1 left-to-right. Never 0.</summary>
    public int TravelDirection => data.travelDirection >= 0 ? 1 : -1;
    /// <summary>The clip to play, which is a cheaper transcode of the one analysed.</summary>
    public string PlaybackFileName => !string.IsNullOrEmpty(data.playbackFile)
        ? data.playbackFile
        : data.source?.file;

    public static VideoLevel LoadFromStreamingAssets(string fileName)
    {
        byte[] bytes = ReadStreamingAsset(fileName,
            "Run 'uv run analyze author ...' and copy the result into StreamingAssets.");
        if (bytes == null)
        {
            return null;
        }

        return Parse(System.Text.Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Raw bytes of a StreamingAssets file, or null with an error logged.</summary>
    /// <remarks>
    /// On Android StreamingAssets lives inside the compressed APK, so it can
    /// only be read through UnityWebRequest; everywhere else it is a file.
    /// Also used for the occluder silhouette atlas that ships with a level.
    /// </remarks>
    public static byte[] ReadStreamingAsset(string fileName, string hint = "")
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);

        if (path.Contains("://"))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            request.SendWebRequest();
            while (!request.isDone)
            {
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"VideoLevel: could not read {path}: {request.error} {hint}");
                return null;
            }

            return request.downloadHandler.data;
        }

        if (!File.Exists(path))
        {
            Debug.LogError($"VideoLevel: no file at {path}. {hint}");
            return null;
        }

        return File.ReadAllBytes(path);
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

    /// <summary>The line the ball's SIZE reads its depth from at a distance.</summary>
    /// <remarks>
    /// Equal to the ride line except where the level bakes a depth line: an
    /// elevated surface's top edge is position, not distance, so the ball
    /// riding a hedge keeps the size of the ground the bush stands on. Levels
    /// authored before this carry no <c>z</c> and fall back to <c>y</c>.
    /// </remarks>
    public float SizeGroundAtDistance(float distance)
    {
        int index = UpperBound(pathDistances, distance);
        if (index <= 0)
        {
            return SizeY(data.path[0]);
        }

        if (index >= data.path.Count)
        {
            return SizeY(data.path[data.path.Count - 1]);
        }

        VideoLevelPathPoint a = data.path[index - 1];
        VideoLevelPathPoint b = data.path[index];
        float span = b.d - a.d;
        float alpha = span > 0f ? Mathf.Clamp01((distance - a.d) / span) : 0f;
        return Mathf.Lerp(SizeY(a), SizeY(b), alpha);
    }

    static float SizeY(VideoLevelPathPoint point)
    {
        return point.z > 0f ? point.z : point.y;
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

    /// <summary>Name of the occluder silhouette atlas, or empty for none.</summary>
    public string OccluderMaskFileName => data.foregroundMaskFile;

    /// <summary>Foreground strip the ball currently passes behind, if any.</summary>
    /// <remarks>
    /// MIRRORED by strip_at in occlude.py, which the analyzer's occlusion
    /// verifier runs against the footage - change both together.
    ///
    /// Samples come from detections every few frames, which used to make the
    /// strip snap between them. Samples sharing an id are the same physical
    /// object, so between two of those the box is interpolated. Past a
    /// track's ends the last sighting may linger only HALF ITS OWN SAMPLE
    /// SPACING: an earlier version held it for a generous fixed tolerance,
    /// and the frozen silhouette sat where the pole used to be, erasing part
    /// of the ball with nothing visibly in front of it. Occlusion must turn
    /// off the moment the evidence does.
    ///
    /// <paramref name="maskUv"/> is the object's silhouette cell in the
    /// occluder atlas when <paramref name="hasMask"/> is true. The nearer
    /// sample's cell is used as-is rather than blended - silhouettes of the
    /// same pole a sixth of a second apart are near-identical, and the box
    /// carrying it is what actually moves.
    /// </remarks>
    public bool TryGetForeground(float distance, out Rect box, out Rect maskUv, out bool hasMask)
    {
        box = default;
        maskUv = default;
        hasMask = false;
        List<VideoLevelForegroundBox> samples = data.foreground;
        if (samples == null || samples.Count == 0)
        {
            return false;
        }

        // First sample at or beyond the current distance. The list is sorted
        // by construction (one candidate per detection frame, in time order).
        int after = 0;
        while (after < samples.Count && samples[after].d < distance)
        {
            after++;
        }
        int before = after - 1;

        VideoLevelForegroundBox a = before >= 0 ? samples[before] : null;
        VideoLevelForegroundBox b = after < samples.Count ? samples[after] : null;

        // id 0 means "no track identity" - an old-format level where every
        // sample deserializes to the default. Interpolating between two of
        // those could sweep the strip between two different poles, which is
        // exactly what track ids exist to prevent.
        float sameObjectGap = TotalDistance > 0f ? TotalDistance * 0.012f : 500f;
        if (a != null && b != null && a.id == b.id && a.id != 0 && b.d - a.d <= sameObjectGap)
        {
            float span = b.d - a.d;
            float alpha = span > 0f ? Mathf.Clamp01((distance - a.d) / span) : 0f;
            box = Rect.MinMaxRect(
                Mathf.Lerp(a.x1, b.x1, alpha),
                Mathf.Lerp(a.y1, b.y1, alpha),
                Mathf.Lerp(a.x2, b.x2, alpha),
                Mathf.Lerp(a.y2, b.y2, alpha));
            VideoLevelForegroundBox near = alpha < 0.5f ? a : b;
            hasMask = near.mask != 0;
            maskUv = Rect.MinMaxRect(near.u1, near.v1, near.u2, near.v2);
            return true;
        }

        int nearest = -1;
        float gap = float.MaxValue;
        if (a != null)
        {
            nearest = before;
            gap = distance - a.d;
        }
        if (b != null && b.d - distance < gap)
        {
            nearest = after;
            gap = b.d - distance;
        }

        if (nearest < 0 || gap > EdgeHold(samples, nearest))
        {
            return false;
        }

        // Carry the track's own velocity through the hold rather than
        // freezing: a near pole sweeps a tenth of the screen per detection
        // interval, and a frozen box left its silhouette biting the ball
        // where the pole no longer was (the 41.6s complaint). The interior
        // same-id neighbour supplies the velocity; a track with none stays
        // frozen, which the ball-overlap gate then bounds.
        VideoLevelForegroundBox held = samples[nearest];
        float heldX1 = held.x1, heldY1 = held.y1, heldX2 = held.x2, heldY2 = held.y2;
        int interior = distance > held.d ? nearest - 1 : nearest + 1;
        if (interior >= 0 && interior < samples.Count && samples[interior].id == held.id)
        {
            VideoLevelForegroundBox neighbour = samples[interior];
            float span = held.d - neighbour.d;
            if (Mathf.Abs(span) > 1e-6f)
            {
                float overshoot = (distance - held.d) / span;
                heldX1 = Mathf.Clamp01(heldX1 + (heldX1 - neighbour.x1) * overshoot);
                heldY1 = Mathf.Clamp01(heldY1 + (heldY1 - neighbour.y1) * overshoot);
                heldX2 = Mathf.Clamp01(heldX2 + (heldX2 - neighbour.x2) * overshoot);
                heldY2 = Mathf.Clamp01(heldY2 + (heldY2 - neighbour.y2) * overshoot);
            }
        }

        box = Rect.MinMaxRect(heldX1, heldY1, heldX2, heldY2);
        hasMask = held.mask != 0;
        maskUv = Rect.MinMaxRect(held.u1, held.v1, held.u2, held.v2);
        return true;
    }

    /// <summary>How far past a sighting the strip may linger, in distance.</summary>
    /// <remarks>
    /// Half the track's own local sample spacing: enough to bridge the
    /// half-interval before the first and after the last detection, never
    /// enough to leave a silhouette frozen while the world sweeps on.
    /// </remarks>
    static float EdgeHold(List<VideoLevelForegroundBox> samples, int index)
    {
        VideoLevelForegroundBox sample = samples[index];
        float spacing = float.MaxValue;
        if (index > 0 && samples[index - 1].id == sample.id)
        {
            spacing = Mathf.Min(spacing, sample.d - samples[index - 1].d);
        }

        if (index + 1 < samples.Count && samples[index + 1].id == sample.id)
        {
            spacing = Mathf.Min(spacing, samples[index + 1].d - sample.d);
        }

        return spacing == float.MaxValue ? 0f : spacing * 0.5f;
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
