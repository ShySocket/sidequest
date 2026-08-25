using System;
using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// Headless end-to-end simulation of the CV runner: a scripted commute
/// (accelerate, cruise, stop, repeat) with a noisy synthetic detector stream
/// pushed through the real ObstacleDirector, real collision rules, and the
/// real movement constants of the sphere. A competent player must survive
/// the whole ride; a player who never reacts must eventually be hit.
/// </summary>
public sealed class CvRideSimulationTests
{
    private const float TickSeconds = 0.02f;
    private const float RideSeconds = 180f;
    private const float LaneWidth = 1.5f;   // SphereRunnerController.LaneWidth
    private const float LaneChangeSpeed = 10f;
    private const float JumpVelocity = 7.5f;
    private const float Gravity = -22f;
    private const float PlayerHalfWidth = 0.75f;
    private const double NoSurfaceStart = 120d;
    private const double NoSurfaceEnd = 130d;
    private static double DebugWindowStart = -1d;
    private static double DebugWindowEnd = -1d;
    private static int debugTickCounter;
    private static double currentSimTime;

    private sealed class SimObstacle
    {
        public SpawnCommand Command;
        public float Z;
        public float HalfDepth;
    }

    private sealed class RideResult
    {
        public int Hits;
        public int Dodged;
        public List<SpawnCommand> Spawns = new List<SpawnCommand>();
        public List<double> SpawnTimes = new List<double>();
        public List<float> SpawnSpeeds = new List<float>();
        public bool ObstacleMovedWhileStopped;
    }

    [Test]
    public void PerfectPlayerSurvivesTheWholeCommute()
    {
        foreach (int seed in new[] { 12345, 777, 99, 424242, 2026 })
        {
            RideResult result = RunRide(perfectPlayer: true, seed: seed);
            if (result.Hits > 0)
            {
                for (int i = 0; i < result.Spawns.Count; i++)
                {
                    SpawnCommand s = result.Spawns[i];
                    TestContext.WriteLine(
                        $"SPAWN t={s.Time:F2} lane={s.Lane} kind={s.Kind} "
                        + $"dist={s.Distance:F1} arrival={s.ArrivalTime:F2} jumpable={s.Jumpable}");
                }
            }

            Assert.That(result.Spawns.Count, Is.GreaterThan(20),
                $"seed {seed}: the ride should be busy enough to be a real test");
            Assert.That(result.Hits, Is.Zero,
                $"seed {seed}: a player with full information and honest reactions must survive");
            Assert.That(result.Dodged, Is.GreaterThan(15), $"seed {seed}");
        }
    }

    [Test]
    public void SleepingPlayerIsEventuallyHit()
    {
        RideResult result = RunRide(perfectPlayer: false, seed: 12345);

        Assert.That(result.Hits, Is.GreaterThan(0),
            "obstacles must be real threats, not decoration");
    }

    [Test]
    public void NothingSpawnsWhileStoppedOrWithoutSurface()
    {
        RideResult result = RunRide(perfectPlayer: true, seed: 777);

        for (int i = 0; i < result.SpawnTimes.Count; i++)
        {
            Assert.That(result.SpawnSpeeds[i], Is.GreaterThanOrEqualTo(1.5f),
                $"spawn at t={result.SpawnTimes[i]:F2} while nearly stopped");
            bool inBlindWindow = result.SpawnTimes[i] >= NoSurfaceStart
                && result.SpawnTimes[i] < NoSurfaceEnd;
            Assert.That(inBlindWindow, Is.False,
                $"spawn at t={result.SpawnTimes[i]:F2} during the no-surface window");
        }
    }

    [Test]
    public void WorldFreezesWhileTheVehicleIsStopped()
    {
        RideResult result = RunRide(perfectPlayer: true, seed: 424242);

        Assert.That(result.ObstacleMovedWhileStopped, Is.False);
    }

    [Test]
    public void EverySpawnHonorsTheReactionContract()
    {
        RideResult result = RunRide(perfectPlayer: true, seed: 99);
        ObstacleDirectorSettings settings = new ObstacleDirectorSettings();

        for (int i = 0; i < result.Spawns.Count; i++)
        {
            float arrival = result.Spawns[i].Distance / result.SpawnSpeeds[i];
            Assert.That(arrival, Is.GreaterThanOrEqualTo(settings.MinReactionSeconds - 1e-3f));
        }
    }

