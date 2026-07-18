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
    private float smoothTime = 0.15f;

    private float zPosition;
    private float xVelocity;

    private void Awake()
    {
        zPosition = transform.position.z;

        if (target == null)
        {
            Debug.LogError($"{nameof(CameraFollow2D)} on {name} requires a target transform.", this);
            enabled = false;
        }
    }

    private void LateUpdate()
    {
        float targetX = target.position.x + horizontalOffset;
        float nextX = Mathf.SmoothDamp(
            transform.position.x,
            targetX,
            ref xVelocity,
            smoothTime);

        transform.position = new Vector3(nextX, fixedYPosition, zPosition);
    }
}
