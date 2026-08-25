using System.IO;
using TMPro;
using Unity.Sentis;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds the CVRunner scene: live camera background, Sentis detection and
/// segmentation, the rolling sphere, obstacle field, HUD, and the reused
/// GPS/IMU speed stack from the original runner.
/// </summary>
public static class CvRunnerSetup
{
    private const string ScenePath = "Assets/Scenes/CVRunner.unity";
    private const string ConfigurationPath = "Assets/ScriptableObjects/Configurations/DefaultRunnerConfiguration.asset";
    private const string YoloModelPath = "Assets/Models/yolox_tiny.onnx";
    private const string SegModelPath = "Assets/Models/fast_scnn_288x480.onnx";
    private const string FontAssetPath =
        "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

    [MenuItem("Tools/Sidequest/Build CV Runner Scene")]
    public static void BuildScene()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("Exit Play Mode before building the CV Runner scene.");
            return;
        }

        RunnerConfiguration configuration =
            AssetDatabase.LoadAssetAtPath<RunnerConfiguration>(ConfigurationPath);
        if (configuration == null)
        {
            throw new FileNotFoundException("Runner configuration asset missing.", ConfigurationPath);
        }

        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        ModelAsset yoloModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(YoloModelPath);
        ModelAsset segModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(SegModelPath);
        if (yoloModel == null)
        {
            Debug.LogWarning($"YOLOX model not importable at {YoloModelPath}; the game will run with procedural obstacles.");
        }

        if (segModel == null)
        {
            Debug.LogWarning($"Fast-SCNN model not importable at {SegModelPath}; surfaces will be assumed road.");
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "CVRunner";

        // --- Camera + light -------------------------------------------------
        GameObject cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 200f;
        cameraObject.transform.position = new Vector3(0f, 2.4f, -5.5f);
        cameraObject.transform.rotation = Quaternion.Euler(10f, 0f, 0f);

        GameObject lightObject = new GameObject("Directional Light");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.15f;
        lightObject.transform.rotation = Quaternion.Euler(55f, -25f, 0f);

        // --- Speed stack (reused from RunnerPrototype) ----------------------
        GameObject systems = new GameObject("GameSystems");
        MockSpeedProvider mock = new GameObject("MockSpeedProvider").AddComponent<MockSpeedProvider>();
        mock.transform.SetParent(systems.transform);
        GameObject gpsObject = new GameObject("UnityGpsSpeedProvider");
        gpsObject.transform.SetParent(systems.transform);
        LocationPermissionService permissions = gpsObject.AddComponent<LocationPermissionService>();
        UnityGpsSpeedProvider gps = gpsObject.AddComponent<UnityGpsSpeedProvider>();
        UnityDeviceMotionProvider motion = gpsObject.AddComponent<UnityDeviceMotionProvider>();
        SetReference(gps, "configuration", configuration);
        SetReference(gps, "permissionService", permissions);

        VehicleSpeedController speed = new GameObject("VehicleSpeedController").AddComponent<VehicleSpeedController>();
        speed.transform.SetParent(systems.transform);
        SetReference(speed, "configuration", configuration);
        SetReference(speed, "mockSpeedProvider", mock);
        SetReference(speed, "unityGpsSpeedProvider", gps);
        SetReference(speed, "deviceMotionProvider", motion);

        // --- Live feed + CV runners -----------------------------------------
        LiveCameraFeed feed = cameraObject.AddComponent<LiveCameraFeed>();
        SetReference(feed, "targetCamera", camera);
        ProceduralStreetTexture street = cameraObject.GetComponent<ProceduralStreetTexture>();
        if (street == null)
        {
            street = cameraObject.AddComponent<ProceduralStreetTexture>();
        }

        DetectionRunner detection = systems.AddComponent<DetectionRunner>();
        SetReference(detection, "modelAsset", yoloModel);
        SetReference(detection, "feed", feed);
        SegmentationRunner segmentation = systems.AddComponent<SegmentationRunner>();
        SetReference(segmentation, "modelAsset", segModel);
        SetReference(segmentation, "feed", feed);

        // --- Player sphere ---------------------------------------------------
        GameObject playerRoot = new GameObject("Player");
        playerRoot.transform.position = new Vector3(0f, SphereRunnerController.GroundY, 0f);
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.DestroyImmediate(sphere.GetComponent<Collider>());
        sphere.name = "Ball";
        sphere.transform.SetParent(playerRoot.transform, false);
        Shader ballShader = Resources.Load<Shader>("BallLit");
        if (ballShader == null)
        {
            ballShader = Shader.Find("Universal Render Pipeline/Lit");
        }

        Material ballMaterial = new Material(ballShader) { name = "CV Ball" };
        ballMaterial.color = new Color(0.95f, 0.55f, 0.1f);
        AssetDatabase.CreateAsset(ballMaterial, "Assets/Materials/CvBall.mat");
        sphere.GetComponent<Renderer>().sharedMaterial = ballMaterial;

        GameObject shadow = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.DestroyImmediate(shadow.GetComponent<Collider>());
        shadow.name = "BlobShadow";
        shadow.transform.SetParent(playerRoot.transform, false);
        shadow.transform.localPosition = new Vector3(0f, -SphereRunnerController.GroundY + 0.02f, 0f);
        shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        shadow.transform.localScale = new Vector3(1.2f, 1.2f, 1f);
        Shader blobShader = Resources.Load<Shader>("BlobShadow");
        if (blobShader != null)
        {
            Material blobMaterial = new Material(blobShader) { name = "CV Blob Shadow" };
            AssetDatabase.CreateAsset(blobMaterial, "Assets/Materials/CvBlobShadow.mat");
            shadow.GetComponent<Renderer>().sharedMaterial = blobMaterial;
        }

        SphereRunnerController runner = playerRoot.AddComponent<SphereRunnerController>();
        SetReference(runner, "visualSphere", sphere.transform);

        // --- Obstacles + director -------------------------------------------
        ObstacleField field = new GameObject("ObstacleField").AddComponent<ObstacleField>();
        SetReference(field, "player", runner);

        // --- HUD --------------------------------------------------------------
        Canvas canvas = CreateCanvas();
        TMP_Text score = CreateText(canvas.transform, font, "ScoreText", 40f,
            new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(800f, 54f), TextAlignmentOptions.Center);
        TMP_Text status = CreateText(canvas.transform, font, "StatusText", 26f,
            new Vector2(1f, 1f), new Vector2(-30f, -30f), new Vector2(640f, 40f), TextAlignmentOptions.MidlineRight);
        TMP_Text hint = CreateText(canvas.transform, font, "HintText", 32f,
            new Vector2(0.5f, 0f), new Vector2(0f, 90f), new Vector2(900f, 46f), TextAlignmentOptions.Center);
        hint.color = new Color(1f, 0.85f, 0.4f);

        GameObject gameOverPanel = new GameObject("GameOverPanel", typeof(RectTransform));
        gameOverPanel.transform.SetParent(canvas.transform, false);
        RectTransform panelRect = gameOverPanel.GetComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(560f, 280f);
        Image panelImage = gameOverPanel.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.78f);
        TMP_Text gameOverText = CreateText(gameOverPanel.transform, font, "Title", 52f,
            new Vector2(0.5f, 0.5f), new Vector2(0f, 70f), new Vector2(520f, 70f), TextAlignmentOptions.Center);
        gameOverText.text = "Run Over";
        gameOverText.color = Color.white;

        GameObject buttonObject = new GameObject("RestartButton", typeof(RectTransform));
        buttonObject.transform.SetParent(gameOverPanel.transform, false);
        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.sizeDelta = new Vector2(280f, 76f);
        buttonRect.anchoredPosition = new Vector2(0f, -55f);
        Image buttonImage = buttonObject.AddComponent<Image>();
        buttonImage.color = new Color(0.95f, 0.55f, 0.1f);
        Button restart = buttonObject.AddComponent<Button>();
        restart.targetGraphic = buttonImage;
        TMP_Text buttonLabel = CreateText(buttonObject.transform, font, "Label", 34f,
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(260f, 64f), TextAlignmentOptions.Center);
        buttonLabel.text = "Restart";
        buttonLabel.color = Color.black;

        // --- Director ----------------------------------------------------------
        CvGameDirector directorComponent = systems.AddComponent<CvGameDirector>();
        SetReference(directorComponent, "speedController", speed);
        SetReference(directorComponent, "detectionRunner", detection);
        SetReference(directorComponent, "segmentationRunner", segmentation);
        SetReference(directorComponent, "obstacleField", field);
        SetReference(directorComponent, "player", runner);
        SetReference(directorComponent, "proceduralStreet", street);
        SetReference(directorComponent, "scoreText", score);
        SetReference(directorComponent, "statusText", status);
        SetReference(directorComponent, "hintText", hint);
        SetReference(directorComponent, "gameOverPanel", gameOverPanel);
        UnityEventTools.AddPersistentListener(restart.onClick, directorComponent.RestartGame);

        DebugDetectionOverlay overlay = systems.AddComponent<DebugDetectionOverlay>();
        SetReference(overlay, "detectionRunner", detection);
        SetReference(overlay, "feed", feed);

        GameObject eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
        eventSystem.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();

        gameOverPanel.SetActive(false);
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("CV Runner scene built. Press Play to try it with the procedural street, or build to device for the live camera.");
    }

    private static Canvas CreateCanvas()
    {
        GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform));
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObject.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    private static TMP_Text CreateText(
        Transform parent,
        TMP_FontAsset font,
        string name,
        float size,
        Vector2 anchor,
        Vector2 position,
        Vector2 dimensions,
        TextAlignmentOptions alignment)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
        {
            text.font = font;
        }

        text.fontSize = size;
        text.color = Color.white;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void SetReference(Object target, string propertyName, Object value)
    {
        SerializedObject serialized = new SerializedObject(target);
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            Debug.LogError($"Property {propertyName} not found on {target.GetType().Name}.");
            return;
        }

        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(target);
    }
}
