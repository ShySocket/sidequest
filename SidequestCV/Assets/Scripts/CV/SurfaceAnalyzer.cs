using System;

public enum SurfaceType
{
    None,
    Road,
    Sidewalk,
    Rail,
    Path
}

/// <summary>Per-frame summary of what the character can run on.</summary>
public sealed class SurfaceReport
{
    public SurfaceReport(int laneCount)
    {
        LaneSurfaces = new SurfaceType[laneCount];
        LaneSurfaceFractions = new float[laneCount];
    }

    public SurfaceType[] LaneSurfaces { get; }
    public float[] LaneSurfaceFractions { get; }
    public SurfaceType DominantSurface { get; set; }
    public bool HasAnySurface { get; set; }
    public float VegetationLeftFraction { get; set; }
    public float VegetationRightFraction { get; set; }

    public static SurfaceReport AllRoad(int laneCount)
    {
        SurfaceReport report = new SurfaceReport(laneCount);
        for (int i = 0; i < laneCount; i++)
        {
            report.LaneSurfaces[i] = SurfaceType.Road;
            report.LaneSurfaceFractions[i] = 1f;
        }

        report.DominantSurface = SurfaceType.Road;
        report.HasAnySurface = true;
        return report;
    }
}

/// <summary>
/// Reduces a Cityscapes class map to per-lane runnable surfaces. Lanes are
/// vertical thirds of a bottom band of the image — the region of the camera
/// frame the sphere visually occupies.
///
/// Cityscapes' 19 train classes have no rail-track label, so "Rail" is
/// approximated: a large train presence inside the band marks the lane as
/// rail. Terrain maps to Path so dirt trails stay runnable.
/// </summary>
public sealed class SurfaceAnalyzer
{
    private readonly int laneCount;
    private readonly float bandTopFraction;
    private readonly float minSurfaceFraction;

    public SurfaceAnalyzer(
        int laneCount = 3,
        float bandTopFraction = 0.6f,
        float minSurfaceFraction = 0.3f)
    {
        if (laneCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(laneCount));
        }

        this.laneCount = laneCount;
        this.bandTopFraction = bandTopFraction;
        this.minSurfaceFraction = minSurfaceFraction;
    }

    public SurfaceReport Analyze(byte[] classMap, int width, int height)
    {
        SurfaceReport report = new SurfaceReport(laneCount);
        int bandTop = (int)(height * bandTopFraction);
        int bandRows = height - bandTop;
        if (bandRows <= 0 || width < laneCount)
        {
            return report;
        }

        int laneWidth = width / laneCount;
        Span<int> road = stackalloc int[laneCount];
        Span<int> sidewalk = stackalloc int[laneCount];
        Span<int> rail = stackalloc int[laneCount];
        Span<int> path = stackalloc int[laneCount];
        Span<int> total = stackalloc int[laneCount];
        int vegetationLeft = 0;
        int vegetationRight = 0;
        int sideColumnWidth = Math.Max(1, width / 6);

        for (int y = bandTop; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int lane = Math.Min(laneCount - 1, x / laneWidth);
                int classId = classMap[rowOffset + x];
                total[lane]++;
                switch (classId)
                {
                    case CvClassCatalog.CityRoad:
                        road[lane]++;
                        break;
                    case CvClassCatalog.CitySidewalk:
                        sidewalk[lane]++;
                        break;
                    case CvClassCatalog.CityTrain:
                        rail[lane]++;
                        break;
                    case CvClassCatalog.CityTerrain:
                        path[lane]++;
                        break;
                    case CvClassCatalog.CityVegetation:
                        if (x < sideColumnWidth)
                        {
                            vegetationLeft++;
                        }
                        else if (x >= width - sideColumnWidth)
                        {
                            vegetationRight++;
                        }

                        break;
                }
            }
        }

        int sideColumnPixels = bandRows * sideColumnWidth;
        report.VegetationLeftFraction = sideColumnPixels > 0
            ? (float)vegetationLeft / sideColumnPixels
            : 0f;
        report.VegetationRightFraction = sideColumnPixels > 0
            ? (float)vegetationRight / sideColumnPixels
            : 0f;

        int bestLaneVotes = 0;
        SurfaceType dominant = SurfaceType.None;
        for (int lane = 0; lane < laneCount; lane++)
        {
            if (total[lane] == 0)
            {
                continue;
            }

            int best = road[lane];
            SurfaceType type = SurfaceType.Road;
            if (sidewalk[lane] > best)
            {
                best = sidewalk[lane];
                type = SurfaceType.Sidewalk;
            }

            if (rail[lane] > best)
            {
                best = rail[lane];
                type = SurfaceType.Rail;
            }

            if (path[lane] > best)
            {
                best = path[lane];
                type = SurfaceType.Path;
            }

            float fraction = (float)best / total[lane];
            report.LaneSurfaceFractions[lane] = fraction;
            report.LaneSurfaces[lane] = fraction >= minSurfaceFraction
                ? type
                : SurfaceType.None;
            if (report.LaneSurfaces[lane] != SurfaceType.None)
            {
                report.HasAnySurface = true;
                if (best > bestLaneVotes)
                {
                    bestLaneVotes = best;
                    dominant = type;
                }
            }
        }

        report.DominantSurface = dominant;
        return report;
    }
}
