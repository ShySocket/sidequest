using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Ties the whole CV runner together: vehicle speed drives the world, the
/// detection and segmentation runners feed the obstacle director, and the
/// HUD reports what the pipeline is doing. When detection is unavailable the
/// game degrades to a procedural spawner instead of dying.
/// </summary>
public sealed class CvGameDirector : MonoBehaviour
{
    [SerializeField] private VehicleSpeedController speedController;
    [SerializeField] private DetectionRunner detectionRunner;
    [SerializeField] private SegmentationRunner segmentationRunner;
    [SerializeField] private ObstacleField obstacleField;
    [SerializeField] private SphereRunnerController player;
    [SerializeField] private ProceduralStreetTexture proceduralStreet;
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text hintText;
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField, Range(1, 9)] private int lives = 3;

    private readonly List<DetectionEvent> pendingEvents = new List<DetectionEvent>();
    private ObstacleDirector director;
    private float distanceMeters;
    private int dodges;
    private int hits;
    private bool gameOver;
    private double noSurfaceSince = -1d;
    private float proceduralSpawnTimer;
    private float stoppedDuration;
    private bool stopResolvedScene;

    public float GameSpeed { get; private set; }
    public int Score => Mathf.RoundToInt(distanceMeters + dodges * 10f);

    private void Start()
    {
        director = new ObstacleDirector(new ObstacleDirectorSettings());
        if (detectionRunner != null)
        {
            detectionRunner.DetectionsReady += OnDetections;
        }

        if (obstacleField != null)
        {
            obstacleField.PlayerHit += OnPlayerHit;
            obstacleField.ObstacleDodged += _ => dodges++;
        }

        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (detectionRunner != null)
        {
            detectionRunner.DetectionsReady -= OnDetections;
        }
    }

    private void OnDetections(IReadOnlyList<DetectionEvent> events)
    {
        pendingEvents.AddRange(events);
    }

    private void Update()
    {
        GameSpeed = speedController != null ? speedController.GameSpeed : 0f;
        if (player != null)
        {
            player.SpeedMetersPerSecond = GameSpeed;
            player.InputEnabled = !gameOver;
        }

        if (proceduralStreet != null)
        {
            proceduralStreet.ScrollSpeed = GameSpeed;
        }

        if (gameOver)
        {
            UpdateHud();
            return;
        }

        SurfaceReport surface = segmentationRunner != null
            ? segmentationRunner.LastReport
            : SurfaceReport.AllRoad(3);

        double now = Time.unscaledTimeAsDouble;
        bool cvActive = detectionRunner != null && detectionRunner.IsAvailable;
        if (cvActive)
        {
            List<SpawnCommand> spawns = director.Step(now, GameSpeed, surface, pendingEvents);
            pendingEvents.Clear();
            for (int i = 0; i < spawns.Count; i++)
            {
                obstacleField.Spawn(spawns[i]);
            }
        }
        else
        {
            SpawnProcedurally(now, surface);
        }

        // A real stop freezes the scene with obstacles potentially inches
        // from the sphere; the street the camera sees after pulling away is
        // a new one anyway. Resolve the frozen scene instead of replaying a
        // compressed, undodgeable wall on restart.
        if (GameSpeed < 1.5f)
        {
            stoppedDuration += Time.deltaTime;
            if (stoppedDuration > 1.5f && !stopResolvedScene)
            {
                stopResolvedScene = true;
                obstacleField.Clear(countAsDodged: true);
            }
        }
        else
        {
            stoppedDuration = 0f;
            stopResolvedScene = false;
        }

        obstacleField.Advance(GameSpeed, Time.deltaTime);
        distanceMeters += GameSpeed * Time.deltaTime;

        TrackSurfaceHint(surface, now);
        UpdateHud();
    }

    /// <summary>
    /// Keeps the game playable when no detection model is present: a light
    /// procedural stream through the same fairness director.
    /// </summary>
    private void SpawnProcedurally(double now, SurfaceReport surface)
    {
        proceduralSpawnTimer -= Time.deltaTime;
        if (proceduralSpawnTimer > 0f)
        {
            return;
        }

        proceduralSpawnTimer = Random.Range(1.2f, 2.4f);
        ObstacleKind[] kinds =
        {
            ObstacleKind.Vehicle, ObstacleKind.Hydrant, ObstacleKind.Bench,
            ObstacleKind.Bush, ObstacleKind.Sign, ObstacleKind.Person
        };
        DetectionEvent synthetic = new DetectionEvent(
            Random.Range(0, 3),
            kinds[Random.Range(0, kinds.Length)],
            1f);
        pendingEvents.Add(synthetic);
        List<SpawnCommand> spawns = director.Step(now, GameSpeed, surface, pendingEvents);
        pendingEvents.Clear();
        for (int i = 0; i < spawns.Count; i++)
        {
            obstacleField.Spawn(spawns[i]);
        }
    }

    private void TrackSurfaceHint(SurfaceReport surface, double now)
    {
        bool moving = GameSpeed > 1.5f;
        if (moving && (surface == null || !surface.HasAnySurface))
        {
            if (noSurfaceSince < 0d)
            {
                noSurfaceSince = now;
            }
        }
        else
        {
            noSurfaceSince = -1d;
        }
    }

    private void OnPlayerHit(ObstacleKind kind)
    {
        hits++;
        if (hits >= lives)
        {
            gameOver = true;
            if (gameOverPanel != null)
            {
                gameOverPanel.SetActive(true);
            }
        }
    }

    public void RestartGame()
    {
        hits = 0;
        dodges = 0;
        distanceMeters = 0f;
        gameOver = false;
        pendingEvents.Clear();
        director.Reset();
        obstacleField.Clear();
        player.ResetRun();
        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(false);
        }
    }

    private void UpdateHud()
    {
        if (scoreText != null)
        {
            scoreText.text = $"Score {Score}   ♥ {Mathf.Max(0, lives - hits)}";
        }

        if (statusText != null)
        {
            string cv = detectionRunner != null && detectionRunner.IsAvailable
                ? "CV on"
                : "CV off";
            string seg = segmentationRunner != null && segmentationRunner.IsAvailable
                ? segmentationRunner.LastReport.DominantSurface.ToString()
                : "assumed road";
            statusText.text = $"{GameSpeed * 3.6f:0} km/h · {cv} · {seg}";
        }

        if (hintText != null)
        {
            if (gameOver)
            {
                hintText.text = "Run over — tap Restart";
            }
            else if (GameSpeed <= 0.5f)
            {
                hintText.text = speedController != null && speedController.HasReceivedValidSpeed
                    ? "Waiting for the vehicle to move…"
                    : "Waiting for GPS…";
            }
            else if (noSurfaceSince > 0d
                && Time.unscaledTimeAsDouble - noSurfaceSince > 3d)
            {
                hintText.text = "Point the camera at the road ahead";
            }
            else
            {
                hintText.text = string.Empty;
            }
        }
    }
}
