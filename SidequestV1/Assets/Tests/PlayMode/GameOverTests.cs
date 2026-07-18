using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class GameOverTests
{
    [UnityTest]
    public IEnumerator GroundIsSafeAndObstacleFreezesTheRun()
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        int obstacleLayer = LayerMask.NameToLayer("Obstacle");
        GameObject systems = new GameObject("Systems");
        systems.SetActive(false);
        VehicleSpeedController controller = PlayModeTestFactory.CreateController(systems, 0f, out _, out _);

        GameObject panel = new GameObject("GameOverPanel");
        panel.transform.SetParent(systems.transform);
        GameObject player = new GameObject("Player");
        player.transform.SetParent(systems.transform);
        player.transform.position = new Vector3(0f, 0.5f, 0f);
        Rigidbody2D body = player.AddComponent<Rigidbody2D>();
        body.constraints = RigidbodyConstraints2D.FreezeRotation;
        player.AddComponent<BoxCollider2D>();
        RunnerMotor motor = player.AddComponent<RunnerMotor>();
        PlayModeTestFactory.SetField(motor, "vehicleSpeedController", controller);
        GameObject point = new GameObject("GroundCheckPoint");
        point.transform.SetParent(player.transform);
        point.transform.localPosition = new Vector3(0f, -0.55f, 0f);
        GroundCheck groundCheck = player.AddComponent<GroundCheck>();
        PlayModeTestFactory.SetField(groundCheck, "groundCheckPoint", point.transform);
        PlayModeTestFactory.SetField(groundCheck, "groundLayer", (LayerMask)(1 << groundLayer));
        PlayerJump jump = player.AddComponent<PlayerJump>();
        PlayModeTestFactory.SetField(jump, "configuration", ScriptableObject.CreateInstance<RunnerConfiguration>());

        GameManager manager = systems.AddComponent<GameManager>();
        PlayModeTestFactory.SetField(manager, "vehicleSpeedController", controller);
        PlayModeTestFactory.SetField(manager, "playerBody", body);
        PlayModeTestFactory.SetField(manager, "runnerMotor", motor);
        PlayModeTestFactory.SetField(manager, "playerJump", jump);
        PlayModeTestFactory.SetField(manager, "gameOverPanel", panel);
        PlayerCollision collision = player.AddComponent<PlayerCollision>();
        PlayModeTestFactory.SetField(collision, "gameManager", manager);

        GameObject ground = new GameObject("Ground");
        ground.layer = groundLayer;
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
        ground.AddComponent<BoxCollider2D>().size = new Vector2(10f, 1f);
        systems.SetActive(true);

        yield return new WaitForSecondsRealtime(0.25f);
        Assert.That(manager.State, Is.Not.EqualTo(GameState.GameOver));

        GameObject obstacle = new GameObject("Obstacle");
        obstacle.layer = obstacleLayer;
        obstacle.transform.position = new Vector3(0.8f, 0.5f, 0f);
        obstacle.AddComponent<BoxCollider2D>();
        Physics2D.SyncTransforms();
        yield return new WaitForFixedUpdate();
        yield return null;

        Assert.That(manager.State, Is.EqualTo(GameState.GameOver));
        Assert.That(motor.enabled, Is.False);
        Assert.That(jump.enabled, Is.False);
        Assert.That(body.simulated, Is.False);
        Assert.That(panel.activeSelf, Is.True);
        Assert.That(typeof(GameManager).GetMethod(nameof(GameManager.RestartGame)), Is.Not.Null);

        Object.Destroy(systems);
        Object.Destroy(ground);
        Object.Destroy(obstacle);
    }
}
