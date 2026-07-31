"""Level file assembly.

The level file is the contract between this pipeline and Unity, and the whole
point of the offline milestone: a future real-time on-device pipeline has to
produce the same thing.

Every field is keyed on **distance travelled**, never video time, so a level
stays correct whatever speed the player drives at. Positions are normalized 0..1
against frame size so a level survives a change of analysis resolution.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .decode import VideoInfo
from .distance import DistanceMap, StoppedSpan
from .obstacles import ObstacleConfig, ObstacleEvent, column_profile
from .segment import NO_GROUND, SurfaceFrame

LEVEL_VERSION = 1

MIN_GAP_FRAMES = 3
"""Surface dropouts shorter than this are bridged rather than emitted as gaps.

Segmentation drops the surface for a frame or two fairly often - a shadow, a
sun flare, a car crossing the prompt points. Emitting those verbatim fragmented
IMG_3775.mov into 36 gaps, 13 of them under 0.1% of the level, which would leave
the character repeatedly falling through holes a few centimetres wide. Only
sustained losses - the car passing within a metre of a building - are real.
"""


@dataclass(frozen=True)
class SurfaceSegment:
    """A stretch of continuously runnable ground."""

    start_distance: float
    end_distance: float
    path: list[tuple[float, float]]
    """(distance, normalized y) samples of the run line at the character column."""


def bridge_short_runs(flags: np.ndarray, min_run: int) -> np.ndarray:
    """Fill runs of ``False`` shorter than ``min_run``."""
    result = flags.copy()
    if min_run <= 1 or result.size == 0:
        return result

    padded = np.concatenate([[True], result, [True]])
    edges = np.diff(padded.astype(np.int8))
    starts = np.flatnonzero(edges == -1)
    ends = np.flatnonzero(edges == 1)

    for start, end in zip(starts, ends):
        if end - start < min_run:
            result[start:end] = True
    return result


def build_surface_segments(
    surfaces: list[SurfaceFrame],
    distance_map: DistanceMap,
    config: ObstacleConfig,
    min_gap_frames: int = MIN_GAP_FRAMES,
) -> tuple[list[SurfaceSegment], list[tuple[float, float]]]:
    """Split the run line into runnable segments, and report the gaps.

    A gap is a stretch where SAM 2 found no plausible ground - most often the car
    passing within a metre of a building, where the wall fills the frame. The
    character has nowhere to run there, so the game needs to know rather than
    being handed a path drawn through a wall.

    Short dropouts are bridged first (see MIN_GAP_FRAMES) and their path points
    interpolated from the surrounding frames.
    """
    centre = int(surfaces[0].width * config.character_column)
    times = np.array([s.time for s in surfaces])
    distances = np.asarray(distance_map.distance_at(times))

    raw = np.array(
        [s.is_plausible_ground and s.run_line[centre] != NO_GROUND for s in surfaces]
    )
    usable = bridge_short_runs(raw, min_gap_frames)

    # The path describes the *ground*, so it is built only from frames measuring
    # free ground. While an obstacle occupies the column the run line dives to
    # the bottom of the frame, and an untrustworthy frame can put it anywhere;
    # following either literally would teleport the character. Both are
    # interpolated over, and the obstacle is recorded separately.
    profile = column_profile(surfaces, config)
    heights = profile.height.copy()
    measured = raw & profile.free_ground

    if measured.any() and not measured.all():
        heights[~measured] = np.interp(
            np.flatnonzero(~measured), np.flatnonzero(measured), heights[measured]
        )

    segments: list[SurfaceSegment] = []
    gaps: list[tuple[float, float]] = []

    padded = np.concatenate([[False], usable, [False]])
    edges = np.diff(padded.astype(np.int8))
    for start, end in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1) - 1):
        if end <= start:
            continue
        path = [
            (float(distances[i]), float(heights[i])) for i in range(start, end + 1)
        ]
        segments.append(SurfaceSegment(path[0][0], path[-1][0], path))

    padded = np.concatenate([[True], usable, [True]])
    edges = np.diff(padded.astype(np.int8))
    for start, end in zip(np.flatnonzero(edges == -1), np.flatnonzero(edges == 1) - 1):
        gaps.append((float(distances[start]), float(distances[min(end, len(surfaces) - 1)])))

    return segments, gaps


def _thin(path: list[tuple[float, float]], max_points: int) -> list[list[float]]:
    """Evenly subsample a path, always keeping its endpoints."""
    if len(path) <= max_points:
        return [[round(d, 4), round(y, 5)] for d, y in path]
    picks = np.linspace(0, len(path) - 1, max_points).astype(int)
    return [[round(path[i][0], 4), round(path[i][1], 5)] for i in picks]


def build_level(
    video: Path,
    info: VideoInfo,
    distance_map: DistanceMap,
    surfaces: list[SurfaceFrame],
    obstacles: list[ObstacleEvent],
    stopped_spans: list[StoppedSpan],
    config: ObstacleConfig,
    *,
    diagnostics: dict | None = None,
    map_samples: int = 2000,
    path_samples: int = 4000,
) -> dict:
    segments, gaps = build_surface_segments(surfaces, distance_map, config)
    map_times, map_distances = distance_map.resample(map_samples)

    per_segment = max(16, path_samples // max(1, len(segments)))

    return {
        "version": LEVEL_VERSION,
        "source": {
            "file": video.name,
            "fps": round(info.fps, 6),
            "width": info.width,
            "height": info.height,
            "duration": round(info.duration, 4),
        },
        "distanceUnits": "relative",
        "characterColumn": config.character_column,
        "totalDistance": round(distance_map.total_distance, 4),
        "analysis": diagnostics or {},
        "timeToDistance": [
            [round(t, 5), round(d, 4)] for t, d in zip(map_times, map_distances)
        ],
        "surfaceSegments": [
            {
                "startDistance": round(segment.start_distance, 4),
                "endDistance": round(segment.end_distance, 4),
                "path": _thin(segment.path, per_segment),
            }
            for segment in segments
        ],
        "surfaceGaps": [
            {"startDistance": round(start, 4), "endDistance": round(end, 4)}
            for start, end in gaps
        ],
        "obstacles": [
            {
                "id": event.id,
                "class": event.label,
                "distance": round(event.distance, 4),
                "startDistance": round(event.start_distance, 4),
                "endDistance": round(event.end_distance, 4),
                "blockage": round(event.blockage, 3),
                "height": round(event.height, 4),
                "confidence": round(event.confidence, 3),
            }
            for event in obstacles
        ],
        "stoppedSpans": [
            {"start": round(s.start_time, 3), "end": round(s.end_time, 3)}
            for s in stopped_spans
        ],
    }


def write_level(path: Path, level: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(level, indent=2))
