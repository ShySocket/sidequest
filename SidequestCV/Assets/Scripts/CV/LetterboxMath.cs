/// <summary>
/// YOLOX-style letterbox: the source image is scaled uniformly to fit the
/// square model input, anchored at the top-left; the remainder is padding.
/// </summary>
public static class LetterboxMath
{
    public static float ComputeScale(int sourceWidth, int sourceHeight, int inputSize)
    {
        float byWidth = (float)inputSize / sourceWidth;
        float byHeight = (float)inputSize / sourceHeight;
        return byWidth < byHeight ? byWidth : byHeight;
    }

    public static void ScaledSize(
        int sourceWidth,
        int sourceHeight,
        int inputSize,
        out int scaledWidth,
        out int scaledHeight)
    {
        float scale = ComputeScale(sourceWidth, sourceHeight, inputSize);
        scaledWidth = (int)System.Math.Round(sourceWidth * scale);
        scaledHeight = (int)System.Math.Round(sourceHeight * scale);
    }

    /// <summary>Maps a box from model-input pixels back to source pixels.</summary>
    public static Detection ToSource(Detection d, float scale)
    {
        return new Detection(
            d.ClassId,
            d.Score,
            d.CenterX / scale,
            d.CenterY / scale,
            d.Width / scale,
            d.Height / scale);
    }
}
