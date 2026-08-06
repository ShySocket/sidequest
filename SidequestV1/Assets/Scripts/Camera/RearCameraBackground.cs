using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// Adds an opt-in rear-camera feed behind the runner scene while leaving the
/// existing game and UI rendered on top.
/// </summary>
public sealed class RearCameraBackground : MonoBehaviour
{
    private const string RunnerSceneName = "RunnerPrototype";
    private const float BackgroundDistance = 50f;
    private const float DeviceDiscoveryTimeoutSeconds = 5f;
    private const float FirstFrameTimeoutSeconds = 8f;
    private const int RequestedCameraWidth = 1280;
    private const int RequestedCameraHeight = 720;
    private const int RequestedCameraFramesPerSecond = 30;

    private Camera targetCamera;
    private GameObject backgroundQuad;
    private MeshRenderer backgroundRenderer;
    private Material backgroundMaterial;
    private WebCamTexture cameraTexture;
    private Button toggleButton;
    private Image toggleImage;
    private TMP_Text toggleLabel;
    private Coroutine cameraStartup;
    private bool cameraRequested;
    private int lastScreenWidth;
    private int lastScreenHeight;
    private int lastTextureWidth;
    private int lastTextureHeight;
    private int lastVideoRotation = -1;
    private bool lastVerticallyMirrored;
    private bool cameraPermissionGranted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (SceneManager.GetActiveScene().name != RunnerSceneName
            || FindFirstObjectByType<RearCameraBackground>() != null)
        {
            return;
        }

