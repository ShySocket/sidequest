using System;
using System.Collections.Generic;

/// <summary>
/// Serializable mirror of the authored level file produced by
/// <c>tools/video-analyzer</c> (<c>analyze author</c>).
/// </summary>
/// <remarks>
/// The schema deliberately uses objects rather than arrays-of-arrays, and keeps
/// every array nested inside an object, because Unity's built-in JsonUtility
/// cannot deserialize top-level arrays or jagged arrays. That is what lets this
/// project read levels without taking a Newtonsoft dependency.
///
/// Everything is keyed on distance travelled rather than video time, so a level
/// stays correct whatever speed the player moves at.
/// </remarks>
[Serializable]
public class VideoLevelData
{
    public int version;
    public VideoLevelSource source;
    public string distanceUnits;
    public float characterColumn;
    public float totalDistance;
    public List<VideoLevelTimePoint> timeToDistance = new List<VideoLevelTimePoint>();
    public List<VideoLevelPathPoint> path = new List<VideoLevelPathPoint>();
    public List<VideoLevelEvent> events = new List<VideoLevelEvent>();
    public List<VideoLevelHiddenSpan> hidden = new List<VideoLevelHiddenSpan>();
}

[Serializable]
public class VideoLevelSource
{
    public string file;
    public float fps;
    public int width;
    public int height;
    public float duration;
}

[Serializable]
public class VideoLevelTimePoint
{
    public float t;
    public float d;
}

/// <summary>A ground sample: normalized screen y at a distance, and its surface.</summary>
[Serializable]
public class VideoLevelPathPoint
{
    public float d;
    public float y;
    public string s;
}

[Serializable]
public class VideoLevelEvent
{
    public int id;
    public string type;
    public string label;
    public float time;
    public float distance;
    public float window;

    /// <summary>Half-width of the success window, in distance.</summary>
    public float windowDistance;
}

[Serializable]
public class VideoLevelHiddenSpan
{
    public float startDistance;
    public float endDistance;
    public float startTime;
    public float endTime;
}

public enum VideoLevelEventType
{
    Jump,
    Platform,
    Dodge
}

public static class VideoLevelEventTypes
{
    public static VideoLevelEventType Parse(string value)
    {
        if (string.Equals(value, "dodge", StringComparison.OrdinalIgnoreCase))
        {
            return VideoLevelEventType.Dodge;
        }

        if (string.Equals(value, "platform", StringComparison.OrdinalIgnoreCase))
        {
            return VideoLevelEventType.Platform;
        }

        return VideoLevelEventType.Jump;
    }
}