    private static float SpeedAt(double t)
    {
        if (t < 8d)
        {
            return (float)t * 1.5f;
        }

        if (t < 70d)
        {
            return 12f + 2f * (float)Math.Sin(t / 5d);
        }

        if (t < 78d)
        {
            return Math.Max(0f, 12f - 1.5f * (float)(t - 70d));
        }

        if (t < 92d)
        {
            return 0f;
        }

        if (t < 100d)
        {
            return Math.Min(12f, (float)(t - 92d) * 1.5f);
        }

        if (t < 160d)
        {
            return 12f + 2f * (float)Math.Sin(t / 5d);
        }

        if (t < 170d)
        {
            return Math.Max(0f, 12f - 1.2f * (float)(t - 160d));
        }

        return 0f;
    }

    private static RideResult RunRide(bool perfectPlayer, int seed)
    {
        Random random = new Random(seed);
        ObstacleDirectorSettings settings = new ObstacleDirectorSettings();
        ObstacleDirector director = new ObstacleDirector(settings);
        SurfaceReport road = SurfaceReport.AllRoad(3);
        SurfaceReport sky = new SurfaceReport(3);
        RideResult result = new RideResult();
        List<SimObstacle> live = new List<SimObstacle>();
        ObstacleKind[] kinds =
        {
            ObstacleKind.Vehicle, ObstacleKind.Vehicle, ObstacleKind.Person,
            ObstacleKind.Sign, ObstacleKind.Hydrant, ObstacleKind.Bench,
            ObstacleKind.Bush
        };

        float playerX = 0f;
        int targetLane = 1;
        float playerY = 0f; // height above ground
        float verticalVelocity = 0f;
        double nextDetectorFrame = 0d;
        double stoppedDuration = 0d;
        bool stopResolvedScene = false;
        float previousSpeed = 0f;

        for (double time = 0d; time < RideSeconds; time += TickSeconds)
        {
            currentSimTime = time;
            float speed = SpeedAt(time);
            bool blind = time >= NoSurfaceStart && time < NoSurfaceEnd;
            SurfaceReport surface = blind ? sky : road;

            // Mirror of CvGameDirector's rule: a real stop resolves the
            // frozen scene instead of replaying it compressed on restart.
            if (speed < 1.5f)
            {
                stoppedDuration += TickSeconds;
                if (stoppedDuration > 1.5d && !stopResolvedScene)
                {
                    stopResolvedScene = true;
                    result.Dodged += live.Count;
                    live.Clear();
                }
            }
            else
            {
                stoppedDuration = 0d;
                stopResolvedScene = false;
            }

            // Synthetic noisy detector at 4 Hz.
            if (time >= nextDetectorFrame)
            {
                nextDetectorFrame = time + 0.25d;
                int count = random.Next(0, 4);
                List<DetectionEvent> events = new List<DetectionEvent>(count);
                for (int i = 0; i < count; i++)
                {
                    events.Add(new DetectionEvent(
                        random.Next(0, 3),
                        kinds[random.Next(kinds.Length)],
                        0.4f + (float)random.NextDouble() * 0.55f));
                }

                List<SpawnCommand> spawns = director.Step(time, speed, surface, events);
                for (int i = 0; i < spawns.Count; i++)
                {
                    result.Spawns.Add(spawns[i]);
                    result.SpawnTimes.Add(time);
                    result.SpawnSpeeds.Add(speed);
                    live.Add(new SimObstacle
                    {
                        Command = spawns[i],
                        Z = spawns[i].Distance,
                        HalfDepth = spawns[i].Kind == ObstacleKind.Vehicle ? 1.7f
                            : spawns[i].Kind == ObstacleKind.Bench ? 0.35f
                            : 0.4f
                    });
                }
            }

            // Player policy. A human sees the vehicle braking; anticipate
            // that a jumpable may become unjumpable by requiring extra speed
            // headroom while decelerating.
            if (perfectPlayer)
            {
                bool braking = speed < previousSpeed - 0.001f;
                float clearSpeedForPlanning =
                    settings.JumpClearMinSpeedMetersPerSecond + (braking ? 2f : 0f);
                // While airborne the next jump is unavailable until landing:
                // lanes whose obstacle needs a jump before then are unsafe —
                // except the obstacle the current jump is already clearing.
                double jumpReadyIn = playerY <= 0f
                    ? 0d
                    : (verticalVelocity + Math.Sqrt(
                        verticalVelocity * verticalVelocity
                        + 2f * -Gravity * playerY)) / -Gravity;
                double airClearFrom = 0d;
                double airClearUntil = -1d;
                double discriminant = verticalVelocity * verticalVelocity
                    - 4f * 11f * (0.75f - playerY);
                if (discriminant >= 0d)
                {
                    double sqrt = Math.Sqrt(discriminant);
                    airClearFrom = Math.Max(0d, (verticalVelocity - sqrt) / 22d);
                    airClearUntil = (verticalVelocity + sqrt) / 22d;
                }

                int nearestLane = (int)Math.Round(playerX / LaneWidth) + 1;
                int airLane = playerY > 0f
                    && Math.Abs(playerX - (nearestLane - 1) * LaneWidth) < 0.4f
                    ? nearestLane
                    : -1;
                targetLane = ChooseLane(
                    live, playerX, speed, clearSpeedForPlanning, targetLane,
                    jumpReadyIn, airLane, airClearFrom, airClearUntil);
                TryJump(live, playerX, speed, playerY, targetLane, ref verticalVelocity);
                debugTickCounter++;
                if (DebugWindowStart >= 0d && time >= DebugWindowStart
                    && time <= DebugWindowEnd
                    && debugTickCounter % 10 == 0)
                {
                    string state = $"t={time:F2} speed={speed:F2} x={playerX:F2} target={targetLane} ";
                    for (int lane = 0; lane < 3; lane++)
                    {
                        double s = ScoreLane(
                            live, playerX, speed,
                            settings.JumpClearMinSpeedMetersPerSecond,
                            lane, 0d, -1, 0d, -1d, out bool laneSafe);
                        state += $"L{lane}:{(laneSafe ? "safe" : $"{s:F2}")} ";
                    }

                    state += "live=[";
                    foreach (SimObstacle o in live)
                    {
                        state += $"({o.Command.Kind} L{o.Command.Lane} z={o.Z:F1})";
                    }

                    TestContext.WriteLine(state + "]");
                }
            }

            // Player motion.
            float targetX = (targetLane - 1) * LaneWidth;
            float maxStep = LaneChangeSpeed * TickSeconds;
            float deltaX = targetX - playerX;
            playerX += Math.Abs(deltaX) <= maxStep ? deltaX : Math.Sign(deltaX) * maxStep;
            verticalVelocity += Gravity * TickSeconds;
            playerY = Math.Max(0f, playerY + verticalVelocity * TickSeconds);
            if (playerY <= 0f)
            {
                verticalVelocity = Math.Max(0f, verticalVelocity);
            }

            // World motion + collisions.
            float step = speed * TickSeconds;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                SimObstacle obstacle = live[i];
                float before = obstacle.Z;
                obstacle.Z -= step;
                if (speed <= 0f && Math.Abs(obstacle.Z - before) > 1e-6f)
                {
                    result.ObstacleMovedWhileStopped = true;
                }

                float laneX = (obstacle.Command.Lane - 1) * LaneWidth;
                bool laneOverlap = Math.Abs(playerX - laneX) < PlayerHalfWidth;
                float zOverlap = obstacle.HalfDepth + 0.5f;
                bool zHit = obstacle.Z <= zOverlap && obstacle.Z >= -zOverlap;
                bool cleared = obstacle.Command.Jumpable && playerY >= 0.75f;
                if (laneOverlap && zHit && !cleared)
                {
                    result.Hits++;
                    TestContext.WriteLine(
                        $"HIT t={time:F2} kind={obstacle.Command.Kind} lane={obstacle.Command.Lane} "
                        + $"jumpable={obstacle.Command.Jumpable} z={obstacle.Z:F2} playerX={playerX:F2} "
                        + $"targetLane={targetLane} playerY={playerY:F2} speed={speed:F2} "
                        + $"spawned={obstacle.Command.Time:F2} arrival={obstacle.Command.ArrivalTime:F2}");
                    live.RemoveAt(i);
                    continue;
                }

                if (obstacle.Z < -4f)
                {
                    live.RemoveAt(i);
                    result.Dodged++;
                }
            }

            previousSpeed = speed;
        }

