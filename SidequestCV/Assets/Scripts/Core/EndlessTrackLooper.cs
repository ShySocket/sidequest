using System.Collections.Generic;
using UnityEngine;

public sealed class EndlessTrackLooper : MonoBehaviour
{
    [SerializeField] private Transform player;
    [SerializeField] private Transform ground;
    [SerializeField] private Transform obstaclesRoot;
    [SerializeField, Min(1f)] private float repeatLength = 50f;
    [SerializeField, Min(0f)] private float recycleBehindDistance = 10f;

    private readonly List<Transform> obstacles = new List<Transform>();

    private void Awake()
    {
        if (player == null || ground == null || obstaclesRoot == null)
        {
            Debug.LogError(
                $"{nameof(EndlessTrackLooper)} on {name} requires player, ground, and obstacles references.",
                this);
            enabled = false;
            return;
        }

        for (int i = 0; i < obstaclesRoot.childCount; i++)
        {
            obstacles.Add(obstaclesRoot.GetChild(i));
        }
    }

    private void FixedUpdate()
    {
        float playerX = player.position.x;

        while (playerX - ground.position.x > repeatLength * 0.5f)
        {
            ground.position += Vector3.right * repeatLength;
        }

        float recycleBoundary = playerX - recycleBehindDistance;
        for (int i = 0; i < obstacles.Count; i++)
        {
            Transform obstacle = obstacles[i];
            while (obstacle.position.x < recycleBoundary)
            {
                obstacle.position += Vector3.right * repeatLength;
            }
        }
    }
}
