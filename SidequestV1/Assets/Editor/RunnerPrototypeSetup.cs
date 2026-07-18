using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class RunnerPrototypeSetup
{
    private const string ScenePath = "Assets/Scenes/RunnerPrototype.unity";
    private const string ConfigurationPath = "Assets/ScriptableObjects/Configurations/DefaultRunnerConfiguration.asset";
    private const string InputActionsPath = "Assets/Input/RunnerInputActions.inputactions";
    private const string JumpReferencePath = "Assets/Input/Jump.inputactionreference.asset";
    private const string SquareTexturePath = "Assets/Materials/PrototypeSquare.png";
    private const string PhysicsMaterialPath = "Assets/Materials/NoFriction.physicsMaterial2D";
    private const string FontAssetPath =
        "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
    private const string PlayerPrefabPath = "Assets/Prefabs/Player/Player.prefab";
    private const string ObstaclePrefabPath = "Assets/Prefabs/Obstacles/SmallBoxObstacle.prefab";
    private const string GpsStatusPrefabPath = "Assets/Prefabs/UI/GpsStatusPanel.prefab";

    [MenuItem("Tools/Sidequest/Build VehicleRunner V1")]
    public static void BuildVehicleRunnerV1()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("Exit Play Mode before building VehicleRunner V1.");
            return;
        }

        CreateFolders();
        ConfigureTagsAndLayers();
        ConfigurePlayerSettings();

        Sprite squareSprite = CreateSquareSprite();
        PhysicsMaterial2D noFriction = CreateNoFrictionMaterial();
        TMP_FontAsset fontAsset = CreateFontAsset();
        InputActionReference jumpReference = CreateInputActions();

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "RunnerPrototype";
        RunnerConfiguration configuration = CreateConfiguration();
        if (configuration == null || !EditorUtility.IsPersistent(configuration))
        {
            throw new InvalidDataException("DefaultRunnerConfiguration could not be loaded as a persistent asset.");
        }

        GameObject gameSystems = new GameObject("GameSystems");
        MockSpeedProvider mockProvider = CreateChild<MockSpeedProvider>(gameSystems.transform, "MockSpeedProvider");
        GameObject gpsObject = new GameObject("UnityGpsSpeedProvider");
        gpsObject.transform.SetParent(gameSystems.transform);
        LocationPermissionService permissionService = gpsObject.AddComponent<LocationPermissionService>();
        UnityGpsSpeedProvider gpsProvider = gpsObject.AddComponent<UnityGpsSpeedProvider>();
        SetObjectReference(gpsProvider, "configuration", configuration);
        SetObjectReference(gpsProvider, "permissionService", permissionService);

        VehicleSpeedController speedController = CreateChild<VehicleSpeedController>(
            gameSystems.transform,
            "VehicleSpeedController");
        SetObjectReference(speedController, "configuration", configuration);
        SetObjectReference(speedController, "mockSpeedProvider", mockProvider);
        SetObjectReference(speedController, "unityGpsSpeedProvider", gpsProvider);
        SetEnum(speedController, "providerMode", (int)SpeedProviderMode.Auto);

        GameObject environment = new GameObject("Environment");
        CreateGround(environment.transform, squareSprite);
        CreateObstacles(environment.transform, squareSprite);

        GameObject player = CreatePlayer(
            squareSprite,
            noFriction,
            configuration,
            jumpReference,
            speedController);
        Rigidbody2D playerBody = player.GetComponent<Rigidbody2D>();
        RunnerMotor runnerMotor = player.GetComponent<RunnerMotor>();
        GroundCheck groundCheck = player.GetComponent<GroundCheck>();
        PlayerJump playerJump = player.GetComponent<PlayerJump>();

        CreateCamera(player.transform);
        Canvas canvas = CreateCanvas();
        GameObject gpsStatusPanel = CreateGpsStatusPanel(canvas.transform, fontAsset, speedController);
        GameObject gameOverPanel = CreateGameOverPanel(canvas.transform, fontAsset, out Button restartButton);

        GameManager gameManager = CreateChild<GameManager>(gameSystems.transform, "GameManager");
        SetObjectReference(gameManager, "vehicleSpeedController", speedController);
        SetObjectReference(gameManager, "playerBody", playerBody);
        SetObjectReference(gameManager, "runnerMotor", runnerMotor);
        SetObjectReference(gameManager, "playerJump", playerJump);
        SetObjectReference(gameManager, "gameOverPanel", gameOverPanel);

        PlayerCollision collision = player.GetComponent<PlayerCollision>();
        SetObjectReference(collision, "gameManager", gameManager);
        UnityEventTools.AddPersistentListener(restartButton.onClick, gameManager.RestartGame);
        CreateDebugPanel(
            canvas.transform,
            fontAsset,
            speedController,
            gpsProvider,
            gameManager,
            groundCheck,
            playerBody);
        CreateEventSystem();

        gameOverPanel.SetActive(false);
        EditorSceneManager.SaveScene(scene, ScenePath);
        ConfigureBuildScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("VehicleRunner V1 setup complete: configuration, scene, prefabs, UI, and build settings saved.");
        Selection.activeGameObject = gpsStatusPanel;
    }

    private static void CreateFolders()
    {
        EnsureFolder("Assets/Input");
        EnsureFolder("Assets/Materials");
        EnsureFolder("Assets/Prefabs/Player");
        EnsureFolder("Assets/Prefabs/Obstacles");
        EnsureFolder("Assets/Prefabs/UI");
        EnsureFolder("Assets/Scenes");
        EnsureFolder("Assets/ScriptableObjects/Configurations");
        EnsureFolder("Assets/Scripts/Camera");
        EnsureFolder("Assets/Scripts/Configuration");
        EnsureFolder("Assets/Scripts/Core");
        EnsureFolder("Assets/Scripts/Player");
        EnsureFolder("Assets/Scripts/Speed");
        EnsureFolder("Assets/Scripts/UI");
        EnsureFolder("Assets/Tests/EditMode");
        EnsureFolder("Assets/Tests/PlayMode");
        EnsureFolder("Assets/Documentation");
        EnsureFolder("Assets/Plugins/Android");
        EnsureFolder("Assets/TextMesh Pro/Resources");
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent))
        {
            EnsureFolder(parent);
        }

        AssetDatabase.CreateFolder(parent, name);
    }

    private static void ConfigureTagsAndLayers()
    {
        SerializedObject tagManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        EnsureTag(tagManager.FindProperty("tags"), "Obstacle");
        SerializedProperty layers = tagManager.FindProperty("layers");
        EnsureLayer(layers, "Player", 8);
        EnsureLayer(layers, "Ground", 9);
        EnsureLayer(layers, "Obstacle", 10);
        tagManager.ApplyModifiedProperties();
    }

    private static void EnsureTag(SerializedProperty tags, string tagName)
    {
        for (int i = 0; i < tags.arraySize; i++)
        {
            if (tags.GetArrayElementAtIndex(i).stringValue == tagName)
            {
                return;
            }
        }

        int index = tags.arraySize;
        tags.InsertArrayElementAtIndex(index);
        tags.GetArrayElementAtIndex(index).stringValue = tagName;
    }

    private static void EnsureLayer(SerializedProperty layers, string layerName, int preferredIndex)
    {
        for (int i = 0; i < layers.arraySize; i++)
        {
            if (layers.GetArrayElementAtIndex(i).stringValue == layerName)
            {
                return;
            }
        }

        layers.GetArrayElementAtIndex(preferredIndex).stringValue = layerName;
    }

    private static void ConfigurePlayerSettings()
    {
        SerializedObject settings = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        SerializedProperty activeInputHandler = settings.FindProperty("activeInputHandler");
        if (activeInputHandler != null)
        {
            activeInputHandler.intValue = 1;
            settings.ApplyModifiedProperties();
        }

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.iOS.locationUsageDescription =
            "VehicleRunner uses your location while the game is open to estimate travel speed and control player movement.";

        const string manifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
        string manifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n"
            + "  <uses-permission android:name=\"android.permission.ACCESS_FINE_LOCATION\" />\n"
            + "  <application />\n"
            + "</manifest>\n";
        File.WriteAllText(manifestPath, manifest);
        AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceUpdate);
    }

    private static RunnerConfiguration CreateConfiguration()
    {
        RunnerConfiguration configuration = AssetDatabase.LoadAssetAtPath<RunnerConfiguration>(ConfigurationPath);
        if (configuration == null)
        {
            configuration = ScriptableObject.CreateInstance<RunnerConfiguration>();
            AssetDatabase.CreateAsset(configuration, ConfigurationPath);
        }

        SerializedObject serialized = new SerializedObject(configuration);
        serialized.FindProperty("playbackDelaySeconds").floatValue = 1f;
        serialized.FindProperty("speedHistoryDurationSeconds").floatValue = 5f;
        serialized.FindProperty("accelerationSmoothingTime").floatValue = 0.25f;
        serialized.FindProperty("decelerationSmoothingTime").floatValue = 0.35f;
        serialized.FindProperty("desiredGpsAccuracyMeters").floatValue = 5f;
        serialized.FindProperty("gpsUpdateDistanceMeters").floatValue = 1f;
        serialized.FindProperty("gpsInitializationTimeoutSeconds").floatValue = 20f;
        serialized.FindProperty("gpsPollingIntervalSeconds").floatValue = 0.25f;
        serialized.FindProperty("maximumAcceptedHorizontalAccuracyMeters").floatValue = 25f;
        serialized.FindProperty("maximumAcceptedPhysicalSpeedMetersPerSecond").floatValue = 80f;
        serialized.FindProperty("gpsStaleTimeoutSeconds").floatValue = 3f;
        serialized.FindProperty("stopThresholdMetersPerSecond").floatValue = 0.75f;
        serialized.FindProperty("requiredConsecutiveLowSpeedReadings").intValue = 3;
        serialized.FindProperty("maximumGameSpeed").floatValue = 10f;
        serialized.FindProperty("jumpVelocity").floatValue = 8f;
        serialized.FindProperty("physicalToGameSpeedCurve").animationCurveValue = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(2f, 2f),
            new Keyframe(5f, 4f),
            new Keyframe(10f, 6f),
            new Keyframe(20f, 8f),
            new Keyframe(30f, 10f),
            new Keyframe(40f, 10f),
            new Keyframe(80f, 10f));
        serialized.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(ConfigurationPath, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<RunnerConfiguration>(ConfigurationPath);
    }

    private static Sprite CreateSquareSprite()
    {
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        File.WriteAllBytes(SquareTexturePath, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(SquareTexturePath, ImportAssetOptions.ForceUpdate);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(SquareTexturePath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spritePixelsPerUnit = 1f;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Point;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(SquareTexturePath);
    }

    private static PhysicsMaterial2D CreateNoFrictionMaterial()
    {
        PhysicsMaterial2D material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(PhysicsMaterialPath);
        if (material == null)
        {
            material = new PhysicsMaterial2D("NoFriction");
            AssetDatabase.CreateAsset(material, PhysicsMaterialPath);
        }

        material.friction = 0f;
        material.bounciness = 0f;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static TMP_FontAsset CreateFontAsset()
    {
        TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (fontAsset == null)
        {
            throw new FileNotFoundException("TextMeshPro Essential Resources are missing.", FontAssetPath);
        }

        return fontAsset;
    }

    private static InputActionReference CreateInputActions()
    {
        InputActionAsset asset = ScriptableObject.CreateInstance<InputActionAsset>();
        InputActionMap gameplay = new InputActionMap("Gameplay");
        InputAction jump = gameplay.AddAction("Jump", InputActionType.Button);
        jump.expectedControlType = "Button";
        jump.AddBinding("<Keyboard>/space");
        jump.AddBinding("<Touchscreen>/primaryTouch/press");
        asset.AddActionMap(gameplay);
        File.WriteAllText(InputActionsPath, asset.ToJson());
        Object.DestroyImmediate(asset);
        AssetDatabase.ImportAsset(InputActionsPath, ImportAssetOptions.ForceUpdate);

        InputActionAsset importedAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
        InputActionReference reference = AssetDatabase.LoadAssetAtPath<InputActionReference>(JumpReferencePath);
        InputAction importedJump = importedAsset.FindAction("Gameplay/Jump", true);
        if (reference == null)
        {
            reference = InputActionReference.Create(importedJump);
            reference.name = "Jump.inputactionreference";
            AssetDatabase.CreateAsset(reference, JumpReferencePath);
        }
        else
        {
            reference.Set(importedJump);
            reference.name = "Jump.inputactionreference";
            EditorUtility.SetDirty(reference);
        }

        return reference;
    }

    private static void CreateGround(Transform environment, Sprite sprite)
    {
        GameObject ground = CreateSpriteObject("Ground", sprite, "Ground");
        ground.transform.SetParent(environment);
        ground.transform.position = new Vector3(30f, -0.5f, 0f);
        ground.transform.localScale = new Vector3(80f, 1f, 1f);
        ground.AddComponent<BoxCollider2D>();
    }

    private static void CreateObstacles(Transform environment, Sprite sprite)
    {
        GameObject root = new GameObject("Obstacles");
        root.transform.SetParent(environment);
        GameObject source = CreateSpriteObject("SmallBoxObstacle", sprite, "Obstacle");
        source.tag = "Obstacle";
        source.AddComponent<BoxCollider2D>();
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, ObstaclePrefabPath);
        Object.DestroyImmediate(source);

        float[] positions = { 10f, 18f, 28f, 32f, 44f };
        for (int i = 0; i < positions.Length; i++)
        {
            GameObject obstacle = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            obstacle.name = $"SmallBoxObstacle_{i + 1:00}";
            obstacle.transform.SetParent(root.transform);
            obstacle.transform.position = new Vector3(positions[i], 0.5f, 0f);
        }
    }

    private static GameObject CreatePlayer(
        Sprite sprite,
        PhysicsMaterial2D noFriction,
        RunnerConfiguration configuration,
        InputActionReference jumpReference,
        VehicleSpeedController speedController)
    {
        GameObject source = CreateSpriteObject("Player", sprite, "Player");
        Rigidbody2D body = source.AddComponent<Rigidbody2D>();
        body.gravityScale = 4f;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;
        body.constraints = RigidbodyConstraints2D.FreezeRotation;
        source.AddComponent<BoxCollider2D>().sharedMaterial = noFriction;
        GameObject point = new GameObject("GroundCheckPoint");
        point.layer = source.layer;
        point.transform.SetParent(source.transform);
        point.transform.localPosition = new Vector3(0f, -0.55f, 0f);
        GroundCheck groundCheck = source.AddComponent<GroundCheck>();
        SetObjectReference(groundCheck, "groundCheckPoint", point.transform);
        SetLayerMask(groundCheck, "groundLayer", LayerMask.GetMask("Ground"));
        PlayerJump jump = source.AddComponent<PlayerJump>();
        SetObjectReference(jump, "jumpAction", jumpReference);
        SetObjectReference(jump, "configuration", configuration);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, PlayerPrefabPath);
        Object.DestroyImmediate(source);
        GameObject player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        player.transform.position = new Vector3(0f, 0.5f, 0f);
        RunnerMotor motor = player.AddComponent<RunnerMotor>();
        SetObjectReference(motor, "vehicleSpeedController", speedController);
        player.AddComponent<PlayerCollision>();
        return player;
    }

    private static void CreateCamera(Transform player)
    {
        GameObject cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.white;
        camera.orthographic = true;
        camera.orthographicSize = 5f;
        cameraObject.transform.position = new Vector3(0f, 2f, -10f);
        CameraFollow2D follow = cameraObject.AddComponent<CameraFollow2D>();
        SetObjectReference(follow, "target", player);
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

    private static GameObject CreateGpsStatusPanel(
        Transform canvas,
        TMP_FontAsset font,
        VehicleSpeedController controller)
    {
        GameObject source = CreatePanel("GpsStatusPanel", new Vector2(460f, 100f), false);
        CreateText(source.transform, "StatusText", font, "GPS: INITIALIZING", 30f,
            new Vector2(0f, 24f), new Vector2(420f, 42f), TextAlignmentOptions.MidlineRight);
        CreateText(source.transform, "SpeedText", font, "Speed: 0.0 m/s", 26f,
            new Vector2(0f, -22f), new Vector2(420f, 38f), TextAlignmentOptions.MidlineRight);
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, GpsStatusPrefabPath);
        Object.DestroyImmediate(source);
        GameObject panel = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        panel.transform.SetParent(canvas, false);
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.one;
        rect.anchorMax = Vector2.one;
        rect.pivot = Vector2.one;
        rect.anchoredPosition = new Vector2(-30f, -30f);
        GpsStatusIndicator indicator = panel.AddComponent<GpsStatusIndicator>();
        SetObjectReference(indicator, "vehicleSpeedController", controller);
        SetObjectReference(indicator, "statusText", panel.transform.Find("StatusText").GetComponent<TMP_Text>());
        SetObjectReference(indicator, "speedText", panel.transform.Find("SpeedText").GetComponent<TMP_Text>());
        return panel;
    }

    private static GameObject CreateGameOverPanel(Transform canvas, TMP_FontAsset font, out Button restartButton)
    {
        GameObject panel = CreatePanel("GameOverPanel", new Vector2(520f, 260f), true);
        panel.transform.SetParent(canvas, false);
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        CreateText(panel.transform, "GameOverText", font, "Run Over", 52f,
            new Vector2(0f, 62f), new Vector2(440f, 70f), TextAlignmentOptions.Center);

        GameObject buttonObject = new GameObject("RestartButton", typeof(RectTransform));
        buttonObject.transform.SetParent(panel.transform, false);
        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.sizeDelta = new Vector2(260f, 72f);
        buttonRect.anchoredPosition = new Vector2(0f, -60f);
        Image image = buttonObject.AddComponent<Image>();
        image.color = Color.black;
        restartButton = buttonObject.AddComponent<Button>();
        restartButton.targetGraphic = image;
        TMP_Text label = CreateText(buttonObject.transform, "Text", font, "Restart", 34f,
            Vector2.zero, new Vector2(240f, 64f), TextAlignmentOptions.Center);
        label.color = Color.white;
        return panel;
    }

    private static void CreateDebugPanel(
        Transform canvas,
        TMP_FontAsset font,
        VehicleSpeedController controller,
        UnityGpsSpeedProvider gpsProvider,
        GameManager gameManager,
        GroundCheck groundCheck,
        Rigidbody2D playerBody)
    {
        GameObject panel = CreatePanel("DebugSpeedPanel", new Vector2(520f, 690f), false);
        panel.transform.SetParent(canvas, false);
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(30f, -30f);
        TMP_Text text = CreateText(panel.transform, "TelemetryText", font, string.Empty, 22f,
            Vector2.zero, new Vector2(480f, 650f), TextAlignmentOptions.TopLeft);
        DebugSpeedPanel debug = panel.AddComponent<DebugSpeedPanel>();
        SetObjectReference(debug, "vehicleSpeedController", controller);
        SetObjectReference(debug, "gpsSpeedProvider", gpsProvider);
        SetObjectReference(debug, "gameManager", gameManager);
        SetObjectReference(debug, "groundCheck", groundCheck);
        SetObjectReference(debug, "playerBody", playerBody);
        SetObjectReference(debug, "telemetryText", text);
        panel.SetActive(false);
    }

    private static GameObject CreatePanel(string name, Vector2 size, bool raycastTarget)
    {
        GameObject panel = new GameObject(name, typeof(RectTransform));
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        Image image = panel.AddComponent<Image>();
        image.color = Color.white;
        image.raycastTarget = raycastTarget;
        Outline outline = panel.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(3f, -3f);
        return panel;
    }

    private static TMP_Text CreateText(
        Transform parent,
        string name,
        TMP_FontAsset font,
        string value,
        float size,
        Vector2 position,
        Vector2 dimensions,
        TextAlignmentOptions alignment)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.sizeDelta = dimensions;
        rect.anchoredPosition = position;
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.text = value;
        text.fontSize = size;
        text.color = Color.black;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void CreateEventSystem()
    {
        GameObject eventSystemObject = new GameObject("EventSystem");
        eventSystemObject.AddComponent<EventSystem>();
        InputSystemUIInputModule module = eventSystemObject.AddComponent<InputSystemUIInputModule>();
        module.AssignDefaultActions();
    }

    private static GameObject CreateSpriteObject(string name, Sprite sprite, string layerName)
    {
        GameObject gameObject = new GameObject(name);
        gameObject.layer = LayerMask.NameToLayer(layerName);
        SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.color = Color.black;
        return gameObject;
    }

    private static T CreateChild<T>(Transform parent, string name) where T : Component
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent);
        return child.AddComponent<T>();
    }

    private static void ConfigureBuildScenes()
    {
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };
    }

    private static void SetObjectReference(Object target, string propertyName, Object value)
    {
        SerializedObject serialized = new SerializedObject(target);
        SerializedProperty property = serialized.FindProperty(propertyName);
        property.objectReferenceValue = value;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private static void SetLayerMask(Object target, string propertyName, int value)
    {
        SerializedObject serialized = new SerializedObject(target);
        serialized.FindProperty(propertyName).intValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetEnum(Object target, string propertyName, int value)
    {
        SerializedObject serialized = new SerializedObject(target);
        serialized.FindProperty(propertyName).enumValueIndex = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
