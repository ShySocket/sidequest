using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Exports the VideoRunner scene as its own iPhone app, separate from the
/// VehicleRunner prototype already on the device.
/// </summary>
/// <remarks>
/// Two things decide whether iOS treats this as a second app or as an update
/// that overwrites the first: the bundle identifier and, for the home screen,
/// the product name. Both live in project-wide Player Settings, so the only way
/// to keep two apps buildable from one project is to apply this app's identity,
/// build, and put the previous values back - which is what this does. Nothing
/// is left mutated once the export finishes, so building the prototype
/// afterwards still produces the prototype.
///
/// The scene list is overridden the same way and for the same reason: the build
/// settings list starts with RunnerPrototype, so a default build would boot
/// into the prototype no matter which scene was open in the editor.
/// </remarks>
public static class VideoRunnerPhoneBuild
{
    const string ScenePath = "Assets/Scenes/VideoRunner.unity";
    const string BundleIdentifier = "com.saibhandar.videorunner";
    const string ProductName = "Video Runner";
    const string OutputFolder = "Builds/VideoRunner-iOS";

    [MenuItem("Tools/Sidequest/Export Video Runner to iPhone")]
    public static void Export()
    {
        if (!File.Exists(ScenePath))
        {
            Debug.LogError(
                $"VideoRunnerPhoneBuild: {ScenePath} is missing. "
                + "Run Tools/Sidequest/Build Video Runner first.");
            return;
        }

        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS))
        {
            Debug.LogError(
                "VideoRunnerPhoneBuild: iOS Build Support is not installed for this "
                + "Unity version. Add it from Unity Hub > Installs.");
            return;
        }

        // Switching platform reimports every asset, so it is worth skipping when
        // the project is already on iOS from a previous export.
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS
            && !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS))
        {
            Debug.LogError("VideoRunnerPhoneBuild: could not switch the active platform to iOS.");
            return;
        }

        var iosTarget = NamedBuildTarget.iOS;
        string previousIdentifier = PlayerSettings.GetApplicationIdentifier(iosTarget);
        string previousProductName = PlayerSettings.productName;

        string outputPath = Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? ".", OutputFolder);

        try
        {
            PlayerSettings.SetApplicationIdentifier(iosTarget, BundleIdentifier);
            PlayerSettings.productName = ProductName;

            // Xcode picks the signing team from the account signed in there,
            // which is the only place a personal Apple ID can be used anyway.
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = BuildOptions.None,
            };

            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.LogError($"VideoRunnerPhoneBuild: export {report.summary.result}.");
                return;
            }

            Debug.Log(
                $"VideoRunnerPhoneBuild: exported '{ProductName}' ({BundleIdentifier}) to "
                + $"{outputPath}. Open Unity-iPhone.xcodeproj there, set your signing team, "
                + "and Run onto the phone.");
            EditorUtility.RevealInFinder(outputPath);
        }
        finally
        {
            // Restored even when the build throws: leaving this app's identity
            // applied would silently retarget the next prototype build.
            PlayerSettings.SetApplicationIdentifier(iosTarget, previousIdentifier);
            PlayerSettings.productName = previousProductName;
        }
    }
}
