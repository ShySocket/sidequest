using TMPro;
using UnityEngine;

public sealed class GpsStatusIndicator : MonoBehaviour
{
    [SerializeField] private VehicleSpeedController vehicleSpeedController;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text speedText;

    private GpsTrackingState displayedState = (GpsTrackingState)(-1);
    private SpeedProviderMode displayedMode = (SpeedProviderMode)(-1);
    private bool displayedHeld;
    private int displayedTenths = int.MinValue;

    private void Awake()
    {
        if (vehicleSpeedController == null || statusText == null || speedText == null)
        {
            Debug.LogError($"{nameof(GpsStatusIndicator)} on {name} has an unassigned reference.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        GpsTrackingState state = vehicleSpeedController.TrackingState;
        SpeedProviderMode mode = vehicleSpeedController.ActiveProviderMode;
        bool held = vehicleSpeedController.IsUsingHeldSpeed;
        int speedTenths = Mathf.RoundToInt(vehicleSpeedController.LastKnownPhysicalSpeed * 10f);

        if (state == displayedState
            && mode == displayedMode
            && held == displayedHeld
            && speedTenths == displayedTenths)
        {
            return;
        }

        displayedState = state;
        displayedMode = mode;
        displayedHeld = held;
        displayedTenths = speedTenths;
        statusText.text = GetStatusText(mode, state);

        float speed = speedTenths * 0.1f;
        if (held)
        {
            speedText.SetText("Holding: {0:0.0} m/s", speed);
        }
        else if (mode == SpeedProviderMode.Mock)
        {
            speedText.SetText("Mock: {0:0.0} m/s", speed);
        }
        else if (state == GpsTrackingState.Tracking)
        {
            speedText.SetText("Live: {0:0.0} m/s", speed);
        }
        else
        {
            speedText.SetText("Speed: {0:0.0} m/s", 0f);
        }
    }

    private static string GetStatusText(SpeedProviderMode mode, GpsTrackingState state)
    {
        if (mode == SpeedProviderMode.Mock && state != GpsTrackingState.SignalLost)
        {
            return "GPS: MOCK";
        }

        switch (state)
        {
            case GpsTrackingState.Initializing:
                return "GPS: INITIALIZING";
            case GpsTrackingState.WaitingForPermission:
                return "GPS: REQUESTING PERMISSION";
            case GpsTrackingState.PermissionDenied:
                return "GPS: PERMISSION DENIED";
            case GpsTrackingState.LocationServicesDisabled:
                return "GPS: LOCATION DISABLED";
            case GpsTrackingState.WaitingForFirstFix:
                return "GPS: WAITING FOR FIRST FIX";
            case GpsTrackingState.Tracking:
                return "GPS: LIVE";
            case GpsTrackingState.SignalLost:
                return "GPS: SIGNAL LOST";
            default:
                return "GPS: INITIALIZING";
        }
    }
}
