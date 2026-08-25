using System.Collections.Generic;
using NUnit.Framework;

public sealed class CvObstacleDirectorTests
{
    private static ObstacleDirectorSettings Settings()
    {
        return new ObstacleDirectorSettings();
    }

    private static List<DetectionEvent> Events(params DetectionEvent[] events)
    {
        return new List<DetectionEvent>(events);
    }

    [Test]
    public void NoSpawnsWhileStopped()
    {
        ObstacleDirector director = new ObstacleDirector(Settings());
        var spawns = director.Step(
            0d,
            0.4f,
            SurfaceReport.AllRoad(3),
            Events(new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f)));

        Assert.That(spawns.Count, Is.Zero);
    }

    [Test]
    public void NoSpawnsWithoutASurface()
    {
        ObstacleDirector director = new ObstacleDirector(Settings());
        SurfaceReport sky = new SurfaceReport(3);

        var spawns = director.Step(
            0d,
            10f,
            sky,
            Events(new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f)));

        Assert.That(spawns.Count, Is.Zero);
    }

    [Test]
    public void RepeatedDetectionOfTheSameObjectSpawnsOnce()
    {
        ObstacleDirector director = new ObstacleDirector(Settings());
        SurfaceReport surface = SurfaceReport.AllRoad(3);
        int total = 0;
        for (int frame = 0; frame < 5; frame++)
        {
            double time = frame * 0.2d;
            total += director.Step(
                time,
                10f,
                surface,
                Events(new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f))).Count;
        }

        Assert.That(total, Is.EqualTo(1));
    }

    [Test]
    public void SpawnGuaranteesMinimumReactionTime()
    {
        ObstacleDirectorSettings settings = Settings();
        foreach (float speed in new[] { 2f, 5f, 12f, 25f })
        {
            ObstacleDirector director = new ObstacleDirector(settings);
            var spawns = director.Step(
                0d,
                speed,
                SurfaceReport.AllRoad(3),
                Events(new DetectionEvent(0, ObstacleKind.Vehicle, 0.9f)));
            Assert.That(spawns.Count, Is.EqualTo(1), $"speed {speed}");
            float arrivalSeconds = spawns[0].Distance / speed;
            Assert.That(
                arrivalSeconds,
                Is.GreaterThanOrEqualTo(settings.MinReactionSeconds),
                $"speed {speed}");
        }
    }

    [Test]
    public void ImpossiblyFastTravelSuppressesSpawnsInsteadOfCheating()
    {
        ObstacleDirectorSettings settings = Settings();
        float unfairSpeed = settings.MaxSpawnDistanceMeters / settings.MinReactionSeconds + 1f;
        ObstacleDirector director = new ObstacleDirector(settings);

        var spawns = director.Step(
            0d,
            unfairSpeed,
            SurfaceReport.AllRoad(3),
            Events(new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f)));

        Assert.That(spawns.Count, Is.Zero);
    }

    [Test]
    public void VehiclesDoNotSpawnOnASidewalkLane()
    {
        SurfaceReport surface = new SurfaceReport(3);
        surface.LaneSurfaces[0] = SurfaceType.Sidewalk;
        surface.LaneSurfaces[1] = SurfaceType.Sidewalk;
        surface.LaneSurfaces[2] = SurfaceType.Sidewalk;
        surface.HasAnySurface = true;
        surface.DominantSurface = SurfaceType.Sidewalk;
        ObstacleDirector director = new ObstacleDirector(Settings());

        var vehicleSpawns = director.Step(
            0d, 8f, surface, Events(new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f)));
        var personSpawns = director.Step(
            1d, 8f, surface, Events(new DetectionEvent(1, ObstacleKind.Person, 0.9f)));

        Assert.That(vehicleSpawns.Count, Is.Zero);
        Assert.That(personSpawns.Count, Is.EqualTo(1));
    }

    [Test]
    public void NonJumpableObstaclesNeverBlockEveryLane()
    {
        ObstacleDirectorSettings settings = Settings();
        ObstacleDirector director = new ObstacleDirector(settings);
        SurfaceReport surface = SurfaceReport.AllRoad(3);
        const float speed = 10f;
        var all = new List<SpawnCommand>();

        // Adversarial stream: vehicles and people in every lane every frame.
        for (int frame = 0; frame < 200; frame++)
        {
            double time = frame * 0.1d;
            var events = Events(
                new DetectionEvent(0, ObstacleKind.Vehicle, 0.9f),
                new DetectionEvent(1, ObstacleKind.Vehicle, 0.9f),
                new DetectionEvent(2, ObstacleKind.Vehicle, 0.9f),
                new DetectionEvent(0, ObstacleKind.Person, 0.9f),
                new DetectionEvent(1, ObstacleKind.Person, 0.9f),
                new DetectionEvent(2, ObstacleKind.Person, 0.9f));
            all.AddRange(director.Step(time, speed, surface, events));
        }

        Assert.That(all.Count, Is.GreaterThan(5), "the stream should still produce obstacles");

        // At every non-jumpable arrival instant at least one lane must be
        // free of non-jumpable arrivals within the dodge window.
        foreach (SpawnCommand spawn in all)
        {
            if (spawn.Jumpable)
            {
                continue;
            }

            int blockedLanes = 0;
            for (int lane = 0; lane < 3; lane++)
            {
                bool blocked = false;
                foreach (SpawnCommand other in all)
                {
                    if (other.Lane != lane || other.Jumpable)
                    {
                        continue;
                    }

                    if (System.Math.Abs(other.ArrivalTime - spawn.ArrivalTime)
                        < settings.DodgeWindowSeconds)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                {
                    blockedLanes++;
                }
            }

            Assert.That(blockedLanes, Is.LessThan(3), $"arrival {spawn.ArrivalTime:F2}");
        }
    }

    [Test]
    public void GlobalSpawnRateIsBounded()
    {
        ObstacleDirectorSettings settings = Settings();
        ObstacleDirector director = new ObstacleDirector(settings);
        SurfaceReport surface = SurfaceReport.AllRoad(3);
        var times = new List<double>();
        ObstacleKind[] kinds =
        {
            ObstacleKind.Vehicle, ObstacleKind.Person, ObstacleKind.Sign,
            ObstacleKind.Hydrant, ObstacleKind.Bench, ObstacleKind.Bush
        };

        for (int frame = 0; frame < 400; frame++)
        {
            double time = frame * 0.05d;
            var events = new List<DetectionEvent>();
            for (int lane = 0; lane < 3; lane++)
            {
                foreach (ObstacleKind kind in kinds)
                {
                    events.Add(new DetectionEvent(lane, kind, 0.9f));
                }
            }

            foreach (SpawnCommand spawn in director.Step(time, 10f, surface, events))
            {
                times.Add(spawn.Time);
            }
        }

        foreach (double start in times)
        {
            int inWindow = 0;
            foreach (double t in times)
            {
                if (t >= start && t < start + 1d)
                {
                    inWindow++;
                }
            }

            Assert.That(inWindow, Is.LessThanOrEqualTo((int)settings.MaxSpawnsPerSecond + 1));
        }
    }

    [Test]
    public void LowSpeedTreatsJumpablesAsBlockers()
    {
        ObstacleDirectorSettings settings = Settings();
        ObstacleDirector director = new ObstacleDirector(settings);
        SurfaceReport surface = SurfaceReport.AllRoad(3);
        const float crawl = 2f; // below JumpClearMinSpeed

        // Fill all three lanes with hydrants at the same arrival moment.
        var spawns = new List<SpawnCommand>();
        for (int frame = 0; frame < 3; frame++)
        {
            spawns.AddRange(director.Step(
                frame * 0.01d,
                crawl,
                surface,
                Events(
                    new DetectionEvent(0, ObstacleKind.Hydrant, 0.9f),
                    new DetectionEvent(1, ObstacleKind.Hydrant, 0.9f),
                    new DetectionEvent(2, ObstacleKind.Hydrant, 0.9f))));
        }

        Assert.That(spawns.Count, Is.LessThan(3), "one lane must stay open at crawl speed");
    }
}
