using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

/// <summary>
/// Builds the video-runner scene from scratch, the way RunnerPrototypeSetup does
/// for the original prototype.
/// </summary>
/// <remarks>
/// The scene is generated rather than committed so it cannot drift from the
/// scripts, and so re-running the menu item is always a safe way back to a known
/// state. It is a separate scene from RunnerPrototype, which is left untouched.
/// </remarks>
public static class VideoRunnerSetup
{
    const string SceneFolder = "Assets/Scenes";
    const string ScenePath = SceneFolder + "/VideoRunner.unity";
    const string StreamingAssets = "Assets/StreamingAssets";
    const string LevelFileName = "IMG_3775.authored.json";
    const string VideoFileName = "IMG_3775.play.mp4";

    [MenuItem("Tools/Sidequest/Build Video Runner")]
    public static void Build()
    {
        if (!VerifyStreamingAssets())
        {
            return;
        }

        Scene scene = EditorSceneManager.NewScene(
            NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Camera camera = CreateCamera();
        VideoBackground background = CreateBackground(camera);
        LevelDirector director = CreateDirector(background);
        VideoRunnerCharacter character = CreateCharacter(director, background);
        CreateHud(director, character);
        CreateSunLight();

        if (!Directory.Exists(SceneFolder))
        {
            Directory.CreateDirectory(SceneFolder);
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        RegisterScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            "Video Runner built at " + ScenePath
            + ". Press Play, or File > Build Settings to make a player.");
    }

    static bool VerifyStreamingAssets()
    {
        if (!Directory.Exists(StreamingAssets))
        {
            Directory.CreateDirectory(StreamingAssets);
            AssetDatabase.Refresh();
        }

        var missing = new List<string>();
        if (!File.Exists(Path.Combine(StreamingAssets, LevelFileName)))
        {
            missing.Add(LevelFileName);
        }

        if (!File.Exists(Path.Combine(StreamingAssets, VideoFileName)))
        {
            missing.Add(VideoFileName);
        }

        if (missing.Count == 0)
        {
            return true;
        }

        EditorUtility.DisplayDialog(
            "Video Runner: missing assets",
            "These files must be in Assets/StreamingAssets before the scene can run:\n\n  "
            + string.Join("\n  ", missing)
            + "\n\nRun tools/video-analyzer/copy-to-unity.sh to put them there.",
            "OK");
        return false;
    }

    static Camera CreateCamera()
    {
        var holder = new GameObject("Main Camera");
        holder.tag = "MainCamera";

        Camera camera = holder.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 5f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.transform.position = new Vector3(0f, 0f, -10f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 200f;

        holder.AddComponent<AudioListener>();
        return camera;
    }

    static VideoBackground CreateBackground(Camera camera)
    {
        var holder = new GameObject("VideoBackground");
        holder.AddComponent<VideoPlayer>();

        VideoBackground background = holder.AddComponent<VideoBackground>();
        SetPrivateField(background, "targetCamera", camera);
        return background;
    }

    static LevelDirector CreateDirector(VideoBackground background)
    {
        var holder = new GameObject("LevelDirector");
        LevelDirector director = holder.AddComponent<LevelDirector>();
        SetPrivateField(director, "levelFileName", LevelFileName);
        SetPrivateField(director, "background", background);
        return director;
    }

    static VideoRunnerCharacter CreateCharacter(
        LevelDirector director, VideoBackground background)
    {
        var holder = new GameObject("Character");

        // A real sphere mesh, so the lighting has genuine normals to work with -
        // a flat disc with a painted highlight falls apart the moment it rolls.
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Ball";
        Object.DestroyImmediate(ball.GetComponent<Collider>());
        ball.transform.SetParent(holder.transform, false);

        var ballRenderer = ball.GetComponent<MeshRenderer>();
        ballRenderer.sharedMaterial = BuildMaterial(
            "Assets/Settings/BallLit.mat", "Sidequest/BallLit");
        ballRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ballRenderer.receiveShadows = false;

        GameObject shadow = GameObject.CreatePrimitive(PrimitiveType.Quad);
        shadow.name = "ContactShadow";
        Object.DestroyImmediate(shadow.GetComponent<Collider>());
        shadow.transform.SetParent(holder.transform, false);

        var shadowRenderer = shadow.GetComponent<MeshRenderer>();
        shadowRenderer.sharedMaterial = BuildMaterial(
            "Assets/Settings/BlobShadow.mat", "Sidequest/BlobShadow");
        shadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shadowRenderer.receiveShadows = false;

        VideoRunnerBallView view = holder.AddComponent<VideoRunnerBallView>();
        SetPrivateField(view, "ball", ball.transform);
        SetPrivateField(view, "ballRenderer", ballRenderer);
        SetPrivateField(view, "blobShadow", shadow.transform);
        SetPrivateField(view, "blobShadowRenderer", shadowRenderer);

        VideoRunnerCharacter character = holder.AddComponent<VideoRunnerCharacter>();
        SetPrivateField(character, "director", director);
        SetPrivateField(character, "background", background);
        SetPrivateField(character, "view", view);
        return character;
    }

    /// <summary>
    /// A material asset, so the shader ships with the build.
    /// </summary>
    /// <remarks>
    /// Shader.Find only resolves shaders a build actually included, and a shader
    /// referenced by nothing is stripped. Referencing it from a committed
    /// material is what keeps it present.
    /// </remarks>
    static Material BuildMaterial(string path, string shaderName)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            return existing;
        }

        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError($"VideoRunnerSetup: shader '{shaderName}' not found");
            return null;
        }

        var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    /// <summary>
    /// A light matching the sun in the footage.
    /// </summary>
    /// <remarks>
    /// Read off the clip rather than chosen: shadows in the parking-lot sections
    /// fall to the right and toward the camera, and the light is low and warm
    /// (late afternoon). The ball's shader takes direction and colour as material
    /// properties, so this light object exists mainly to document the choice and
    /// to light anything else added later.
    /// </remarks>
    static void CreateSunLight()
    {
        var holder = new GameObject("Sun");
        Light light = holder.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.94f, 0.82f);
        light.intensity = 1.25f;
        light.shadows = LightShadows.None;
        holder.transform.rotation = Quaternion.LookRotation(
            new Vector3(0.55f, -0.75f, 0.35f).normalized);
    }

    static void CreateHud(LevelDirector director, VideoRunnerCharacter character)
    {
        var holder = new GameObject("Hud");
        VideoRunnerHud hud = holder.AddComponent<VideoRunnerHud>();
        SetPrivateField(hud, "director", director);
        SetPrivateField(hud, "character", character);
    }

    /// <summary>A 1x1 white sprite, so the scene needs no imported art.</summary>
    static Sprite BuildSprite()
    {
        const string path = "Assets/Sprites/VideoRunnerBody.png";
        if (!Directory.Exists("Assets/Sprites"))
        {
            Directory.CreateDirectory("Assets/Sprites");
        }

        if (!File.Exists(path))
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = 1f;
            importer.filterMode = FilterMode.Point;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static void RegisterScene()
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (scenes.Exists(entry => entry.path == ScenePath))
        {
            return;
        }

        scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    static void SetPrivateField(Object target, string field, object value)
    {
        var serialized = new SerializedObject(target);
        SerializedProperty property = serialized.FindProperty(field);
        if (property == null)
        {
            Debug.LogWarning($"VideoRunnerSetup: no serialized field '{field}' on {target.name}");
            return;
        }

        if (value is Object unityObject)
        {
            property.objectReferenceValue = unityObject;
        }
        else if (value is string text)
        {
            property.stringValue = text;
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
