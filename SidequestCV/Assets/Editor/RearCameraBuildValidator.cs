using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class RearCameraBuildValidator : IPreprocessBuildWithReport
{
    private const string ShaderPath = "Assets/Resources/RearCameraBackground.shader";
    private const string ShaderGuid = "2c11db93c272496b89e712e222c72921";
    private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
    private const string PlayerSettingsPath = "ProjectSettings/ProjectSettings.asset";
    private const string AndroidManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        ValidateBundledShader();

        if (report.summary.platform == BuildTarget.iOS)
        {
            ValidateIosCameraDescription();
        }
        else if (report.summary.platform == BuildTarget.Android)
        {
            ValidateAndroidManifest();
        }

        Debug.Log($"Rear camera build validation passed for {report.summary.platform}.");
    }

    private static void ValidateBundledShader()
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null)
        {
            throw new BuildFailedException(
                $"Rear camera shader is missing at {ShaderPath}. "
                + "The live camera background would not render.");
        }

        string graphicsSettings = File.ReadAllText(GraphicsSettingsPath);
        if (!graphicsSettings.Contains(ShaderGuid))
        {
            throw new BuildFailedException(
                "Rear camera shader is not in Always Included Shaders. "
                + "Add Sidequest/RearCameraBackground in Project Settings > Graphics.");
        }
    }

    private static void ValidateIosCameraDescription()
    {
        Object playerSettingsAsset = AssetDatabase.LoadAllAssetsAtPath(PlayerSettingsPath)[0];
        SerializedObject playerSettings = new SerializedObject(playerSettingsAsset);
        SerializedProperty description = playerSettings.FindProperty("cameraUsageDescription");
        if (description == null || string.IsNullOrWhiteSpace(description.stringValue))
        {
            throw new BuildFailedException(
                "iOS Camera Usage Description is empty. "
                + "Set it in Project Settings > Player before building.");
        }
    }

    private static void ValidateAndroidManifest()
    {
        if (!File.Exists(AndroidManifestPath))
        {
            throw new BuildFailedException(
                $"Android manifest is missing at {AndroidManifestPath}.");
        }

        string manifest = File.ReadAllText(AndroidManifestPath);
        if (!manifest.Contains("android.permission.CAMERA"))
        {
            throw new BuildFailedException(
                "Android manifest does not declare android.permission.CAMERA.");
        }
    }
}
