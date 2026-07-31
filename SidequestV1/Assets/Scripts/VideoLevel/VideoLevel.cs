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
    }

    public VideoLevelData Data => data;
    public IReadOnlyList<VideoLevelEvent> Events => data.events;
    public float TotalDistance => data.totalDistance;
    public float Duration => data.source != null ? data.source.duration : 0f;
    public float CharacterColumn => data.characterColumn;
    public string VideoFileName => data.source != null ? data.source.file : null;

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

    public string SurfaceAtDistance(float distance)
    {
        int index = Mathf.Clamp(UpperBound(pathDistances, distance) - 1, 0, data.path.Count - 1);
        return data.path[index].s;
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
