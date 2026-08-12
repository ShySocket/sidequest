using System.IO;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Plays the source clip as the world behind the character, and owns the mapping
/// from video-frame coordinates to world space.
/// </summary>
/// <remarks>
/// That mapping is the reason this class exists rather than a bare VideoPlayer.
/// The level file stores the ground as a normalized y within the *video frame*,
/// so anything that positions the character has to know where the video frame
/// actually landed on screen. Letterboxing changes that, and getting it wrong
/// puts the character's feet off the ground everywhere.
///
/// The quad is parented to the camera, following the arrangement already proven
/// in RearCameraBackground.cs for the live camera feed.
/// </remarks>
[RequireComponent(typeof(VideoPlayer))]
public sealed class VideoBackground : MonoBehaviour
{
    public enum FitMode
    {
        /// <summary>Whole frame visible, bars if the aspects differ. Coordinates stay exact.</summary>
        Letterbox,

        /// <summary>Fill the screen and crop the overflow.</summary>
        Crop
    }

    const float BackgroundDistance = 50f;
    const string PreferredShader = "Sidequest/RearCameraBackground";
    const string ForegroundShader = "Sidequest/VideoForegroundMasked";

    [SerializeField] Camera targetCamera;
    [SerializeField] FitMode fitMode = FitMode.Letterbox;

    VideoPlayer player;
    RenderTexture texture;
    Transform quad;
    Material material;
    static readonly int MaskTexProperty = Shader.PropertyToID("_MaskTex");
    static readonly int MaskRectProperty = Shader.PropertyToID("_MaskRect");

    Transform foregroundQuad;
    Material foregroundMaterial;
    Texture2D occluderAtlas;
    bool foregroundMasked;
    bool appliedMask;
    Vector4 appliedMaskRect = new Vector4(0f, 0f, 1f, 1f);
    float halfWidth;
    float halfHeight;
    int laidOutWidth;
    int laidOutHeight;
    float laidOutAspect;

    public VideoPlayer Player => player;
    public bool IsPrepared => player != null && player.isPrepared;

    void Awake()
    {
        EnsureInitialized();
    }

    /// <summary>
    /// Set up the player and quad, whoever asks first.
    /// </summary>
    /// <remarks>
    /// Unity does not order Awake between objects, so LevelDirector.Awake can
    /// call Begin before this component's own Awake has run. Initializing on
    /// demand removes the ordering dependency rather than relying on luck.
    /// </remarks>
    void EnsureInitialized()
    {
        if (player != null)
        {
            return;
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        player = GetComponent<VideoPlayer>();
        BuildQuad();
    }

    /// <summary>
    /// Start preparing a clip from StreamingAssets.
    /// </summary>
    /// <remarks>
    /// The file name comes from the level rather than a serialized field, so
    /// changing clips does not need the scene rebuilt - a scene holding a stale
    /// name silently played the wrong file, or nothing.
    /// </remarks>
    public void Begin(string fileName)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(fileName))
        {
            Debug.LogError("VideoBackground: no clip name; the level file has no playbackFile.");
            return;
        }

