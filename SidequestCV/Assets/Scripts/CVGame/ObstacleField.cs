using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns live obstacles: applies spawn commands, advances everything toward
/// the player at the vehicle speed, detects hits and dodges.
/// </summary>
public sealed class ObstacleField : MonoBehaviour
{
    [SerializeField] private SphereRunnerController player;

    private readonly List<ActiveObstacle> active = new List<ActiveObstacle>();
    private readonly Stack<GameObject>[] pools = CreatePools();

    public int ActiveCount => active.Count;
    public event Action<ObstacleKind> PlayerHit;
    public event Action<ObstacleKind> ObstacleDodged;

    public void Spawn(SpawnCommand command)
    {
        GameObject obstacle = Acquire(command.Kind);
        float laneX = (command.Lane - 1) * SphereRunnerController.LaneWidth;
        obstacle.transform.position = new Vector3(laneX, 0f, command.Distance);
        obstacle.SetActive(true);
        active.Add(new ActiveObstacle(command, obstacle));
    }

    public void Advance(float speedMetersPerSecond, float deltaTime)
    {
        float step = speedMetersPerSecond * deltaTime;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            ActiveObstacle entry = active[i];
            Vector3 position = entry.Instance.transform.position;
            position.z -= step;
            entry.Instance.transform.position = position;

            if (player != null && CollisionJudge.IsHit(
                position.z,
                entry.HalfDepth,
                entry.Command.Lane,
                entry.Command.Jumpable,
                player.Lane,
                player.HeightAboveGround))
            {
                Release(entry);
                active.RemoveAt(i);
                PlayerHit?.Invoke(entry.Command.Kind);
                continue;
            }

            if (position.z < -4f)
            {
                Release(entry);
                active.RemoveAt(i);
                ObstacleDodged?.Invoke(entry.Command.Kind);
            }
        }
    }

    public void Clear(bool countAsDodged = false)
    {
        for (int i = 0; i < active.Count; i++)
        {
            Release(active[i]);
            if (countAsDodged)
            {
                ObstacleDodged?.Invoke(active[i].Command.Kind);
            }
        }

        active.Clear();
    }

    private GameObject Acquire(ObstacleKind kind)
    {
        Stack<GameObject> pool = pools[(int)kind];
        if (pool.Count > 0)
        {
            return pool.Pop();
        }

        GameObject built = ObstacleFactory.Build(kind);
        built.transform.SetParent(transform, false);
        return built;
    }

    private void Release(ActiveObstacle entry)
    {
        entry.Instance.SetActive(false);
        pools[(int)entry.Command.Kind].Push(entry.Instance);
    }

    private static Stack<GameObject>[] CreatePools()
    {
        int kinds = Enum.GetValues(typeof(ObstacleKind)).Length;
        Stack<GameObject>[] pools = new Stack<GameObject>[kinds];
        for (int i = 0; i < kinds; i++)
        {
            pools[i] = new Stack<GameObject>();
        }

        return pools;
    }

    private readonly struct ActiveObstacle
    {
        public ActiveObstacle(SpawnCommand command, GameObject instance)
        {
            Command = command;
            Instance = instance;
            HalfDepth = ObstacleFactory.HalfDepth(command.Kind);
        }

        public SpawnCommand Command { get; }
        public GameObject Instance { get; }
        public float HalfDepth { get; }
    }
}

/// <summary>Primitive-based obstacle visuals; no external art needed.</summary>
public static class ObstacleFactory
{
    public static float HalfDepth(ObstacleKind kind)
    {
        switch (kind)
        {
            case ObstacleKind.Vehicle:
                return 1.7f;
            case ObstacleKind.Bench:
                return 0.35f;
            default:
                return 0.4f;
        }
    }

