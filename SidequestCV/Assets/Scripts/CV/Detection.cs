/// <summary>
/// One detected object in source-image pixel coordinates (center + size).
/// </summary>
public readonly struct Detection
{
    public Detection(int classId, float score, float centerX, float centerY, float width, float height)
    {
        ClassId = classId;
        Score = score;
        CenterX = centerX;
        CenterY = centerY;
        Width = width;
        Height = height;
    }

    public int ClassId { get; }
    public float Score { get; }
    public float CenterX { get; }
    public float CenterY { get; }
    public float Width { get; }
    public float Height { get; }

    public float Left => CenterX - Width * 0.5f;
    public float Right => CenterX + Width * 0.5f;
    public float Top => CenterY - Height * 0.5f;
    public float Bottom => CenterY + Height * 0.5f;
}
