using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class PlayerJumpTests
{
    [UnityTest]
    public IEnumerator GroundedJumpPreservesXRejectsAirborneAndAllowsLanding()
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        GameObject ground = new GameObject("Ground");
        ground.layer = groundLayer;
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
        BoxCollider2D groundCollider = ground.AddComponent<BoxCollider2D>();
        groundCollider.size = new Vector2(10f, 1f);

        GameObject player = new GameObject("Player");
        player.SetActive(false);
        player.transform.position = new Vector3(0f, 0.5f, 0f);
        Rigidbody2D body = player.AddComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        player.AddComponent<BoxCollider2D>();
        GameObject point = new GameObject("GroundCheckPoint");
        point.transform.SetParent(player.transform);
        point.transform.localPosition = new Vector3(0f, -0.55f, 0f);
        GroundCheck groundCheck = player.AddComponent<GroundCheck>();
        PlayModeTestFactory.SetField(groundCheck, "groundCheckPoint", point.transform);
        PlayModeTestFactory.SetField(groundCheck, "groundLayer", (LayerMask)(1 << groundLayer));
        PlayerJump jump = player.AddComponent<PlayerJump>();
        PlayModeTestFactory.SetField(jump, "configuration", ScriptableObject.CreateInstance<RunnerConfiguration>());
        player.SetActive(true);

        yield return new WaitForFixedUpdate();
        body.linearVelocity = new Vector2(4f, 0f);
        Assert.That(jump.TryJump(), Is.True);
        Assert.That(body.linearVelocity.x, Is.EqualTo(4f).Within(0.01f));
        Assert.That(body.linearVelocity.y, Is.GreaterThan(0f));

        player.transform.position = new Vector3(0f, 3f, 0f);
        Physics2D.SyncTransforms();
        yield return new WaitForFixedUpdate();
        Assert.That(jump.TryJump(), Is.False);

        player.transform.position = new Vector3(0f, 0.5f, 0f);
        body.linearVelocity = Vector2.zero;
        Physics2D.SyncTransforms();
        yield return new WaitForFixedUpdate();
        Assert.That(jump.TryJump(), Is.True);

        Object.Destroy(player);
        Object.Destroy(ground);
    }
}
