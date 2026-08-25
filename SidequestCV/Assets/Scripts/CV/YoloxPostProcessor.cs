using System;
using System.Collections.Generic;

/// <summary>
/// Decodes the raw YOLOX head output (rows of [x, y, w, h, obj, 80 classes],
/// with xy/wh relative to the stride grids and obj/cls already sigmoided)
/// and applies class-wise non-maximum suppression.
/// </summary>
public sealed class YoloxPostProcessor
{
    public const int ValuesPerRow = 85;
    private static readonly int[] Strides = { 8, 16, 32 };

    private readonly int inputSize;
    private readonly float scoreThreshold;
    private readonly float iouThreshold;
    private readonly int[] gridSizes;
    private readonly int[] rowOffsets;

    public YoloxPostProcessor(int inputSize, float scoreThreshold, float iouThreshold)
    {
        this.inputSize = inputSize;
        this.scoreThreshold = scoreThreshold;
        this.iouThreshold = iouThreshold;
        gridSizes = new int[Strides.Length];
        rowOffsets = new int[Strides.Length];
        int offset = 0;
        for (int i = 0; i < Strides.Length; i++)
        {
            gridSizes[i] = inputSize / Strides[i];
            rowOffsets[i] = offset;
            offset += gridSizes[i] * gridSizes[i];
        }

        RowCount = offset;
    }

    public int RowCount { get; }

    /// <summary>
    /// Decodes a full flat output tensor of shape [RowCount, 85] and returns
    /// the surviving detections in model-input pixel space.
    /// </summary>
    public List<Detection> Decode(float[] flatOutput)
    {
        List<Detection> candidates = new List<Detection>();
        int rows = Math.Min(RowCount, flatOutput.Length / ValuesPerRow);
        for (int row = 0; row < rows; row++)
        {
            DecodeRow(row, flatOutput, row * ValuesPerRow, candidates);
        }

        return Nms(candidates, iouThreshold);
    }

    /// <summary>
    /// Decodes sparse rows (original row index + 85 values each). Used by
    /// tests to replay reference tensors without carrying all 3549 rows.
    /// </summary>
    public List<Detection> DecodeSparse(int[] rowIndices, float[] rowValues)
    {
        List<Detection> candidates = new List<Detection>();
        for (int i = 0; i < rowIndices.Length; i++)
        {
            DecodeRow(rowIndices[i], rowValues, i * ValuesPerRow, candidates);
        }

        return Nms(candidates, iouThreshold);
    }

    private void DecodeRow(int rowIndex, float[] values, int valueOffset, List<Detection> candidates)
    {
        float objectness = values[valueOffset + 4];
        if (objectness <= 0f)
        {
            return;
        }

        int bestClass = -1;
        float bestScore = 0f;
        for (int c = 5; c < ValuesPerRow; c++)
        {
            float score = objectness * values[valueOffset + c];
            if (score > bestScore)
            {
                bestScore = score;
                bestClass = c - 5;
            }
        }

        if (bestClass < 0 || bestScore < scoreThreshold)
        {
            return;
        }

        FindGrid(rowIndex, out int gridSize, out int stride, out int cell);
        int gx = cell % gridSize;
        int gy = cell / gridSize;
        float cx = (values[valueOffset] + gx) * stride;
        float cy = (values[valueOffset + 1] + gy) * stride;
        float w = (float)Math.Exp(values[valueOffset + 2]) * stride;
        float h = (float)Math.Exp(values[valueOffset + 3]) * stride;
        candidates.Add(new Detection(bestClass, bestScore, cx, cy, w, h));
    }

    private void FindGrid(int rowIndex, out int gridSize, out int stride, out int cell)
    {
        for (int i = Strides.Length - 1; i >= 0; i--)
        {
            if (rowIndex >= rowOffsets[i])
            {
                gridSize = gridSizes[i];
                stride = Strides[i];
                cell = rowIndex - rowOffsets[i];
                return;
            }
        }

        gridSize = gridSizes[0];
        stride = Strides[0];
        cell = rowIndex;
    }

    public static List<Detection> Nms(List<Detection> candidates, float iouThreshold)
    {
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        List<Detection> kept = new List<Detection>();
        for (int i = 0; i < candidates.Count; i++)
        {
            bool suppressed = false;
            for (int k = 0; k < kept.Count; k++)
            {
                if (kept[k].ClassId == candidates[i].ClassId
                    && IntersectionOverUnion(kept[k], candidates[i]) >= iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(candidates[i]);
            }
        }

        return kept;
    }

    public static float IntersectionOverUnion(Detection a, Detection b)
    {
        float ix = Math.Max(0f, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        float iy = Math.Max(0f, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        float intersection = ix * iy;
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0f ? 0f : intersection / union;
    }
}
