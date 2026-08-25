using System;
using System.Collections.Generic;

/// <summary>A detection already reduced to gameplay terms.</summary>
public readonly struct DetectionEvent
{
    public DetectionEvent(int lane, ObstacleKind kind, float score)
    {
        Lane = lane;
        Kind = kind;
        Score = score;
    }

    public int Lane { get; }
    public ObstacleKind Kind { get; }
    public float Score { get; }
}

public readonly struct SpawnCommand
{
    public SpawnCommand(double time, int lane, ObstacleKind kind, float distance, bool jumpable, double arrivalTime)
    {
        Time = time;
        Lane = lane;
        Kind = kind;
        Distance = distance;
        Jumpable = jumpable;
        ArrivalTime = arrivalTime;
    }

    public double Time { get; }
    public int Lane { get; }
    public ObstacleKind Kind { get; }
    public float Distance { get; }
    public bool Jumpable { get; }
    public double ArrivalTime { get; }
}

[Serializable]
public sealed class ObstacleDirectorSettings
{
    public int LaneCount = 3;
    public float MinSpeedMetersPerSecond = 1.5f;
    public float MinScore = 0.45f;
    public float MinReactionSeconds = 1.1f;
    public float SpawnLeadFactor = 1.6f;
    public float MinSpawnDistanceMeters = 8f;
    public float MaxSpawnDistanceMeters = 32f;
    // Must exceed the jump air time (~0.68 s) plus the jump trigger band,
    // or two low obstacles in one lane cannot both be cleared.
    public float SameLaneGapSeconds = 1f;
    public float KindCooldownSeconds = 1.25f;
    public int MaxScheduledPerLane = 2;
    public float DodgeWindowSeconds = 0.45f;
    public float MaxSpawnsPerSecond = 1.5f;

    /// <summary>
    /// Below this speed the sphere cannot stay above a low obstacle for its
    /// whole (long) collision window — the window outlasts the jump air
    /// time, especially while decelerating — so "jumpable" obstacles must
    /// count as lane blockers for fairness purposes.
    /// </summary>
    public float JumpClearMinSpeedMetersPerSecond = 6f;
}

/// <summary>
/// Turns raw detection events into fair spawn commands. Every rule exists to
/// keep the game honest against a noisy, real-world detector:
/// - no spawns while the vehicle is effectively stopped or no surface is seen,
/// - per-lane/kind cooldowns absorb the same physical object being detected
///   frame after frame,
/// - spawn distance guarantees a minimum reaction time at the current speed,
///   and impossible spawns (too fast to react) are dropped instead of clamped,
/// - a non-jumpable obstacle is only scheduled if at least one runnable lane
///   stays passable around its arrival moment.
/// </summary>
public sealed class ObstacleDirector
{
    private readonly ObstacleDirectorSettings settings;
    private readonly Dictionary<int, double> kindCooldowns = new Dictionary<int, double>();
    private readonly List<ScheduledArrival>[] laneSchedules;
    private readonly Queue<double> recentSpawnTimes = new Queue<double>();

    public ObstacleDirector(ObstacleDirectorSettings settings)
    {
        this.settings = settings;
        laneSchedules = new List<ScheduledArrival>[settings.LaneCount];
        for (int i = 0; i < laneSchedules.Length; i++)
        {
            laneSchedules[i] = new List<ScheduledArrival>();
        }
    }

