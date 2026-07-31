using System.Text;
using TMPro;
using UnityEngine;

public sealed class DebugSpeedPanel : MonoBehaviour
{
    [SerializeField] private VehicleSpeedController vehicleSpeedController;
    [SerializeField] private UnityGpsSpeedProvider gpsSpeedProvider;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private GroundCheck groundCheck;
    [SerializeField] private Rigidbody2D playerBody;
    [SerializeField] private TMP_Text telemetryText;
    [SerializeField, Range(0.1f, 0.2f)] private float refreshInterval = 0.15f;

    private readonly StringBuilder textBuilder = new StringBuilder(512);
    private float nextRefreshTime;

    private void Awake()
    {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
        gameObject.SetActive(false);
#endif
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime || telemetryText == null)
        {
            return;
        }

        nextRefreshTime = Time.unscaledTime + refreshInterval;
        textBuilder.Clear();
        textBuilder.Append("Provider: ").Append(vehicleSpeedController.ActiveProviderMode).AppendLine();
        textBuilder.Append("Tracking: ").Append(vehicleSpeedController.TrackingState).AppendLine();
        textBuilder.Append("Valid: ").Append(vehicleSpeedController.HasReceivedValidSpeed).AppendLine();
        textBuilder.Append("Available: ").Append(vehicleSpeedController.IsTrackingAvailable).AppendLine();
        textBuilder.Append("Held: ").Append(vehicleSpeedController.IsUsingHeldSpeed).AppendLine();
        textBuilder.Append("GPS health: ").Append(vehicleSpeedController.GpsHealth).AppendLine();
        textBuilder.Append("Dead reckoning: ").Append(vehicleSpeedController.IsDeadReckoning).AppendLine();
        textBuilder.Append("Braking: ").Append(vehicleSpeedController.IsBraking).AppendLine();
        textBuilder.Append("Hard braking: ").Append(vehicleSpeedController.IsHardBraking).AppendLine();
        textBuilder.Append("Stop confirmed: ").Append(vehicleSpeedController.SuddenStopDetected).AppendLine();
        textBuilder.Append("Confidence: ").Append(vehicleSpeedController.EstimatorConfidence.ToString("F2")).AppendLine();
        textBuilder.Append("Forward accel: ")
            .Append(vehicleSpeedController.ForwardAccelerationMetersPerSecondSquared.ToString("F2"))
            .AppendLine();
        textBuilder.Append("GPS raw: ")
            .Append(gpsSpeedProvider.LastKnownSpeedMetersPerSecond.ToString("F2"))
            .AppendLine();
        textBuilder.Append("Estimated: ")
            .Append(vehicleSpeedController.LastKnownPhysicalSpeed.ToString("F2"))
            .AppendLine();
        textBuilder.Append("Delayed: ").Append(vehicleSpeedController.DelayedPhysicalSpeed.ToString("F2")).AppendLine();
        textBuilder.Append("Filtered: ").Append(vehicleSpeedController.FilteredPhysicalSpeed.ToString("F2")).AppendLine();
        textBuilder.Append("Game: ").Append(vehicleSpeedController.GameSpeed.ToString("F2")).AppendLine();
        textBuilder.Append("Buffered: ").Append(vehicleSpeedController.BufferedSampleCount).AppendLine();
        double age = vehicleSpeedController.LastValidLocalSampleTime < 0d
            ? -1d
            : Time.realtimeSinceStartupAsDouble - vehicleSpeedController.LastValidLocalSampleTime;
        textBuilder.Append("Sample age: ").Append(age.ToString("F2")).AppendLine();
        textBuilder.Append("Latitude: ").Append(gpsSpeedProvider.LastLatitude.ToString("F6")).AppendLine();
        textBuilder.Append("Longitude: ").Append(gpsSpeedProvider.LastLongitude.ToString("F6")).AppendLine();
        textBuilder.Append("Accuracy: ").Append(gpsSpeedProvider.LastHorizontalAccuracy.ToString("F1")).AppendLine();
        textBuilder.Append("Game state: ").Append(gameManager.State).AppendLine();
        textBuilder.Append("Grounded: ").Append(groundCheck.IsGrounded).AppendLine();
        textBuilder.Append("Velocity: ")
            .Append(playerBody.linearVelocity.x.ToString("F2"))
            .Append(", ")
            .Append(playerBody.linearVelocity.y.ToString("F2"));
        telemetryText.SetText(textBuilder);
    }
}
