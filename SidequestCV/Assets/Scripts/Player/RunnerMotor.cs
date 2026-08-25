using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public sealed class RunnerMotor : MonoBehaviour
{
    [SerializeField]
    private VehicleSpeedController vehicleSpeedController;

    private Rigidbody2D body;

    public float CurrentGameSpeed { get; private set; }

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();

        if (vehicleSpeedController == null)
        {
            Debug.LogError(
                $"{nameof(RunnerMotor)} on {name} requires a {nameof(VehicleSpeedController)}.",
                this);
            enabled = false;
        }
    }

    private void FixedUpdate()
    {
        CurrentGameSpeed = vehicleSpeedController.GameSpeed;
        Vector2 velocity = body.linearVelocity;
        velocity.x = CurrentGameSpeed;
        body.linearVelocity = velocity;
    }
}
