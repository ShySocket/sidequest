using UnityEngine;

public sealed class CameraFollow2D : MonoBehaviour
{
    [SerializeField]
    private Transform target;

    [SerializeField]
    private float horizontalOffset = 4f;

    [SerializeField]
    private float fixedYPosition = 2f;

    [SerializeField, Min(0f)]
    private float smoothTime;

    [SerializeField, Range(0f, 0.9f)]
    private float maximumViewportOffset = 0.65f;

    private Camera attachedCamera;
    private float zPosition;
    private float xVelocity;
    private float previousTargetX;

    private void Awake()
    {
        attachedCamera = GetComponent<Camera>();
        zPosition = transform.position.z;

        if (target == null)
        {
            Debug.LogError($"{nameof(CameraFollow2D)} on {name} requires a target transform.", this);
            enabled = false;
        }
    }

    private void Start()
    {
        if (enabled)
        {
            SnapToTarget();
        }
    }

    private void LateUpdate()
    {
        float targetDeltaX = target.position.x - previousTargetX;
        previousTargetX = target.position.x;
        transform.position += Vector3.right * targetDeltaX;

        float targetX = CalculateTargetX();
        float nextX = smoothTime <= Mathf.Epsilon
            ? targetX
            : Mathf.SmoothDamp(
                transform.position.x,
                targetX,
                ref xVelocity,
                smoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime);

        transform.position = new Vector3(nextX, fixedYPosition, zPosition);
    }

    private void SnapToTarget()
    {
        xVelocity = 0f;
        previousTargetX = target.position.x;
        transform.position = new Vector3(CalculateTargetX(), fixedYPosition, zPosition);
    }

    private float CalculateTargetX()
    {
        float offset = horizontalOffset;
        if (attachedCamera != null && attachedCamera.orthographic)
        {
            float halfViewWidth = attachedCamera.orthographicSize * attachedCamera.aspect;
            float maximumOffset = halfViewWidth * maximumViewportOffset;
            offset = Mathf.Clamp(offset, -maximumOffset, maximumOffset);
        }

        return target.position.x + offset;
    }
}
