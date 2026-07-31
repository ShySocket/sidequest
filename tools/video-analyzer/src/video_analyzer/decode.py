"""Video decoding and frame iteration."""

from __future__ import annotations

from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np


@dataclass(frozen=True)
class VideoInfo:
    path: Path
    fps: float
    frame_count: int
    width: int
    height: int
    rotation: float

    @property
    def duration(self) -> float:
        return self.frame_count / self.fps if self.fps > 0 else 0.0


@dataclass(frozen=True)
class Frame:
    index: int
    time: float
    image: np.ndarray
    """BGR, scaled to the analysis resolution."""


def probe(path: Path) -> VideoInfo:
    capture = cv2.VideoCapture(str(path))
    if not capture.isOpened():
        raise RuntimeError(f"could not open video: {path}")
    try:
        fps = capture.get(cv2.CAP_PROP_FPS)
        frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
        width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
        height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
        rotation = capture.get(cv2.CAP_PROP_ORIENTATION_META)
    finally:
        capture.release()

    if fps <= 0:
        raise RuntimeError(f"video reports a non-positive frame rate ({fps}): {path}")

    return VideoInfo(
        path=path,
        fps=fps,
        frame_count=frame_count,
        width=width,
        height=height,
        rotation=rotation,
    )


def iter_frames(
    info: VideoInfo,
    *,
    analysis_width: int = 960,
    stride: int = 1,
) -> Iterator[Frame]:
    """Yield frames downscaled to ``analysis_width``.

    Analysis runs at a reduced resolution because optical flow and detection
    gain nothing from 1080p here; the original is only needed for playback.
    ``stride`` skips frames for quick passes over a long clip.
    """
    if stride < 1:
        raise ValueError("stride must be at least 1")

    capture = cv2.VideoCapture(str(info.path))
    if not capture.isOpened():
        raise RuntimeError(f"could not open video: {info.path}")

    # Let OpenCV apply the container's rotation metadata so downstream code
    # always sees upright frames. iPhone clips rely on this.
    capture.set(cv2.CAP_PROP_ORIENTATION_AUTO, 1)

    scale = analysis_width / info.width if info.width > analysis_width else 1.0

    try:
        index = 0
        while True:
            ok, image = capture.read()
            if not ok:
                break

            if index % stride == 0:
                if scale != 1.0:
                    image = cv2.resize(
                        image, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA
                    )
                yield Frame(index=index, time=index / info.fps, image=image)

            index += 1
    finally:
        capture.release()