        GameObject service = new GameObject(nameof(RearCameraBackground));
        DontDestroyOnLoad(service);
        service.AddComponent<RearCameraBackground>();
    }

    private void Awake()
    {
        CreateToggleUi();
        SceneManager.sceneLoaded += HandleSceneLoaded;
        BindToMainCamera();
    }

    private void Update()
    {
        if (!cameraRequested || cameraTexture == null || !cameraTexture.isPlaying)
        {
            return;
        }

        if (Screen.width != lastScreenWidth
            || Screen.height != lastScreenHeight
            || cameraTexture.width != lastTextureWidth
            || cameraTexture.height != lastTextureHeight
            || cameraTexture.videoRotationAngle != lastVideoRotation
            || cameraTexture.videoVerticallyMirrored != lastVerticallyMirrored)
        {
            RefreshBackgroundLayout();
        }
    }

    private void OnApplicationPause(bool isPaused)
    {
        if (isPaused)
        {
            if (cameraStartup != null)
            {
                StopCoroutine(cameraStartup);
                cameraStartup = null;
            }

            StopCameraFeed(true);
        }
        else if (cameraRequested && cameraStartup == null)
        {
            cameraStartup = StartCoroutine(StartCameraFeed());
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        StopCameraFeed(true);

        if (backgroundMaterial != null)
        {
            Destroy(backgroundMaterial);
        }
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != RunnerSceneName)
        {
            if (cameraStartup != null)
            {
                StopCoroutine(cameraStartup);
                cameraStartup = null;
            }

            cameraRequested = false;
            StopCameraFeed(true);
            SetToggleAppearance("Camera: Off", Color.black);
            gameObject.SetActive(false);
            return;
        }

        // The service survives scene loads, so it may be inactive here after
        // a visit to another scene; coroutines only run on active objects.
        gameObject.SetActive(true);
        StartCoroutine(BindToMainCameraNextFrame());
    }

    private IEnumerator BindToMainCameraNextFrame()
    {
        yield return null;
        BindToMainCamera();
    }

    private void BindToMainCamera()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("Rear camera background could not find the scene's Main Camera.", this);
            return;
        }

        targetCamera = mainCamera;
        targetCamera.clearFlags = CameraClearFlags.SolidColor;
        targetCamera.backgroundColor = Color.white;

        if (backgroundQuad != null)
        {
            Destroy(backgroundQuad);
        }

        backgroundQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backgroundQuad.name = "Live Rear Camera Background";
        backgroundQuad.transform.SetParent(targetCamera.transform, false);
        backgroundQuad.transform.localPosition = new Vector3(0f, 0f, BackgroundDistance);

        Collider backgroundCollider = backgroundQuad.GetComponent<Collider>();
        if (backgroundCollider != null)
        {
            Destroy(backgroundCollider);
        }

        backgroundRenderer = backgroundQuad.GetComponent<MeshRenderer>();
        backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        backgroundRenderer.receiveShadows = false;
        backgroundRenderer.sortingOrder = short.MinValue;
        Material material = GetOrCreateBackgroundMaterial();
        backgroundRenderer.sharedMaterial = material;
        bool feedIsLive = material != null
            && cameraRequested
            && cameraTexture != null
            && cameraTexture.isPlaying;
        if (feedIsLive)
        {
            ApplyCameraTexture(material);
        }

        backgroundRenderer.enabled = feedIsLive;
        RefreshBackgroundLayout();
    }

    private Material GetOrCreateBackgroundMaterial()
    {
        if (backgroundMaterial != null)
        {
            return backgroundMaterial;
        }

        // Loading the dedicated shader from Resources guarantees that it is
        // packaged instead of being stripped from mobile builds.
        Shader shader = Resources.Load<Shader>("RearCameraBackground");
        if (shader == null)
        {
            shader = Shader.Find("Sidequest/RearCameraBackground");
        }

        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Texture");
        }

        if (shader == null)
        {
            Debug.LogError("No unlit texture shader is available for the rear camera background.", this);
            return null;
        }

        backgroundMaterial = new Material(shader)
        {
            name = "Live Rear Camera Background Material"
        };
        return backgroundMaterial;
    }

    private void CreateToggleUi()
    {
        GameObject canvasObject = new GameObject(
            "Camera Toggle Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject buttonObject = new GameObject(
            "Rear Camera Toggle",
            typeof(RectTransform),
            typeof(Image),
            typeof(Button),
            typeof(Outline));
        buttonObject.transform.SetParent(canvasObject.transform, false);

        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(1f, 0f);
        buttonRect.anchorMax = new Vector2(1f, 0f);
        buttonRect.pivot = new Vector2(1f, 0f);
        buttonRect.anchoredPosition = new Vector2(-40f, 40f);
        buttonRect.sizeDelta = new Vector2(320f, 84f);

        toggleImage = buttonObject.GetComponent<Image>();
        toggleImage.color = Color.black;

        Outline outline = buttonObject.GetComponent<Outline>();
        outline.effectColor = Color.white;
        outline.effectDistance = new Vector2(3f, -3f);

        toggleButton = buttonObject.GetComponent<Button>();
        toggleButton.targetGraphic = toggleImage;
        toggleButton.onClick.AddListener(ToggleCamera);

        ColorBlock colors = toggleButton.colors;
        colors.highlightedColor = new Color(0.2f, 0.2f, 0.2f);
        colors.pressedColor = new Color(0.35f, 0.35f, 0.35f);
        colors.selectedColor = Color.black;
        toggleButton.colors = colors;

        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(buttonObject.transform, false);
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        toggleLabel = labelObject.GetComponent<TextMeshProUGUI>();
        toggleLabel.text = "Camera: Off";
        toggleLabel.fontSize = 32f;
        toggleLabel.fontStyle = FontStyles.Bold;
        toggleLabel.color = Color.white;
        toggleLabel.alignment = TextAlignmentOptions.Center;
        toggleLabel.raycastTarget = false;

        EnsureEventSystem();
    }

    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null)
        {
            return;
        }

        Debug.LogWarning(
            "No EventSystem is present, so the rear camera toggle cannot receive pointer input.");
    }

    private void ToggleCamera()
    {
        cameraRequested = !cameraRequested;
        if (!cameraRequested)
        {
            if (cameraStartup != null)
            {
                StopCoroutine(cameraStartup);
                cameraStartup = null;
            }

            StopCameraFeed(true);
            SetToggleAppearance("Camera: Off", Color.black);
            return;
        }

        SetToggleAppearance("Camera: Starting…", new Color(0.12f, 0.32f, 0.55f));
        if (cameraStartup == null)
        {
            cameraStartup = StartCoroutine(StartCameraFeed());
        }
    }

    private IEnumerator StartCameraFeed()
    {
        yield return RequestCameraPermission();

        if (!cameraRequested)
        {
            cameraStartup = null;
            yield break;
        }

        if (!cameraPermissionGranted)
        {
            FailCameraStart("Camera: Denied", "Camera access was denied.");
            yield break;
        }

        Material material = GetOrCreateBackgroundMaterial();
        if (material == null)
        {
            FailCameraStart(
                "Camera renderer error",
                "The bundled rear-camera shader could not be loaded.");
            yield break;
        }

        WebCamDevice[] devices = WebCamTexture.devices;
        float discoveryDeadline = Time.realtimeSinceStartup + DeviceDiscoveryTimeoutSeconds;
        while (cameraRequested
            && !ContainsRearCamera(devices)
            && Time.realtimeSinceStartup < discoveryDeadline)
        {
            yield return null;
            devices = WebCamTexture.devices;
        }

        if (!cameraRequested)
        {
            cameraStartup = null;
            yield break;
        }

        if (!ContainsRearCamera(devices))
        {
            FailCameraStart(
                "No rear camera",
                $"No rear-facing camera was reported. Found {devices.Length} camera device(s).");
            yield break;
        }

        bool receivedFrame = false;
        for (int i = 0; i < devices.Length && cameraRequested; i++)
        {
            if (devices[i].isFrontFacing)
            {
                continue;
            }

            ReleaseCameraTexture();
            // Ask for a concrete resolution; several Android devices fall
            // back to a tiny default frame when none is requested. The device
            // still substitutes its closest supported mode.
            cameraTexture = new WebCamTexture(
                devices[i].name,
                RequestedCameraWidth,
                RequestedCameraHeight,
                RequestedCameraFramesPerSecond);

            try
            {
                cameraTexture.Play();
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    $"Rear camera '{devices[i].name}' could not start: {exception.Message}",
                    this);
                ReleaseCameraTexture();
                continue;
            }

            float frameDeadline = Time.realtimeSinceStartup + FirstFrameTimeoutSeconds;
            while (cameraRequested && Time.realtimeSinceStartup < frameDeadline)
            {
                if (cameraTexture != null
                    && cameraTexture.isPlaying
                    && cameraTexture.didUpdateThisFrame
                    && cameraTexture.width > 16
                    && cameraTexture.height > 16)
                {
                    receivedFrame = true;
                    break;
                }

                yield return null;
            }

            if (receivedFrame)
            {
                Debug.Log(
                    $"Rear camera started: {devices[i].name} "
                    + $"({cameraTexture.width}x{cameraTexture.height}).",
                    this);
                break;
            }

            Debug.LogWarning(
                $"Rear camera '{devices[i].name}' did not deliver a frame before timeout.",
                this);
            ReleaseCameraTexture();
            yield return null;
        }

        if (!cameraRequested)
        {
            ReleaseCameraTexture();
            cameraStartup = null;
            yield break;
        }

        if (!receivedFrame)
        {
            FailCameraStart(
                "Camera unavailable",
                "Rear cameras were found, but none delivered video. "
                + "The camera may be in use by another app; retry after closing it.");
            yield break;
        }

        ApplyCameraTexture(material);
        if (backgroundRenderer != null)
        {
            backgroundRenderer.sharedMaterial = material;
            backgroundRenderer.enabled = true;
        }

        SetToggleAppearance("Camera: On", new Color(0.05f, 0.52f, 0.28f));
        cameraStartup = null;
        RefreshBackgroundLayout();
    }

    private IEnumerator RequestCameraPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            cameraPermissionGranted = true;
            yield break;
        }

        bool requestFinished = false;
        PermissionCallbacks callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => requestFinished = true;
        callbacks.PermissionDenied += _ => requestFinished = true;
        Permission.RequestUserPermission(Permission.Camera, callbacks);

        while (!requestFinished && cameraRequested)
        {
            yield return null;
        }

        // Wait one frame after the callback before enumerating devices, as
        // required by Unity's Android camera initialization guidance.
        yield return null;
        cameraPermissionGranted = Permission.HasUserAuthorizedPermission(Permission.Camera);
