using UnityEngine;

public sealed class PlayerCollision : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    private bool hasTriggeredGameOver;

    private void Awake()
    {
        if (gameManager == null)
        {
            Debug.LogError($"{nameof(PlayerCollision)} on {name} requires a {nameof(GameManager)}.", this);
            enabled = false;
        }
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (hasTriggeredGameOver)
        {
            return;
        }

        GameObject other = collision.gameObject;
        bool isObstacle = other.layer == LayerMask.NameToLayer("Obstacle")
            || other.CompareTag("Obstacle");
        if (!isObstacle)
        {
            return;
        }

        hasTriggeredGameOver = true;
        gameManager.TriggerGameOver();
    }
}
