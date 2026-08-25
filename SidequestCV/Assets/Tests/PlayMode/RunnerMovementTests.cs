using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class RunnerMovementTests
{
    [UnityTest]
    public IEnumerator SavedRunnerPrototypeSceneIsVisibleWiredAndMoves()
    {
        SceneManager.LoadScene("RunnerPrototype");
        yield return null;
        yield return new WaitForFixedUpdate();

        GameObject player = GameObject.Find("Player");
        GameObject systems = GameObject.Find("GameSystems");
        GameObject canvasObject = GameObject.Find("Canvas");
        Assert.That(player, Is.Not.Null);
        Assert.That(systems, Is.Not.Null);
        Assert.That(canvasObject, Is.Not.Null);
        Assert.That(player.GetComponent<SpriteRenderer>().sprite, Is.Not.Null);
        Assert.That(player.GetComponent<RunnerMotor>(), Is.Not.Null);
        Assert.That(player.GetComponent<PlayerJump>(), Is.Not.Null);
        Assert.That(player.GetComponent<PlayerCollision>(), Is.Not.Null);

        VehicleSpeedController controller = systems.GetComponentInChildren<VehicleSpeedController>();
        MockSpeedProvider mock = systems.GetComponentInChildren<MockSpeedProvider>();
        GameManager gameManager = systems.GetComponentInChildren<GameManager>();
        Assert.That(controller, Is.Not.Null);
        Assert.That(mock, Is.Not.Null);
        Assert.That(gameManager, Is.Not.Null);
        Assert.That(controller.ActiveProviderMode, Is.EqualTo(SpeedProviderMode.Mock));

        Transform gpsPanel = canvasObject.transform.Find("GpsStatusPanel");
        Transform gameOverPanel = canvasObject.transform.Find("GameOverPanel");
        Transform debugPanel = canvasObject.transform.Find("DebugSpeedPanel");
        Assert.That(gpsPanel, Is.Not.Null);
        Assert.That(((RectTransform)gpsPanel).anchorMin, Is.EqualTo(Vector2.one));
        Assert.That(gameOverPanel, Is.Not.Null);
        Assert.That(gameOverPanel.gameObject.activeSelf, Is.False);
        Assert.That(debugPanel, Is.Not.Null);
        Assert.That(debugPanel.gameObject.activeSelf, Is.False);
        Assert.That(Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length, Is.EqualTo(1));

        Camera camera = Camera.main;
        Assert.That(camera, Is.Not.Null);
        yield return null;
        float startingViewportX = camera.WorldToViewportPoint(player.transform.position).x;
        Assert.That(startingViewportX, Is.InRange(0.1f, 0.5f));

        float startX = player.transform.position.x;
        PlayModeTestFactory.SetField(mock, "speedMetersPerSecond", 10f);
        yield return new WaitForSecondsRealtime(1.5f);
        Assert.That(player.transform.position.x, Is.GreaterThan(startX));
        float movingViewportX = camera.WorldToViewportPoint(player.transform.position).x;
        Assert.That(movingViewportX, Is.EqualTo(startingViewportX).Within(0.02f));
    }

    [UnityTest]
    public IEnumerator MotorUsesControllerSpeedAndPreservesVerticalVelocity()
    {
        GameObject root = new GameObject("MovementTestRoot");
        root.SetActive(false);
        VehicleSpeedController controller = PlayModeTestFactory.CreateController(root, 0f, out MockSpeedProvider mock, out _);

        GameObject player = new GameObject("Player");
        player.transform.SetParent(root.transform);
        Rigidbody2D body = player.AddComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        RunnerMotor motor = player.AddComponent<RunnerMotor>();
        PlayModeTestFactory.SetField(motor, "vehicleSpeedController", controller);
        root.SetActive(true);

        yield return new WaitForFixedUpdate();
        Assert.That(body.linearVelocity.x, Is.EqualTo(0f).Within(0.01f));

        PlayModeTestFactory.SetField(mock, "speedMetersPerSecond", 10f);
        yield return new WaitForSecondsRealtime(1.5f);
        float firstPositiveVelocity = body.linearVelocity.x;
        Assert.That(firstPositiveVelocity, Is.GreaterThan(0f));

        body.linearVelocity = new Vector2(body.linearVelocity.x, 3f);
        yield return new WaitForFixedUpdate();
        Assert.That(body.linearVelocity.y, Is.EqualTo(3f).Within(0.01f));

        PlayModeTestFactory.SetField(mock, "speedMetersPerSecond", 20f);
        yield return new WaitForSecondsRealtime(1.5f);
        Assert.That(body.linearVelocity.x, Is.GreaterThan(firstPositiveVelocity));
        Assert.That(motor.CurrentGameSpeed, Is.EqualTo(controller.GameSpeed).Within(0.01f));

        Object.Destroy(root);
    }
}
