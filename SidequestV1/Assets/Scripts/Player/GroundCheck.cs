using UnityEngine;

public sealed class GroundCheck : MonoBehaviour
{
    [SerializeField]
    private Transform groundCheckPoint;

    [SerializeField]
    private LayerMask groundLayer;

    [SerializeField, Min(0.01f)]
    private float checkRadius = 0.12f;

    public bool IsGrounded { get; private set; }

    private void Awake()
    {
        if (groundCheckPoint == null)
        {
            Debug.LogError($"{nameof(GroundCheck)} on {name} requires a ground check point.", this);
            enabled = false;
        }
    }

    private void FixedUpdate()
    {
        IsGrounded = Physics2D.OverlapCircle(
            groundCheckPoint.position,
            checkRadius,
            groundLayer) != null;
    }

    private void OnDrawGizmosSelected()
    {
        if (groundCheckPoint == null)
        {
            return;
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(groundCheckPoint.position, checkRadius);
    }
}