    public List<SpawnCommand> Step(
        double time,
        float speedMetersPerSecond,
        SurfaceReport surface,
        IReadOnlyList<DetectionEvent> detections)
    {
        List<SpawnCommand> spawns = new List<SpawnCommand>();
        PruneSchedules(time);

        if (surface == null
            || !surface.HasAnySurface
            || speedMetersPerSecond < settings.MinSpeedMetersPerSecond
            || detections == null)
        {
            return spawns;
        }

        for (int i = 0; i < detections.Count; i++)
        {
            DetectionEvent detection = detections[i];
            if (detection.Score < settings.MinScore
                || detection.Lane < 0
                || detection.Lane >= settings.LaneCount)
            {
                continue;
            }

            SurfaceType laneSurface = surface.LaneSurfaces[detection.Lane];
            if (laneSurface == SurfaceType.None)
            {
                continue;
            }

            if (detection.Kind == ObstacleKind.Vehicle
                && laneSurface == SurfaceType.Sidewalk)
            {
                // Parked or passing cars seen over a sidewalk run are scenery,
                // not something barreling down the player's path.
                continue;
            }

            int cooldownKey = detection.Lane * 64 + (int)detection.Kind;
            if (kindCooldowns.TryGetValue(cooldownKey, out double lastSpawn)
                && time - lastSpawn < settings.KindCooldownSeconds)
            {
                continue;
            }

            if (CountRecentSpawns(time) >= settings.MaxSpawnsPerSecond)
            {
                continue;
            }

            float distance = Clamp(
                speedMetersPerSecond * settings.MinReactionSeconds * settings.SpawnLeadFactor,
                settings.MinSpawnDistanceMeters,
                settings.MaxSpawnDistanceMeters);
            double arrivalSeconds = distance / speedMetersPerSecond;
            if (arrivalSeconds < settings.MinReactionSeconds)
            {
                // The vehicle is moving so fast that even the farthest spawn
                // point would be unfair. Skip rather than surprise the player.
                continue;
            }

            double arrivalTime = time + arrivalSeconds;
            if (!LaneAcceptsArrival(detection.Lane, arrivalTime))
            {
                continue;
            }

            bool jumpable = CvClassCatalog.IsJumpable(detection.Kind);
            bool clearableInLane = jumpable
                && speedMetersPerSecond >= settings.JumpClearMinSpeedMetersPerSecond;
            if (!clearableInLane && WouldBlockAllLanes(
                surface, detection.Lane, arrivalTime, speedMetersPerSecond))
            {
                continue;
            }

            // Bookkeep every spawn as a blocker: a jumpable scheduled at
            // cruise speed can become physically unjumpable if the vehicle
            // brakes before it arrives, so the escape-lane guarantee must
            // never lean on jumps actually being possible.
            laneSchedules[detection.Lane].Add(new ScheduledArrival(arrivalTime, false));
            kindCooldowns[cooldownKey] = time;
            recentSpawnTimes.Enqueue(time);
            spawns.Add(new SpawnCommand(
                time,
                detection.Lane,
                detection.Kind,
                distance,
                jumpable,
                arrivalTime));
        }

        return spawns;
    }

    public void Reset()
    {
        kindCooldowns.Clear();
        recentSpawnTimes.Clear();
        for (int i = 0; i < laneSchedules.Length; i++)
        {
            laneSchedules[i].Clear();
        }
    }

    private bool LaneAcceptsArrival(int lane, double arrivalTime)
    {
        List<ScheduledArrival> schedule = laneSchedules[lane];
        int scheduled = 0;
        for (int i = 0; i < schedule.Count; i++)
        {
            scheduled++;
            if (Math.Abs(schedule[i].Time - arrivalTime) < settings.SameLaneGapSeconds)
            {
                return false;
            }
        }

        return scheduled < settings.MaxScheduledPerLane;
    }

    private bool WouldBlockAllLanes(
        SurfaceReport surface,
        int candidateLane,
        double arrivalTime,
        float speedMetersPerSecond)
    {
        // A vehicle's physical collision window is ~4.4 m of overlap divided
        // by travel speed, so at low speed obstacles occupy their lane for
        // much longer than the fixed dodge window. Scale the separation
        // requirement accordingly to keep an honest escape lane open.
        float blockingWindow = settings.DodgeWindowSeconds * 2f
            + 4.4f / Math.Max(1f, speedMetersPerSecond);
        int runnableLanes = 0;
        int blockedLanes = 0;
        for (int lane = 0; lane < settings.LaneCount; lane++)
        {
            if (surface.LaneSurfaces[lane] == SurfaceType.None)
            {
                continue;
            }

            runnableLanes++;
            bool blocked = lane == candidateLane;
            if (!blocked)
            {
                List<ScheduledArrival> schedule = laneSchedules[lane];
                for (int i = 0; i < schedule.Count; i++)
                {
                    if (!schedule[i].Jumpable
                        && Math.Abs(schedule[i].Time - arrivalTime) < blockingWindow)
                    {
                        blocked = true;
                        break;
                    }
                }
            }

            if (blocked)
            {
                blockedLanes++;
            }
        }

        return runnableLanes > 0 && blockedLanes >= runnableLanes;
    }

    private void PruneSchedules(double time)
    {
        for (int lane = 0; lane < laneSchedules.Length; lane++)
        {
            laneSchedules[lane].RemoveAll(a => a.Time < time - 2d);
        }

        while (recentSpawnTimes.Count > 0 && recentSpawnTimes.Peek() < time - 1d)
        {
            recentSpawnTimes.Dequeue();
        }
    }

    private int CountRecentSpawns(double time)
    {
        int count = 0;
        foreach (double spawnTime in recentSpawnTimes)
        {
            if (spawnTime >= time - 1d)
            {
                count++;
            }
        }

        return count;
    }

    private static float Clamp(float value, float min, float max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    private readonly struct ScheduledArrival
    {
        public ScheduledArrival(double time, bool jumpable)
        {
            Time = time;
            Jumpable = jumpable;
        }

        public double Time { get; }
        public bool Jumpable { get; }
    }
}
