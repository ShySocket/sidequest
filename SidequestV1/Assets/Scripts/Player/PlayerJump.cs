using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(GroundCheck))]
public sealed class PlayerJump : MonoBehaviour
{
    [SerializeField]
    private InputActionReference jumpAction;

    [SerializeField]
    private RunnerConfiguration configuration;

    private Rigidbody2D body;
    private GroundCheck groundCheck;
    private InputAction activeJumpAction;
    private InputAction fallbackJumpAction;

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        groundCheck = GetComponent<GroundCheck>();

        activeJumpAction = jumpAction != null ? jumpAction.action : null;
        if (activeJumpAction == null)
        {
            fallbackJumpAction = new InputAction("Jump", InputActionType.Button);
            fallbackJumpAction.expectedControlType = "Button";
            fallbackJumpAction.AddBinding("<Keyboard>/space");
            fallbackJumpAction.AddBinding("<Touchscreen>/primaryTouch/press");
            activeJumpAction = fallbackJumpAction;
        }
    }

    private void OnEnable()
    {
        if (activeJumpAction == null)
        {
            return;
        }

        activeJumpAction.performed += OnJumpPerformed;
        activeJumpAction.Enable();
    }

    private void OnDisable()
    {
        if (activeJumpAction == null)
        {
            return;
        }

        activeJumpAction.performed -= OnJumpPerformed;
        activeJumpAction.Disable();
    }

    private void OnDestroy()
    {
        fallbackJumpAction?.Dispose();
    }

    private void OnJumpPerformed(InputAction.CallbackContext context)
    {
        TryJump();
    }

    public bool TryJump()
    {
        if (!groundCheck.IsGrounded)
        {
            return false;
        }

        Vector2 velocity = body.linearVelocity;
        velocity.y = configuration != null ? configuration.JumpVelocity : 8f;
        body.linearVelocity = velocity;
        return true;
    }
}
