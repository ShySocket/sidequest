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
    const string VideoFileName = "IMG_3775.mov";

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
        SetPrivateField(background, "videoFileName", VideoFileName);
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
        var renderer = holder.AddComponent<SpriteRenderer>();
        renderer.sprite = BuildSprite();
        renderer.color = new Color(0.15f, 0.85f, 1f);
        renderer.sortingOrder = 100;

        VideoRunnerCharacter character = holder.AddComponent<VideoRunnerCharacter>();
        SetPrivateField(character, "director", director);
        SetPrivateField(character, "background", background);
        SetPrivateField(character, "body", renderer);
        return character;
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
