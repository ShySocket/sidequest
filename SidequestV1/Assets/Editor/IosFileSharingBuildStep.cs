#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// Marks the exported iOS app's Documents folder as user-visible, so a drive
/// recorded by SpeedTraceRecorder (persistentDataPath/speed-traces/) can be
/// copied off the phone with the Files app instead of an Xcode container dump.
/// </summary>
public static class IosFileSharingBuildStep
{
    [PostProcessBuild]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS)
        {
            return;
        }

        string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        plist.root.SetBoolean("UIFileSharingEnabled", true);
        plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        plist.WriteToFile(plistPath);
    }
}
#endif
