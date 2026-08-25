using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// Always-on rear camera feed rendered as the scene background, and shared
/// with the CV runners as their input texture. In the editor (or when the
/// camera is unavailable or denied) it falls back to a procedural street so
/// the game stays playable and the pipeline keeps receiving frames.
/// </summary>
public sealed class LiveCameraFeed : MonoBehaviour
{
    private const float BackgroundDistance = 60f;
    private const float FirstFrameTimeoutSeconds = 8f;

    [SerializeField] private Camera targetCamera;

    private WebCamTexture cameraTexture;
    private ProceduralStreetTexture fallbackStreet;
    private GameObject backgroundQuad;
    private Material backgroundMaterial;
    private bool started;
    private int lastRotation = -1;
    private int lastWidth;
    private int lastHeight;

    /// <summary>Current source frame for CV. Never null after Start.</summary>
    public Texture SourceTexture { get; private set; }
    public bool IsLiveCamera { get; private set; }

    private void Start()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        fallbackStreet = GetComponent<ProceduralStreetTexture>();
        if (fallbackStreet == null)
        {
            fallbackStreet = gameObject.AddComponent<ProceduralStreetTexture>();
        }

        SourceTexture = fallbackStreet.Texture;
        CreateBackgroundQuad();
        StartCoroutine(StartCameraWhenPermitted());
    }

    private void Update()
    {
        if (IsLiveCamera && cameraTexture != null)
        {
            if (cameraTexture.videoRotationAngle != lastRotation
                || cameraTexture.width != lastWidth
                || cameraTexture.height != lastHeight
                || Screen.width + Screen.height != lastWidth + lastHeight)
            {
                LayoutBackground();
            }
        }
    }

    private IEnumerator StartCameraWhenPermitted()
    {
        if (started)
        {
            yield break;
        }

        started = true;
#if UNITY_EDITOR
        // The editor plays with the procedural street; using the developer's
        // webcam would feed the detector desk scenery instead of a road.
        ApplyTexture(fallbackStreet.Texture, false);
        yield break;
#else
        yield return RequestPermission();
        WebCamDevice[] devices = WebCamTexture.devices;
        string deviceName = null;
        for (int i = 0; i < devices.Length; i++)
        {
            if (!devices[i].isFrontFacing)
            {
                deviceName = devices[i].name;
                break;
            }
        }

        if (deviceName == null)
        {
            Debug.LogWarning("No rear camera available; using procedural street background.", this);
            ApplyTexture(fallbackStreet.Texture, false);
            yield break;
        }

        cameraTexture = new WebCamTexture(deviceName, 1280, 720, 30);
        cameraTexture.Play();
        float deadline = Time.realtimeSinceStartup + FirstFrameTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (cameraTexture.isPlaying
                && cameraTexture.didUpdateThisFrame
                && cameraTexture.width > 16)
            {
                ApplyTexture(cameraTexture, true);
                yield break;
            }

            yield return null;
        }

        Debug.LogWarning("Rear camera delivered no frames; using procedural street background.", this);
        cameraTexture.Stop();
        Destroy(cameraTexture);
        cameraTexture = null;
        ApplyTexture(fallbackStreet.Texture, false);
#endif
    }

    private IEnumerator RequestPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            bool finished = false;
            PermissionCallbacks callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => finished = true;
            callbacks.PermissionDenied += _ => finished = true;
            Permission.RequestUserPermission(Permission.Camera, callbacks);
            while (!finished)
            {
                yield return null;
            }

            yield return null;
        }
#else
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }
#endif
    }

    private void ApplyTexture(Texture texture, bool live)
    {
        SourceTexture = texture;
        IsLiveCamera = live;
        if (backgroundMaterial != null)
        {
            backgroundMaterial.mainTexture = texture;
            if (backgroundMaterial.HasProperty("_MainTex"))
            {
                backgroundMaterial.SetTexture("_MainTex", texture);
            }
        }

        LayoutBackground();
    }

    private void CreateBackgroundQuad()
    {
        Shader shader = Resources.Load<Shader>("RearCameraBackground");
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        backgroundMaterial = new Material(shader) { name = "Live Feed Background" };
        backgroundMaterial.mainTexture = SourceTexture;

        backgroundQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backgroundQuad.name = "Live Feed Background";
        backgroundQuad.transform.SetParent(targetCamera.transform, false);
        backgroundQuad.transform.localPosition = new Vector3(0f, 0f, BackgroundDistance);
        Destroy(backgroundQuad.GetComponent<Collider>());
        MeshRenderer meshRenderer = backgroundQuad.GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = backgroundMaterial;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        LayoutBackground();
    }

    private void LayoutBackground()
    {
        if (backgroundQuad == null || targetCamera == null || SourceTexture == null)
        {
            return;
        }

        float viewHeight = 2f * BackgroundDistance
            * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float viewWidth = viewHeight * targetCamera.aspect;
        int rotation = cameraTexture != null && IsLiveCamera
            ? cameraTexture.videoRotationAngle
            : 0;
        bool quarterTurn = rotation == 90 || rotation == 270;
        backgroundQuad.transform.localEulerAngles = new Vector3(0f, 0f, -rotation);
        backgroundQuad.transform.localScale = quarterTurn
            ? new Vector3(viewHeight, viewWidth, 1f)
            : new Vector3(viewWidth, viewHeight, 1f);

        float quadAspect = quarterTurn ? viewHeight / viewWidth : viewWidth / viewHeight;
        float textureAspect = (float)SourceTexture.width / SourceTexture.height;
        Vector2 scale = Vector2.one;
        Vector2 offset = Vector2.zero;
        if (textureAspect > quadAspect)
        {
            scale.x = quadAspect / textureAspect;
            offset.x = (1f - scale.x) * 0.5f;
        }
        else if (textureAspect > 0f)
        {
            scale.y = textureAspect / quadAspect;
            offset.y = (1f - scale.y) * 0.5f;
        }

        if (cameraTexture != null && IsLiveCamera && cameraTexture.videoVerticallyMirrored)
        {
            scale.y = -scale.y;
            offset.y = 1f - offset.y;
        }

        backgroundMaterial.mainTextureScale = scale;
        backgroundMaterial.mainTextureOffset = offset;
        lastRotation = rotation;
        lastWidth = SourceTexture.width;
        lastHeight = SourceTexture.height;
    }

    private void OnDestroy()
    {
        if (cameraTexture != null)
        {
            if (cameraTexture.isPlaying)
            {
                cameraTexture.Stop();
            }

            Destroy(cameraTexture);
        }

        if (backgroundMaterial != null)
        {
            Destroy(backgroundMaterial);
        }
    }
}