        return result;
    }

    /// <summary>
    /// Honest reactive policy, path-aware the way a human is: a candidate
    /// lane is judged by every lane the sphere sweeps through on the way
    /// there, using only on-screen information. Jumpable obstacles in the
    /// destination lane are acceptable when the sphere settles early enough
    /// to time a jump; everything else must not intersect the crossing.
    /// </summary>
    private static int ChooseLane(
        List<SimObstacle> live,
        float playerX,
        float speed,
        float clearSpeedForPlanning,
        int currentTarget,
        double jumpReadyIn,
        int airLane,
        double airClearFrom,
        double airClearUntil)
    {
        // Commit to the current plan while it stays safe; re-planning every
        // tick makes the player oscillate between lanes and miss jumps.
        ScoreLane(live, playerX, speed, clearSpeedForPlanning, currentTarget, jumpReadyIn, airLane, airClearFrom, airClearUntil, out bool targetSafe);
        if (targetSafe)
        {
            return currentTarget;
        }

        double bestScore = double.MinValue;
        int bestLane = currentTarget;
        for (int lane = 0; lane < 3; lane++)
        {
            if (lane == currentTarget)
            {
                continue;
            }

            double score = ScoreLane(
                live, playerX, speed, clearSpeedForPlanning, lane,
                jumpReadyIn, airLane, airClearFrom, airClearUntil, out _);
            if (score > bestScore)
            {
                bestScore = score;
                bestLane = lane;
            }
        }

        // Nowhere is safe: stay with the least-bad option, comparing the
        // current target on equal terms.
        double currentScore = ScoreLane(
            live, playerX, speed, clearSpeedForPlanning, currentTarget,
            jumpReadyIn, airLane, airClearFrom, airClearUntil, out _);
        if (bestScore <= currentScore)
        {
            return currentTarget;
        }

        return bestLane;
    }

    private static double ScoreLane(
        List<SimObstacle> live,
        float playerX,
        float speed,
        float clearSpeedForPlanning,
        int laneOnly,
        double jumpReadyIn,
        int airLane,
        double airClearFrom,
        double airClearUntil,
        out bool laneSafe)
    {
        // Margin covers tick quantization only; the interval intersection is
        // otherwise exact. An oversized margin here makes short, safe dashes
        // across a lane look dangerous and paralyzes the player in the lane
        // with the earliest threat.
        const double margin = 0.04d;
        const double horizon = 2.2d;
        double bestScore = double.MinValue;
        laneSafe = false;
        for (int lane = 0; lane < 3; lane++)
        {
            if (lane != laneOnly)
            {
                continue;
            }
            float laneX = (lane - 1) * LaneWidth;
            double travelTime = Math.Abs(laneX - playerX) / LaneChangeSpeed;
            double firstConflict = double.MaxValue;
            bool safe = true;

            for (int sweptLane = 0; sweptLane < 3; sweptLane++)
            {
                float sweptX = (sweptLane - 1) * LaneWidth;
                if (!OccupancyWindow(
                    playerX, laneX, sweptX, travelTime, horizon,
                    out double enter, out double exit))
                {
                    continue;
                }

                for (int i = 0; i < live.Count; i++)
                {
                    SimObstacle obstacle = live[i];
                    if (obstacle.Command.Lane != sweptLane)
                    {
                        continue;
                    }

                    if (speed <= 0.1f)
                    {
                        continue; // frozen world cannot hit anyone
                    }

                    double hitStart = (obstacle.Z - obstacle.HalfDepth - 0.5f) / speed;
                    double hitEnd = (obstacle.Z + obstacle.HalfDepth + 0.5f) / speed;
                    if (hitEnd < 0d)
                    {
                        continue; // already passed
                    }

                    if (obstacle.Command.Jumpable
                        && sweptLane == airLane
                        && airClearUntil > 0d
                        && hitStart >= airClearFrom - 0.02d
                        && hitEnd <= airClearUntil + 0.02d)
                    {
                        continue; // the jump in progress clears this window
                    }

                    // In the lane the player already occupies, a jumpable is
                    // manageable as long as the jump trigger band (starting
                    // 0.16 s before the window) is still reachable; when
                    // arriving from another lane, allow settling time first.
                    double settleLead = travelTime < 0.05d ? 0.13d : travelTime + 0.35d;
                    settleLead = Math.Max(settleLead, jumpReadyIn + 0.16d);
                    bool jumpManageable = obstacle.Command.Jumpable
                        && speed >= clearSpeedForPlanning
                        && sweptLane == lane
                        && hitStart > settleLead;
                    if (jumpManageable)
                    {
                        continue;
                    }

                    // Exiting the overlap the player already stands in only
                    // needs tick-level margin; entering any other lane's
                    // overlap must clear its windows with real headroom, or
                    // razor-thin crossings die to quantization.
                    double effectiveMargin = enter <= 1e-6d ? margin : 0.15d;
                    if (hitEnd + effectiveMargin >= enter
                        && hitStart - effectiveMargin <= exit)
                    {
                        safe = false;
                        firstConflict = Math.Min(firstConflict, Math.Max(0d, hitStart));
                    }
                }
            }

            double score = safe
                ? 100d - travelTime * 2d + (lane == 1 ? 0.01d : 0d)
                : firstConflict - travelTime * 0.5d;
            laneSafe = safe;
            bestScore = score;
        }

        return bestScore;
    }

    /// <summary>
    /// Time window [enter, exit] during which a straight-line lane change
    /// from playerX to targetX overlaps the swept lane's collision width.
    /// The destination lane's window extends to the planning horizon.
    /// </summary>
    private static bool OccupancyWindow(
        float playerX,
        float targetX,
        float sweptLaneX,
        double travelTime,
        double horizon,
        out double enter,
        out double exit)
    {
        float lo = sweptLaneX - PlayerHalfWidth;
        float hi = sweptLaneX + PlayerHalfWidth;
        float start = playerX;
        float end = targetX;
        bool insideNow = start > lo && start < hi;
        bool insideEnd = end > lo && end < hi;

        if (Math.Abs(end - start) < 1e-4f)
        {
            enter = 0d;
            exit = insideNow ? horizon : -1d;
            return insideNow;
        }

        float direction = Math.Sign(end - start);
        float entryX = direction > 0 ? lo : hi;
        float exitX = direction > 0 ? hi : lo;
        double speedX = LaneChangeSpeed;
        double tEntry = insideNow ? 0d : (entryX - start) * direction / speedX;
        double tExit = (exitX - start) * direction / speedX;
        if (tEntry > travelTime && !insideEnd)
        {
            enter = 0d;
            exit = -1d;
            return false;
        }

        enter = Math.Max(0d, tEntry);
        exit = insideEnd ? horizon : Math.Min(travelTime, Math.Max(0d, tExit));
        return exit >= enter;
    }

    private static void TryJump(
        List<SimObstacle> live,
        float playerX,
        float speed,
        float playerY,
        int targetLane,
        ref float verticalVelocity)
    {
        if (playerY > 0f || speed < 6f)
        {
            return;
        }

        // Only jump for obstacles in the lane being ridden: jumping for a
        // neighbor lane while sliding past its boundary wastes the jump and
        // leaves the player airborne when their own lane needs one.
        float targetX = (targetLane - 1) * LaneWidth;
        if (Math.Abs(playerX - targetX) > 0.4f)
        {
            return;
        }

        for (int i = 0; i < live.Count; i++)
        {
            SimObstacle obstacle = live[i];
            if (!obstacle.Command.Jumpable || obstacle.Command.Lane != targetLane)
            {
                continue;
            }

            // Above the 0.75 m clear height between 0.12 s and 0.56 s after
            // takeoff. Jump so the whole collision window fits inside that,
            // with slack for the window stretching if the vehicle brakes.
            float windowStart = (obstacle.Z - obstacle.HalfDepth - 0.5f) / speed;
            float windowDuration = (obstacle.HalfDepth + 0.5f) * 2f / speed;
            if (windowStart > 0.16f && windowStart < 0.20f && windowDuration <= 0.30f)
            {
                if (DebugWindowStart >= 0d && currentSimTime >= DebugWindowStart
                    && currentSimTime <= DebugWindowEnd)
                {
                    TestContext.WriteLine(
                        $"JUMP t={currentSimTime:F2} for {obstacle.Command.Kind} "
                        + $"L{obstacle.Command.Lane} z={obstacle.Z:F2} x={playerX:F2}");
                }

                verticalVelocity = JumpVelocity;
                return;
            }
        }
    }
}
