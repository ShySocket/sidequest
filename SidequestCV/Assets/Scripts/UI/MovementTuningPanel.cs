using UnityEngine;
using UnityEngine.InputSystem;

public sealed class MovementTuningPanel : MonoBehaviour
{
    private const float ReferenceScreenWidth = 1170f;
    private const float PanelWidth = 650f;
    private const float PanelHeight = 890f;

    private VehicleSpeedController controller;
    private bool isOpen;
    private GUIStyle titleStyle;
    private GUIStyle labelStyle;
    private GUIStyle hintStyle;
    private GUIStyle valueStyle;
    private Vector2 scrollPosition;

    public void Initialize(VehicleSpeedController speedController)
    {
        controller = speedController;
    }

    private void Update()
    {
        if (Keyboard.current != null
            && Keyboard.current.f8Key.wasPressedThisFrame)
        {
            isOpen = !isOpen;
        }
    }

    private void OnGUI()
    {
        if (controller == null)
        {
            return;
        }

        InitializeStyles();
        float scale = Mathf.Clamp(
            Mathf.Min(
                Screen.width / ReferenceScreenWidth,
                Screen.height / 1000f),
            0.5f,
            1.4f);
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        float virtualWidth = Screen.width / scale;
        float virtualHeight = Screen.height / scale;
        Rect toggleRect = new Rect(virtualWidth - 190f, 20f, 170f, 56f);
        if (GUI.Button(toggleRect, isOpen ? "Close tuning" : "Movement tuning"))
        {
            isOpen = !isOpen;
        }

        if (isOpen)
        {
            DrawPanel(new Rect(
                Mathf.Max(20f, virtualWidth - PanelWidth - 20f),
                90f,
                PanelWidth,
                Mathf.Min(PanelHeight, virtualHeight - 110f)));
        }

        GUI.matrix = previousMatrix;
    }

    private void DrawPanel(Rect panelRect)
    {
        GUI.Box(panelRect, GUIContent.none);
        GUILayout.BeginArea(new Rect(
            panelRect.x + 22f,
            panelRect.y + 18f,
            panelRect.width - 44f,
            panelRect.height - 36f));
        scrollPosition = GUILayout.BeginScrollView(scrollPosition);

        GUILayout.Label("Movement tuning", titleStyle);
        GUILayout.Label(
            "Adjust with a passenger or while safely parked. Changes apply immediately.",
            hintStyle);
        GUILayout.Space(12f);

        MovementTuningValues current = controller.CurrentMovementTuning;
        float gpsReliance = DrawSlider(
            "GPS vs accelerometer reliance",
            $"{current.GpsReliance * 100f:F0}% GPS / "
                + $"{current.AccelerometerReliance * 100f:F0}% accelerometer",
            current.GpsReliance,
            0f,
            1f);
        float stopSensitivity = DrawSlider(
            "Hard-brake sensitivity",
            $"{current.SuddenStopSensitivity * 100f:F0}%  "
                + "(higher recognizes gentler braking)",
            current.SuddenStopSensitivity,
            0f,
            1f);
        float noiseFilter = DrawSlider(
            "Acceleration noise filter",
            $"{current.AccelerationNoiseFilter:F2} m/s² ignored",
            current.AccelerationNoiseFilter,
            MovementTuningValues.MinimumNoiseFilter,
            MovementTuningValues.MaximumNoiseFilter);
        float staleSeconds = DrawSlider(
            "GPS stale time",
            $"{current.GpsStaleSeconds:F1} seconds",
            current.GpsStaleSeconds,
            MovementTuningValues.MinimumGpsStaleSeconds,
            MovementTuningValues.MaximumGpsStaleSeconds);
        float fallbackSeconds = DrawSlider(
            "GPS loss confidence horizon",
            $"{current.GpsLossFallbackSeconds:F0} seconds to low confidence",
            current.GpsLossFallbackSeconds,
            MovementTuningValues.MinimumFallbackSeconds,
            MovementTuningValues.MaximumFallbackSeconds);

        MovementTuningValues updated = new MovementTuningValues(
            gpsReliance,
            stopSensitivity,
            noiseFilter,
            staleSeconds,
            fallbackSeconds);
        if (!AreEqual(current, updated))
        {
            controller.ApplyMovementTuning(updated);
        }

        GUILayout.Space(12f);
        GUILayout.Label(
            $"GPS: {controller.RawGpsSpeedMetersPerSecond:F2} m/s    "
                + $"Estimated: {controller.LastKnownPhysicalSpeed:F2} m/s",
            valueStyle);
        GUILayout.Label(
            $"Accel: {controller.ForwardAccelerationMetersPerSecondSquared:F2} m/s²    "
                + $"Health: {controller.GpsHealth}",
            valueStyle);
        GUILayout.Label(
            $"Stationary lock: {controller.IsStationary}    "
                + $"Confidence: {controller.EstimatorConfidence:F2}",
            valueStyle);
        GUILayout.Space(12f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save settings", GUILayout.Height(52f)))
        {
            controller.SaveMovementTuning();
        }

        if (GUILayout.Button("Reset defaults", GUILayout.Height(52f)))
        {
            controller.ResetMovementTuning();
        }
        GUILayout.EndHorizontal();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private float DrawSlider(
        string label,
        string valueText,
        float value,
        float minimum,
        float maximum)
    {
        GUILayout.Label(label, labelStyle);
        GUILayout.Label(valueText, valueStyle);
        float updated = GUILayout.HorizontalSlider(
            value,
            minimum,
            maximum,
            GUILayout.Height(34f));
        GUILayout.Space(10f);
        return updated;
    }

    private void InitializeStyles()
    {
        if (titleStyle != null)
        {
            return;
        }

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold
        };
        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 21,
            fontStyle = FontStyle.Bold
        };
        hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            wordWrap = true
        };
        valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18
        };
    }

    private static bool AreEqual(
        MovementTuningValues first,
        MovementTuningValues second)
    {
        return Mathf.Approximately(first.GpsReliance, second.GpsReliance)
            && Mathf.Approximately(
                first.SuddenStopSensitivity,
                second.SuddenStopSensitivity)
            && Mathf.Approximately(
                first.AccelerationNoiseFilter,
                second.AccelerationNoiseFilter)
            && Mathf.Approximately(
                first.GpsStaleSeconds,
                second.GpsStaleSeconds)
            && Mathf.Approximately(
                first.GpsLossFallbackSeconds,
                second.GpsLossFallbackSeconds);
    }
}