#else
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }

        cameraPermissionGranted =
            Application.HasUserAuthorization(UserAuthorization.WebCam);
#endif
    }

    private static bool ContainsRearCamera(WebCamDevice[] devices)
    {
        for (int i = 0; i < devices.Length; i++)
        {
            if (!devices[i].isFrontFacing)
            {
                return true;
            }
        }

        return false;
    }

    private void FailCameraStart(string buttonText, string logMessage)
    {
        cameraRequested = false;
        StopCameraFeed(true);
        SetToggleAppearance(buttonText, new Color(0.62f, 0.12f, 0.12f));
        cameraStartup = null;
        Debug.LogWarning(logMessage, this);
    }

    private void StopCameraFeed(bool releaseTexture)
    {
        if (backgroundRenderer != null)
        {
            backgroundRenderer.enabled = false;
        }

        if (cameraTexture == null)
        {
            return;
        }

        if (cameraTexture.isPlaying)
        {
            cameraTexture.Stop();
        }

        if (releaseTexture)
        {
            ReleaseCameraTexture();
        }
    }

    private void ReleaseCameraTexture()
    {
        if (cameraTexture == null)
        {
            return;
        }

        if (cameraTexture.isPlaying)
        {
            cameraTexture.Stop();
        }

        Destroy(cameraTexture);
        cameraTexture = null;
    }

    private void ApplyCameraTexture(Material material)
    {
        material.mainTexture = cameraTexture;
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", cameraTexture);
        }
    }

    private void RefreshBackgroundLayout()
    {
        if (targetCamera == null || backgroundQuad == null)
        {
            return;
        }

        float viewHeight;
        if (targetCamera.orthographic)
        {
            viewHeight = targetCamera.orthographicSize * 2f;
        }
        else
        {
            viewHeight = 2f
                * BackgroundDistance
                * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        }

        float viewWidth = viewHeight * targetCamera.aspect;
        int rotation = cameraTexture == null ? 0 : cameraTexture.videoRotationAngle;
        bool quarterTurn = rotation == 90 || rotation == 270;
        backgroundQuad.transform.localEulerAngles = new Vector3(0f, 0f, -rotation);
        backgroundQuad.transform.localScale = quarterTurn
            ? new Vector3(viewHeight, viewWidth, 1f)
            : new Vector3(viewWidth, viewHeight, 1f);

        if (cameraTexture != null && backgroundMaterial != null
            && cameraTexture.width > 16 && cameraTexture.height > 16)
        {
            float quadAspect = quarterTurn
                ? viewHeight / viewWidth
                : viewWidth / viewHeight;
            float textureAspect = (float)cameraTexture.width / cameraTexture.height;
            Vector2 scale = Vector2.one;
            Vector2 offset = Vector2.zero;

            if (textureAspect > quadAspect)
            {
                scale.x = quadAspect / textureAspect;
                offset.x = (1f - scale.x) * 0.5f;
            }
            else
            {
                scale.y = textureAspect / quadAspect;
                offset.y = (1f - scale.y) * 0.5f;
            }

            if (cameraTexture.videoVerticallyMirrored)
            {
                scale.y = -scale.y;
                offset.y = 1f - offset.y;
            }

            SetTextureTransform(backgroundMaterial, scale, offset);
        }

        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        lastTextureWidth = cameraTexture == null ? 0 : cameraTexture.width;
        lastTextureHeight = cameraTexture == null ? 0 : cameraTexture.height;
        lastVideoRotation = rotation;
        lastVerticallyMirrored = cameraTexture != null && cameraTexture.videoVerticallyMirrored;
    }

    private static void SetTextureTransform(Material material, Vector2 scale, Vector2 offset)
    {
        material.mainTextureScale = scale;
        material.mainTextureOffset = offset;
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTextureScale("_BaseMap", scale);
            material.SetTextureOffset("_BaseMap", offset);
        }
    }

    private void SetToggleAppearance(string text, Color color)
    {
        if (toggleLabel != null)
        {
            toggleLabel.text = text;
        }

        if (toggleImage != null)
        {
            toggleImage.color = color;
        }
    }
}
