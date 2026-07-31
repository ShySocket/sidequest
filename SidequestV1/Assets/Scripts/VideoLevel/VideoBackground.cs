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

    [SerializeField] Camera targetCamera;
    [SerializeField] string videoFileName = "IMG_3775.mov";
    [SerializeField] FitMode fitMode = FitMode.Letterbox;

    VideoPlayer player;
    RenderTexture texture;
    Transform quad;
    Material material;
    float halfWidth;
    float halfHeight;

    public VideoPlayer Player => player;
    public bool IsPrepared => player != null && player.isPrepared;

    void Awake()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        player = GetComponent<VideoPlayer>();
        ConfigurePlayer();
        BuildQuad();
    }

    void ConfigurePlayer()
    {
        player.source = VideoSource.Url;
        player.url = ResolveUrl(videoFileName);
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

        RefreshLayout();
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
    }
}
