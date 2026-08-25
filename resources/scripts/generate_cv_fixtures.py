#!/usr/bin/env python3
"""Regenerates the C# test fixtures for SidequestCV's vision pipeline.

Runs the real ONNX models with onnxruntime over the reference street photo
and writes:
  - yolox_bus_rows.bin       raw YOLOX output rows with objectness > 0.005
  - yolox_bus_expected.txt   reference decode + class-wise NMS results
  - fastscnn_bus_classmap.txt RLE-encoded Cityscapes class map

CvYoloxPostProcessorTests then asserts that the C# decoder reproduces the
Python reference exactly. Re-run this ONLY when the models or the reference
image change, and re-run the C# tests afterwards.

Setup:
  python3 -m venv .venv && .venv/bin/pip install numpy onnxruntime pillow
Usage:
  .venv/bin/python generate_cv_fixtures.py \
      --models ../../SidequestCV/Assets/Models \
      --image ../testharness/reference_street.jpg \
      --out ../../SidequestCV/Assets/Tests/Fixtures
"""

import argparse
import json
import struct
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

SCORE_THRESHOLD = 0.35
IOU_THRESHOLD = 0.45
YOLOX_INPUT = 416
SEG_WIDTH, SEG_HEIGHT = 480, 288
MMSEG_MEAN = np.array([123.675, 116.28, 103.53], np.float32)
MMSEG_STD = np.array([58.395, 57.12, 57.375], np.float32)


def letterbox(img: Image.Image, size: int) -> tuple[np.ndarray, float]:
    w0, h0 = img.size
    scale = min(size / w0, size / h0)
    nw, nh = int(round(w0 * scale)), int(round(h0 * scale))
    resized = np.asarray(img.resize((nw, nh), Image.BILINEAR), np.float32)
    canvas = np.full((size, size, 3), 114.0, np.float32)
    canvas[:nh, :nw] = resized
    # YOLOX consumes BGR 0-255 without normalization.
    return canvas[:, :, ::-1].transpose(2, 0, 1)[None].copy(), scale


def decode_rows(rows_idx, rows_data, in_size):
    """Grid decode for strides 8/16/32; xy/wh raw, obj/cls pre-sigmoided."""
    sizes = [(in_size // s, s) for s in (8, 16, 32)]
    counts = [n * n for n, _ in sizes]
    out = []
    for idx, row in zip(rows_idx, rows_data):
        k, acc = int(idx), 0
        for (n, stride), c in zip(sizes, counts):
            if k < acc + c:
                cell = k - acc
                out.append((
                    (float(row[0]) + cell % n) * stride,
                    (float(row[1]) + cell // n) * stride,
                    float(np.exp(row[2])) * stride,
                    float(np.exp(row[3])) * stride,
                ))
                break
            acc += c
    return out


def iou(a, b):
    ax1, ay1, ax2, ay2 = a[0] - a[2] / 2, a[1] - a[3] / 2, a[0] + a[2] / 2, a[1] + a[3] / 2
    bx1, by1, bx2, by2 = b[0] - b[2] / 2, b[1] - b[3] / 2, b[0] + b[2] / 2, b[1] + b[3] / 2
    ix = max(0.0, min(ax2, bx2) - max(ax1, bx1))
    iy = max(0.0, min(ay2, by2) - max(ay1, by1))
    inter = ix * iy
    return inter / (a[2] * a[3] + b[2] * b[3] - inter + 1e-9)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    img = Image.open(args.image).convert("RGB")
    w0, h0 = img.size

    # ---------------- YOLOX ----------------
    x, scale = letterbox(img, YOLOX_INPUT)
    session = ort.InferenceSession(
        str(args.models / "yolox_tiny.onnx"), providers=["CPUExecutionProvider"])
    out = session.run(None, {"images": x})[0][0]

    rows = np.where(out[:, 4] > 0.005)[0]
    with open(args.out / "yolox_bus_rows.bin", "wb") as f:
        f.write(struct.pack("<ii", len(rows), out.shape[1]))
        for i in rows:
            f.write(struct.pack("<i", int(i)))
            f.write(out[i].astype("<f4").tobytes())

    data = out[rows]
    boxes = decode_rows(rows, data, YOLOX_INPUT)
    candidates = []
    for (cx, cy, bw, bh), row in zip(boxes, data):
        scores = row[4] * row[5:]
        cid = int(np.argmax(scores))
        score = float(scores[cid])
        if score >= SCORE_THRESHOLD:
            candidates.append([cid, score, cx, cy, bw, bh])
    candidates.sort(key=lambda c: -c[1])
    kept = []
    for c in candidates:
        if all(k[0] != c[0] or iou(c[2:], k[2:]) < IOU_THRESHOLD for k in kept):
            kept.append(c)

    with open(args.out / "yolox_bus_expected.txt", "w") as f:
        f.write(f"{YOLOX_INPUT} {scale} {w0} {h0} {SCORE_THRESHOLD} {IOU_THRESHOLD}\n")
        for cid, score, cx, cy, bw, bh in kept:
            f.write(f"{cid} {round(score, 6)} {round(cx / scale, 3)} "
                    f"{round(cy / scale, 3)} {round(bw / scale, 3)} {round(bh / scale, 3)}\n")
    print("yolox kept:", [(c[0], round(c[1], 3)) for c in kept])

    # ---------------- Fast-SCNN ----------------
    seg_in = np.asarray(img.resize((SEG_WIDTH, SEG_HEIGHT), Image.BILINEAR), np.float32)
    xs = ((seg_in - MMSEG_MEAN) / MMSEG_STD).transpose(2, 0, 1)[None]
    seg = ort.InferenceSession(
        str(args.models / "fast_scnn_288x480.onnx"), providers=["CPUExecutionProvider"])
    classmap = seg.run(None, {"input": xs})[0][0, 0].astype(np.uint8).flatten()

    rle = []
    prev, run = int(classmap[0]), 0
    for value in classmap:
        if int(value) == prev:
            run += 1
        else:
            rle += [prev, run]
            prev, run = int(value), 1
    rle += [prev, run]
    with open(args.out / "fastscnn_bus_classmap.txt", "w") as f:
        f.write(f"{SEG_WIDTH} {SEG_HEIGHT}\n")
        f.write(" ".join(map(str, rle)))
    print("classmap runs:", len(rle) // 2)


if __name__ == "__main__":
    main()