        player.source = VideoSource.Url;
        player.url = ResolveUrl(fileName);
        player.renderMode = VideoRenderMode.RenderTexture;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.isLooping = false;
        player.playOnAwake = false;
        player.waitForFirstFrame = true;
        // Frames are consumed by explicit time control, not by wall clock.
        player.skipOnDrop = true;
        player.Prepare();
    }

    static string ResolveUrl(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        return path.Contains("://") ? path : "file://" + path;
    }

    void BuildQuad()
    {
        GameObject created = GameObject.CreatePrimitive(PrimitiveType.Quad);
        created.name = "VideoBackgroundQuad";
        Destroy(created.GetComponent<Collider>());

        quad = created.transform;
        quad.SetParent(targetCamera != null ? targetCamera.transform : transform, false);
        quad.localPosition = new Vector3(0f, 0f, BackgroundDistance);
        quad.localRotation = Quaternion.identity;

        var renderer = created.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortingOrder = short.MinValue;

        material = new Material(ResolveShader());
        renderer.sharedMaterial = material;
    }

    static Shader ResolveShader()
    {
        Shader shader = Shader.Find(PreferredShader);
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Texture");
        }

        return shader;
    }

    void Update()
    {
        if (texture == null && player.isPrepared)
        {
            CreateTexture();
        }

        // The layout only depends on the screen and the clip, neither of which
        // changes most frames. Recomputing it every frame wrote a new transform
        // scale 60 times a second to arrive at the same number.
        if (targetCamera != null
            && (Screen.width != laidOutWidth
                || Screen.height != laidOutHeight
                || !Mathf.Approximately(targetCamera.aspect, laidOutAspect)))
        {
            laidOutWidth = Screen.width;
            laidOutHeight = Screen.height;
            laidOutAspect = targetCamera.aspect;
            RefreshLayout();
        }
    }

    void CreateTexture()
    {
        // Match the clip exactly: a mismatched RenderTexture rescales every frame
        // and softens the image for no reason.
        texture = new RenderTexture((int)player.width, (int)player.height, 0)
        {
            name = "VideoBackground",
            wrapMode = TextureWrapMode.Clamp
        };
        player.targetTexture = texture;
        RefreshLayout();

        if (material != null)
        {
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }
        }
    }

    void RefreshLayout()
    {
        if (targetCamera == null || quad == null)
        {
            return;
        }

        float viewHalfHeight = targetCamera.orthographic
            ? targetCamera.orthographicSize
            : BackgroundDistance * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float viewHalfWidth = viewHalfHeight * targetCamera.aspect;

        float videoAspect = texture != null && texture.height > 0
            ? (float)texture.width / texture.height
            : 16f / 9f;

        // Letterbox fits the frame inside the view; crop covers it. Both keep the
        // video's own aspect, which is what makes the coordinate mapping valid.
        float fitted = viewHalfWidth / videoAspect;
        halfHeight = fitMode == FitMode.Letterbox
            ? Mathf.Min(viewHalfHeight, fitted)
            : Mathf.Max(viewHalfHeight, fitted);
        halfWidth = halfHeight * videoAspect;

        quad.localScale = new Vector3(halfWidth * 2f, halfHeight * 2f, 1f);
    }

    /// <summary>
    /// World position of a point in the video frame, where (0,0) is the frame's
    /// top-left and (1,1) its bottom-right.
    /// </summary>
    public Vector3 FrameToWorld(float x, float y)
    {
        Vector3 origin = targetCamera != null ? targetCamera.transform.position : Vector3.zero;
        return new Vector3(
            origin.x + (x - 0.5f) * halfWidth * 2f,
            origin.y + (0.5f - y) * halfHeight * 2f,
            0f);
    }

    /// <summary>
    /// Silhouette atlas for occluders, baked by the analyzer. Optional.
    /// </summary>
    public void SetOccluderMask(Texture2D atlas)
    {
        occluderAtlas = atlas;
    }

    /// <summary>
    /// Re-draw a strip of the video in front of everything at z &lt; 0.
    /// </summary>
    /// <remarks>
    /// The strip shows exactly the pixels already behind it, so its only
    /// visible effect is occluding the ball - which is how the ball passes
    /// *behind* a pole without any runtime segmentation. ``strip`` is in
    /// video-frame coordinates, (0,0) top-left.
    ///
    /// With a silhouette (``hasMask`` and a loaded atlas), only the object's
    /// own pixels occlude: the ball slides behind the pole's outline instead
    /// of vanishing at its detection rectangle. ``maskUv`` is the object's
    /// cell in the atlas, GL convention.
    /// </remarks>
    public void ShowForeground(Rect strip, Rect maskUv = default, bool hasMask = false)
    {
        if (texture == null)
        {
            return;
        }

        // A sample authored WITH a silhouette must never degrade to the full
        // rectangle just because the atlas failed to load - a rectangle bites
        // a visible hole out of the ball. No occlusion is the safe failure.
        if (hasMask && occluderAtlas == null)
        {
            HideForeground();
            return;
        }

        if (foregroundQuad == null)
        {
            GameObject created = GameObject.CreatePrimitive(PrimitiveType.Quad);
            created.name = "VideoForegroundStrip";
            Destroy(created.GetComponent<Collider>());
            foregroundQuad = created.transform;
            foregroundQuad.SetParent(
                targetCamera != null ? targetCamera.transform : transform, false);

            var renderer = created.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // The masked shader degrades to the rectangle behavior when no
            // atlas is bound (its default mask is solid white), so it serves
            // both paths; the opaque background shader remains the fallback
            // if it is somehow missing from the build.
            Shader masked = Shader.Find(ForegroundShader);
            foregroundMasked = masked != null;
            foregroundMaterial = new Material(masked != null ? masked : ResolveShader());
            foregroundMaterial.mainTexture = texture;
            if (foregroundMaterial.HasProperty("_BaseMap"))
            {
                foregroundMaterial.SetTexture("_BaseMap", texture);
            }
            renderer.sharedMaterial = foregroundMaterial;
        }

        if (foregroundMasked)
        {
            // Write only on change, same reason SetPlaybackSpeed does: this
            // runs every frame a strip is visible, and re-assigning identical
            // values reaches into the native material for nothing.
            bool useMask = hasMask && occluderAtlas != null;
            if (useMask != appliedMask)
            {
                appliedMask = useMask;
                // A null texture reverts the property to its solid-white
                // default: every pixel of the strip occludes - the rectangle.
                foregroundMaterial.SetTexture(MaskTexProperty, useMask ? occluderAtlas : null);
            }

            Vector4 rect = useMask
                ? new Vector4(maskUv.xMin, maskUv.yMin, maskUv.width, maskUv.height)
                : new Vector4(0f, 0f, 1f, 1f);
            if (rect != appliedMaskRect)
            {
                appliedMaskRect = rect;
                foregroundMaterial.SetVector(MaskRectProperty, rect);
            }
        }

        foregroundQuad.gameObject.SetActive(true);

        float width = strip.width * halfWidth * 2f;
        float height = strip.height * halfHeight * 2f;
        float centreX = (strip.center.x - 0.5f) * halfWidth * 2f;
        float centreY = (0.5f - strip.center.y) * halfHeight * 2f;

        // A few units in front of the camera: nearer than the ball (world z=0),
        // far enough to clear the near plane. Depth does not change apparent
        // size under this orthographic camera, so the mapping stays exact.
        foregroundQuad.localPosition = new Vector3(centreX, centreY, 5f);
        foregroundQuad.localScale = new Vector3(width, height, 1f);

        // The video texture's v axis runs bottom-up; frame coordinates top-down.
        foregroundMaterial.mainTextureScale = new Vector2(strip.width, strip.height);
        foregroundMaterial.mainTextureOffset = new Vector2(strip.xMin, 1f - strip.yMax);
    }

    public void HideForeground()
    {
        if (foregroundQuad != null)
        {
            foregroundQuad.gameObject.SetActive(false);
        }
    }

    /// <summary>World units per unit of normalized frame height. Used to size the character.</summary>
    public float FrameHeightInWorld => halfHeight * 2f;

    /// <summary>World units per unit of normalized frame width.</summary>
    public float FrameWidthInWorld => halfWidth * 2f;

    void OnDestroy()
    {
        if (texture != null)
        {
            texture.Release();
        }

        if (material != null)
        {
            Destroy(material);
        }

        if (foregroundMaterial != null)
        {
            Destroy(foregroundMaterial);
        }
    }
}
