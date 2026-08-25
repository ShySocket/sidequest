using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The rolling sphere. It never travels forward — the world (and the live
/// camera background) does — but it rolls at the vehicle's speed, switches
/// between three lanes, and jumps. Input: swipe/tap on device, arrows or
/// A/D plus space in the editor.
/// </summary>
public sealed class SphereRunnerController : MonoBehaviour
{
    public const float LaneWidth = 1.5f;
    public const float Gravity = -22f;
    public const float JumpVelocity = 7.5f;
    public const float GroundY = 0.5f;

    [SerializeField] private Transform visualSphere;

    private int lane = 1;
    private float verticalVelocity;
    private Vector2 touchStart;
    private bool touchActive;
    private bool touchConsumed;

    public int Lane => lane;
    public float HeightAboveGround => transform.position.y - GroundY;
    public bool IsGrounded => transform.position.y <= GroundY + 0.001f;
    public float SpeedMetersPerSecond { get; set; }
    public bool InputEnabled { get; set; } = true;

    private void Update()
    {
        if (InputEnabled)
        {
            ReadKeyboard();
            ReadTouch();
        }

        // Lane easing
        Vector3 position = transform.position;
        float targetX = (lane - 1) * LaneWidth;
        position.x = Mathf.MoveTowards(position.x, targetX, 10f * Time.deltaTime);

        // Jump physics
        verticalVelocity += Gravity * Time.deltaTime;
        position.y += verticalVelocity * Time.deltaTime;
        if (position.y <= GroundY)
        {
            position.y = GroundY;
            verticalVelocity = 0f;
        }

        transform.position = position;

        if (visualSphere != null)
        {
            visualSphere.Rotate(
                SpeedMetersPerSecond / GroundY * Mathf.Rad2Deg * Time.deltaTime,
                0f,
                0f,
                Space.World);
        }
    }

    public void ResetRun()
    {
        lane = 1;
        verticalVelocity = 0f;
        transform.position = new Vector3(0f, GroundY, 0f);
    }

    private void ReadKeyboard()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame)
        {
            MoveLane(-1);
        }

        if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame)
        {
            MoveLane(1);
        }

        if (keyboard.spaceKey.wasPressedThisFrame || keyboard.upArrowKey.wasPressedThisFrame)
        {
            Jump();
        }
    }

    private void ReadTouch()
    {
        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen == null)
        {
            return;
        }

        var touch = touchscreen.primaryTouch;
        if (touch.press.wasPressedThisFrame)
        {
            touchStart = touch.position.ReadValue();
            touchActive = true;
            touchConsumed = false;
            return;
        }

        if (!touchActive)
        {
            return;
        }

        Vector2 current = touch.position.ReadValue();
        Vector2 delta = current - touchStart;
        float swipeThreshold = Screen.width * 0.06f;
        if (!touchConsumed && Mathf.Abs(delta.x) > swipeThreshold
            && Mathf.Abs(delta.x) > Mathf.Abs(delta.y))
        {
            MoveLane(delta.x > 0f ? 1 : -1);
            touchConsumed = true;
        }

        if (touch.press.wasReleasedThisFrame)
        {
            if (!touchConsumed && delta.magnitude < swipeThreshold)
            {
                Jump();
            }

            touchActive = false;
        }
    }

    public void MoveLane(int direction)
    {
        lane = Mathf.Clamp(lane + direction, 0, 2);
    }

    public void Jump()
    {
        if (IsGrounded)
        {
            verticalVelocity = JumpVelocity;
        }
    }
}
