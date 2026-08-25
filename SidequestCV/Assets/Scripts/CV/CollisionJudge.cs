/// <summary>
/// Deterministic hit test between the rolling sphere and an obstacle window.
/// Kept free of engine types so ride simulations can run headlessly.
/// </summary>
public static class CollisionJudge
{
    public const float PlayerRadius = 0.5f;
    public const float JumpClearHeight = 0.75f;

    public static bool IsHit(
        float obstacleZ,
        float obstacleHalfDepth,
        int obstacleLane,
        bool jumpable,
        int playerLane,
        float playerHeightAboveGround)
    {
        if (obstacleLane != playerLane)
        {
            return false;
        }

        float overlap = obstacleHalfDepth + PlayerRadius;
        if (obstacleZ > overlap || obstacleZ < -overlap)
        {
            return false;
        }

        if (jumpable && playerHeightAboveGround >= JumpClearHeight)
        {
            return false;
        }

        return true;
    }
}
