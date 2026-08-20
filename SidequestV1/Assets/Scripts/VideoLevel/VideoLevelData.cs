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
    public string playbackFile;
    public string distanceUnits;
    public float characterColumn;

    /// <summary>-1 = the character travels right-to-left across the screen.</summary>
    /// <remarks>
    /// Measured from optical flow by the analyzer rather than assumed: the world
    /// sweeping one way means the vehicle is going the other, and filming out the
    /// opposite window flips it. Decides which way the character faces and which
    /// screen edge obstacles arrive from.
    /// </remarks>
    public int travelDirection = -1;

    /// <summary>Diameter the marker was drawn at, 0..1 of frame height.</summary>
    /// <remarks>Zero when no marker was used, and the tuned default applies.</remarks>
    public float markerDiameter;

    public float totalDistance;
    public List<VideoLevelTimePoint> timeToDistance = new List<VideoLevelTimePoint>();
    public List<VideoLevelPathPoint> path = new List<VideoLevelPathPoint>();
    public List<VideoLevelSpeedPoint> screenSpeed = new List<VideoLevelSpeedPoint>();
    public List<VideoLevelEvent> events = new List<VideoLevelEvent>();
    public List<VideoLevelHiddenSpan> hidden = new List<VideoLevelHiddenSpan>();
    public List<VideoLevelAmbientPoint> ambient = new List<VideoLevelAmbientPoint>();
    public List<VideoLevelForegroundBox> foreground = new List<VideoLevelForegroundBox>();

    /// <summary>Grayscale atlas of occluder silhouettes, in StreamingAssets.</summary>
    /// <remarks>Empty when the analyzer skipped the mask pass; occluders then
    /// fall back to plain rectangles, which still read correctly because the
    /// re-drawn pixels match the background exactly.</remarks>
    public string foregroundMaskFile;
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

    /// <summary>Where the character sits horizontally, 0..1 across the frame.</summary>
    /// <remarks>
    /// Per-sample rather than one fixed column: the tracked marker traverses the
    /// screen right to left over the run, and pinning the character to a column
    /// would throw away that intent.
    /// </remarks>
    public float x;

    /// <summary>Ground line the ball's SIZE reads its depth from, when it
    /// differs from <c>y</c>. Zero = same as <c>y</c>.</summary>
    /// <remarks>
    /// An elevated surface's top edge is position, not distance: a hedge top
    /// rides high on screen while the bush stands at the sidewalk's near edge,
    /// and sizing from the top edge shrank the ball as if it had run away from
    /// the camera. The analyzer bakes the surface's ground-plane stand here.
    /// </remarks>
    public float z;

    public string s;
}

/// <summary>How fast the world slides past, in frame widths per second.</summary>
/// <remarks>Measured by the analyzer; the ball rolls at this rate.</remarks>
[Serializable]
public class VideoLevelSpeedPoint
{
    public float d;
    public float v;
}

/// <summary>Light sampled from the footage where the character stands.</summary>
/// <remarks>
/// Normalized so the clip's median luminance is 1.0: the game multiplies its
/// tuned material colours by this, so ordinary daylight leaves them unchanged
/// and shade or glare move them relative to that.
/// </remarks>
/// <summary>A strip of the video re-drawn in front of the ball.</summary>
/// <remarks>
/// The re-drawn pixels are identical to the background beneath, so the only
/// visible effect is the ball disappearing behind that strip - which is exactly
/// what passing behind a pole looks like.
///
/// Samples sharing an <c>id</c> are sightings of the same physical object, so
/// the game interpolates between them instead of snapping the strip from one
/// pole to the next. When <c>mask</c> is set, u1..v2 point at the object's
/// silhouette in the occluder atlas (GL convention, v up), and only silhouette
/// pixels occlude - the ball slides behind the pole's actual outline rather
/// than vanishing at its detection rectangle.
/// </remarks>
[Serializable]
public class VideoLevelForegroundBox
{
    public float d;
    public int id;
    public float x1;
    public float y1;
    public float x2;
    public float y2;
    public int mask;
    public float u1;
    public float v1;
    public float u2;
    public float v2;
}

[Serializable]
public class VideoLevelAmbientPoint
{
    public float d;
    public float r;
    public float g;
    public float b;
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

    /// <summary>Peak height the designer drew for this jump, 0..1 of frame height.</summary>
    /// <remarks>Zero means none was recorded, and the tuned default is used.</remarks>
    public float height;

    /// <summary>Full scoring window in seconds, centred on the takeoff.</summary>
    public float windowSeconds;

    /// <summary>Seconds before the takeoff that the warning appears.</summary>
    public float lead;

    /// <summary>Seconds of air the designer's drawn arc spans. Zero = derive from height.</summary>
    /// <remarks>
    /// Carried per cue because the obstacle decides it: a van takes longer to
    /// cross the column than a sqrt-of-height arc stays airborne, and the ball
    /// was landing on cars mid-crossing until arcs matched the drawn ones.
    /// </remarks>
    public float airTime;

    /// <summary>Dodge only: seconds the sidestep takes. Zero = tuned default.</summary>
    /// <remarks>The 57.3s stop-sign dodge was annotated "do not make it too
    /// quick", so a dodge can carry its own pace.</remarks>
    public float duration;

    public string source;
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
    Dodge,

    /// <summary>A small automatic believability hop - a kerb, a level change.</summary>
    /// <remarks>Never cued, scored, or missed: it fires like any scheduled
    /// jump but the player is not asked to press for it.</remarks>
    Hop
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

        if (string.Equals(value, "hop", StringComparison.OrdinalIgnoreCase))
        {
            return VideoLevelEventType.Hop;
        }

        return VideoLevelEventType.Jump;
    }
}
