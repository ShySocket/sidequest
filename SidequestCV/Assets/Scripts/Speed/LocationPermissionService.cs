using System;
using System.Collections;
using UnityEngine;

public sealed class LocationPermissionService : MonoBehaviour
{
    public IEnumerator RequestPermission(Action<bool> completed)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        string permission = UnityEngine.Android.Permission.FineLocation;
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
        {
            completed(true);
            yield break;
        }

        bool finished = false;
        bool granted = false;
        UnityEngine.Android.PermissionCallbacks callbacks = new UnityEngine.Android.PermissionCallbacks();
        callbacks.PermissionGranted += _ =>
        {
            granted = true;
            finished = true;
        };
        callbacks.PermissionDenied += _ => finished = true;
        callbacks.PermissionDeniedAndDontAskAgain += _ => finished = true;
        UnityEngine.Android.Permission.RequestUserPermission(permission, callbacks);

        while (!finished)
        {
            yield return null;
        }

        completed(granted);
#else
        yield return null;
        completed(true);
#endif
    }
}
