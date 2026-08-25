using NUnit.Framework;

public sealed class CvSurfaceAnalyzerTests
{
    [Test]
    public void ReferenceStreetPhotoReadsAsRoad()
    {
        byte[] map = CvFixtures.LoadClassMap(out int width, out int height);
        SurfaceAnalyzer analyzer = new SurfaceAnalyzer();

        SurfaceReport report = analyzer.Analyze(map, width, height);

        Assert.That(report.HasAnySurface, Is.True);
        Assert.That(report.DominantSurface, Is.EqualTo(SurfaceType.Road));
    }

    [Test]
    public void SidewalkBandIsClassifiedPerLane()
    {
        int width = 90;
        int height = 60;
        byte[] map = Filled(width, height, CvClassCatalog.CitySky);
        // Bottom band: left lane sidewalk, middle road, right vegetation.
        for (int y = 40; y < 60; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                map[y * width + x] = CvClassCatalog.CitySidewalk;
            }

            for (int x = 30; x < 60; x++)
            {
                map[y * width + x] = CvClassCatalog.CityRoad;
            }

            for (int x = 60; x < 90; x++)
            {
                map[y * width + x] = CvClassCatalog.CityVegetation;
            }
        }

        SurfaceReport report = new SurfaceAnalyzer().Analyze(map, width, height);

        Assert.That(report.LaneSurfaces[0], Is.EqualTo(SurfaceType.Sidewalk));
        Assert.That(report.LaneSurfaces[1], Is.EqualTo(SurfaceType.Road));
        Assert.That(report.LaneSurfaces[2], Is.EqualTo(SurfaceType.None));
        Assert.That(report.VegetationRightFraction, Is.GreaterThan(0.5f));
        Assert.That(report.HasAnySurface, Is.True);
    }

    [Test]
    public void SkyOnlyFrameHasNoSurface()
    {
        byte[] map = Filled(60, 40, CvClassCatalog.CitySky);

        SurfaceReport report = new SurfaceAnalyzer().Analyze(map, 60, 40);

        Assert.That(report.HasAnySurface, Is.False);
        Assert.That(report.DominantSurface, Is.EqualTo(SurfaceType.None));
    }

    [Test]
    public void TrainPresenceMarksRailLane()
    {
        int width = 90;
        int height = 60;
        byte[] map = Filled(width, height, CvClassCatalog.CityRoad);
        for (int y = 36; y < 60; y++)
        {
            for (int x = 30; x < 60; x++)
            {
                map[y * width + x] = CvClassCatalog.CityTrain;
            }
        }

        SurfaceReport report = new SurfaceAnalyzer().Analyze(map, width, height);

        Assert.That(report.LaneSurfaces[1], Is.EqualTo(SurfaceType.Rail));
        Assert.That(report.LaneSurfaces[0], Is.EqualTo(SurfaceType.Road));
    }

    private static byte[] Filled(int width, int height, int classId)
    {
        byte[] map = new byte[width * height];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = (byte)classId;
        }

        return map;
    }
}
