using System.Collections.Generic;

/// <summary>
/// Converts detector boxes (source-image pixels) into lane-based gameplay
/// events. Only objects that plausibly sit on the path ahead become events:
/// their box bottom must reach into the lower part of the frame and the box
/// must not be vanishingly small.
/// </summary>
public sealed class DetectionEventMapper
{
    private readonly int laneCount;
    private readonly float minBottomFraction;
    private readonly float minHeightFraction;

    public DetectionEventMapper(
        int laneCount = 3,
        float minBottomFraction = 0.45f,
        float minHeightFraction = 0.04f)
    {
        this.laneCount = laneCount;
        this.minBottomFraction = minBottomFraction;
        this.minHeightFraction = minHeightFraction;
    }

    public List<DetectionEvent> Map(IReadOnlyList<Detection> detections, int sourceWidth, int sourceHeight)
    {
        List<DetectionEvent> events = new List<DetectionEvent>();
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return events;
        }

        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];
            if (!CvClassCatalog.TryMapCocoToObstacle(d.ClassId, out ObstacleKind kind))
            {
                continue;
            }

            if (d.Bottom < sourceHeight * minBottomFraction)
            {
                continue;
            }

            if (d.Height < sourceHeight * minHeightFraction)
            {
                continue;
            }

            float normalizedX = d.CenterX / sourceWidth;
            int lane = (int)(normalizedX * laneCount);
            if (lane < 0)
            {
                lane = 0;
            }
            else if (lane >= laneCount)
            {
                lane = laneCount - 1;
            }

            events.Add(new DetectionEvent(lane, kind, d.Score));
        }

        return events;
    }
}