    public static GameObject Build(ObstacleKind kind)
    {
        GameObject root = new GameObject($"Obstacle_{kind}");
        switch (kind)
        {
            case ObstacleKind.Vehicle:
                AddBox(root, new Vector3(0f, 0.7f, 0f), new Vector3(1.5f, 1.4f, 3.4f), new Color(0.75f, 0.16f, 0.14f));
                AddBox(root, new Vector3(0f, 1.5f, -0.2f), new Vector3(1.3f, 0.55f, 1.8f), new Color(0.6f, 0.13f, 0.12f));
                break;
            case ObstacleKind.Person:
                AddCapsule(root, new Vector3(0f, 0.9f, 0f), new Color(0.2f, 0.35f, 0.7f));
                break;
            case ObstacleKind.Sign:
                AddCylinder(root, new Vector3(0f, 0.55f, 0f), new Vector3(0.08f, 0.55f, 0.08f), new Color(0.5f, 0.5f, 0.55f));
                AddBox(root, new Vector3(0f, 1.25f, 0f), new Vector3(0.7f, 0.7f, 0.08f), new Color(0.8f, 0.12f, 0.12f));
                break;
            case ObstacleKind.TrafficLight:
                AddCylinder(root, new Vector3(0f, 1f, 0f), new Vector3(0.09f, 1f, 0.09f), new Color(0.25f, 0.25f, 0.28f));
                AddBox(root, new Vector3(0f, 2.15f, 0f), new Vector3(0.4f, 0.9f, 0.35f), new Color(0.15f, 0.15f, 0.18f));
                break;
            case ObstacleKind.Hydrant:
                AddCylinder(root, new Vector3(0f, 0.35f, 0f), new Vector3(0.22f, 0.35f, 0.22f), new Color(0.85f, 0.25f, 0.1f));
                break;
            case ObstacleKind.Bench:
                AddBox(root, new Vector3(0f, 0.35f, 0f), new Vector3(1.4f, 0.12f, 0.5f), new Color(0.45f, 0.3f, 0.16f));
                AddBox(root, new Vector3(0f, 0.18f, 0f), new Vector3(1.2f, 0.35f, 0.4f), new Color(0.35f, 0.24f, 0.13f));
                break;
            case ObstacleKind.Bush:
                AddSphere(root, new Vector3(-0.25f, 0.35f, 0f), 0.75f, new Color(0.16f, 0.42f, 0.16f));
                AddSphere(root, new Vector3(0.3f, 0.3f, 0.1f), 0.6f, new Color(0.2f, 0.5f, 0.18f));
                break;
            default:
                AddBox(root, new Vector3(0f, 0.5f, 0f), Vector3.one, new Color(0.5f, 0.5f, 0.5f));
                break;
        }

        return root;
    }

    private static void AddBox(GameObject parent, Vector3 position, Vector3 size, Color color)
    {
        GameObject piece = Shape(PrimitiveType.Cube, parent, position, color);
        piece.transform.localScale = size;
    }

    private static void AddCapsule(GameObject parent, Vector3 position, Color color)
    {
        Shape(PrimitiveType.Capsule, parent, position, color);
    }

    private static void AddCylinder(GameObject parent, Vector3 position, Vector3 scale, Color color)
    {
        GameObject piece = Shape(PrimitiveType.Cylinder, parent, position, color);
        piece.transform.localScale = scale;
    }

    private static void AddSphere(GameObject parent, Vector3 position, float diameter, Color color)
    {
        GameObject piece = Shape(PrimitiveType.Sphere, parent, position, color);
        piece.transform.localScale = Vector3.one * diameter;
    }

    private static GameObject Shape(PrimitiveType type, GameObject parent, Vector3 position, Color color)
    {
        GameObject piece = GameObject.CreatePrimitive(type);
        UnityEngine.Object.Destroy(piece.GetComponent<Collider>());
        piece.transform.SetParent(parent.transform, false);
        piece.transform.localPosition = position;
        Renderer renderer = piece.GetComponent<Renderer>();
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        Material material = lit != null ? new Material(lit) : renderer.material;
        material.color = color;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return piece;
    }
}
