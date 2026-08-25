using UnityEngine;

/// <summary>
/// Developer overlay: draws the latest detector boxes over the screen.
/// Toggled with a three-finger tap on device or F1 in the editor.
/// </summary>
public sealed class DebugDetectionOverlay : MonoBehaviour
{
    [SerializeField] private DetectionRunner detectionRunner;
    [SerializeField] private LiveCameraFeed feed;

    private bool visible;
    private Texture2D fill;

    private void Start()
    {
        fill = new Texture2D(1, 1);
        fill.SetPixel(0, 0, new Color(1f, 0.4f, 0.1f, 0.9f));
        fill.Apply();
    }

    private void Update()
    {
        if (UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.f1Key.wasPressedThisFrame)
        {
            visible = !visible;
        }

        var touchscreen = UnityEngine.InputSystem.Touchscreen.current;
        if (touchscreen != null)
        {
            int pressed = 0;
            foreach (var touch in touchscreen.touches)
            {
                if (touch.press.isPressed)
                {
                    pressed++;
                }
            }

            if (pressed >= 3)
            {
                visible = true;
            }
        }
    }

    private void OnGUI()
    {
        if (!visible || detectionRunner == null || feed == null || feed.SourceTexture == null)
        {
            return;
        }

        float sw = feed.SourceTexture.width;
        float sh = feed.SourceTexture.height;
        foreach (Detection d in detectionRunner.LastDetections)
        {
            float x = d.Left / sw * Screen.width;
            float y = d.Top / sh * Screen.height;
            float w = d.Width / sw * Screen.width;
            float h = d.Height / sh * Screen.height;
            DrawRect(new Rect(x, y, w, h), 2f);
            GUI.Label(new Rect(x + 3, y + 1, 220, 22), $"{d.ClassId} {d.Score:0.00}");
        }
    }

    private void DrawRect(Rect rect, float thickness)
    {
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), fill);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), fill);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), fill);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), fill);
    }
}
