"""Debug overlay rendering.

The overlay is the acceptance test for the pipeline: numbers in a JSON file are
easy to misread, but a wrong speed curve or a drifting path is obvious the
moment it is drawn back onto the footage.
"""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np

from .decode import VideoInfo, iter_frames
from .detect import DetectionFrame
from .distance import DistanceMap
from .obstacles import ObstacleConfig, ObstacleEvent
from .segment import NO_GROUND, SurfaceFrame
from .speed import SpeedConfig, SpeedResult

WHITE = (255, 255, 255)
GREY = (150, 150, 150)
CYAN = (255, 220, 0)
AMBER = (0, 190, 255)
RED = (60, 60, 255)

PLOT_HEIGHT = 150
FONT = cv2.FONT_HERSHEY_SIMPLEX


def _draw_plot(
    width: int,
    result: SpeedResult,
    current_time: float,
) -> np.ndarray:
    """A speed-vs-time strip with a playhead."""
    panel = np.full((PLOT_HEIGHT, width, 3), 24, dtype=np.uint8)

    speeds = result.speeds
    times = result.times
    if speeds.size == 0:
        return panel

    peak = float(np.percentile(speeds, 99.5)) or 1.0
    margin = 24

    # Speed curve.
    xs = np.interp(times, (times[0], times[-1]), (0, width - 1)).astype(np.int32)
    ys = (
        PLOT_HEIGHT
        - margin
        - np.clip(speeds / peak, 0, 1) * (PLOT_HEIGHT - 2 * margin)
    ).astype(np.int32)
    cv2.polylines(panel, [np.stack([xs, ys], axis=1)], False, CYAN, 1, cv2.LINE_AA)

    # Stop threshold.
    threshold_y = int(
        PLOT_HEIGHT - margin - min(result.stop_threshold / peak, 1.0) * (PLOT_HEIGHT - 2 * margin)
    )
    cv2.line(panel, (0, threshold_y), (width, threshold_y), RED, 1)
    cv2.putText(panel, "stop", (6, threshold_y - 4), FONT, 0.35, RED, 1, cv2.LINE_AA)

    # Playhead.
    head_x = int(np.interp(current_time, (times[0], times[-1]), (0, width - 1)))
    cv2.line(panel, (head_x, 0), (head_x, PLOT_HEIGHT), WHITE, 1)

    cv2.putText(
        panel, f"{peak:.0f} px/s", (width - 90, margin - 6), FONT, 0.38, GREY, 1, cv2.LINE_AA
    )
    cv2.putText(panel, "speed", (6, 16), FONT, 0.42, GREY, 1, cv2.LINE_AA)
    return panel


def _draw_roi(image: np.ndarray, config: SpeedConfig) -> None:
    height, width = image.shape[:2]
    top = int(height * config.roi_top)
    bottom = int(height * config.roi_bottom)

    shaded = image.copy()
    cv2.rectangle(shaded, (0, top), (width, bottom), (0, 90, 0), -1)
    cv2.addWeighted(shaded, 0.18, image, 0.82, 0, image)
    cv2.rectangle(image, (0, top), (width - 1, bottom), (0, 200, 0), 1)
    cv2.putText(
        image, "flow ROI (near ground)", (8, top - 8), FONT, 0.5, (0, 220, 0), 1, cv2.LINE_AA
    )


def _draw_run_line(image: np.ndarray, surface: SurfaceFrame) -> None:
    """Draw the run line, breaking it wherever the ground is interrupted.

    The breaks matter: a gap means an object is standing on the surface, so
    drawing through it would hide exactly the obstacles we care about.
    """
    height, width = image.shape[:2]
    scale_x = width / surface.width
    scale_y = height / surface.height

    segment: list[tuple[int, int]] = []
    for column, value in enumerate(surface.run_line):
        if value == NO_GROUND:
            if len(segment) > 1:
                cv2.polylines(
                    image, [np.array(segment, np.int32)], False, (0, 0, 255), 2, cv2.LINE_AA
                )
            segment = []
            continue
        segment.append((int(column * scale_x), int(value * scale_y)))

    if len(segment) > 1:
        cv2.polylines(image, [np.array(segment, np.int32)], False, (0, 0, 255), 2, cv2.LINE_AA)


CLASS_COLOURS = {
    "car": (255, 0, 255),
    "traffic sign": (60, 60, 255),
    "pole": (200, 200, 255),
    "bush": (0, 200, 200),
    "unknown": (255, 255, 255),
}


def _draw_character_column(image: np.ndarray, config: ObstacleConfig, blocked: bool) -> None:
    """The column the character occupies: obstacles matter only here."""
    height, width = image.shape[:2]
    centre = int(width * config.character_column)
    half = max(1, int(width * config.column_halfwidth))
    colour = RED if blocked else (0, 255, 120)

    band = image.copy()
    cv2.rectangle(band, (centre - half, 0), (centre + half, height), colour, -1)
    cv2.addWeighted(band, 0.18, image, 0.82, 0, image)
    cv2.line(image, (centre, 0), (centre, height), colour, 1)


def _draw_detections(image: np.ndarray, frame: DetectionFrame, scale: float) -> None:
    for detection in frame.detections:
        if detection.is_surface or detection.label == "tree":
            continue
        x1, y1, x2, y2 = (int(v * scale) for v in detection.box)
        colour = CLASS_COLOURS.get(detection.label, (180, 180, 180))
        cv2.rectangle(image, (x1, y1), (x2, y2), colour, 1)
        cv2.putText(
            image,
            f"{detection.label} {detection.score:.2f}",
            (x1 + 2, max(y1 - 4, 12)),
            FONT,
            0.4,
            colour,
            1,
            cv2.LINE_AA,
        )


def _draw_obstacle_strip(
    width: int,
    obstacles: list[ObstacleEvent],
    distance_map: DistanceMap,
    current_distance: float,
    total_distance: float,
) -> np.ndarray:
    """A distance ruler showing every obstacle in the level and where we are."""
    height = 46
    panel = np.full((height, width, 3), 18, dtype=np.uint8)
    if total_distance <= 0:
        return panel

    cv2.line(panel, (0, height - 14), (width, height - 14), (70, 70, 70), 1)
    for event in obstacles:
        x = int(event.distance / total_distance * (width - 1))
        colour = CLASS_COLOURS.get(event.label, (180, 180, 180))
        cv2.line(panel, (x, height - 30), (x, height - 8), colour, 1)

    head = int(current_distance / total_distance * (width - 1))
    cv2.line(panel, (head, 0), (head, height), WHITE, 1)
    cv2.putText(panel, "obstacles", (6, 14), FONT, 0.42, GREY, 1, cv2.LINE_AA)
    return panel


def render(
    info: VideoInfo,
    result: SpeedResult,
    distance_map: DistanceMap,
    output: Path,
    *,
    config: SpeedConfig,
    width: int = 960,
    stride: int = 1,
    surfaces: list[SurfaceFrame] | None = None,
    detections: list[DetectionFrame] | None = None,
    obstacles: list[ObstacleEvent] | None = None,
    obstacle_config: ObstacleConfig | None = None,
    progress: object = None,
) -> None:
    """Write a debug overlay video next to the analysis output."""
    output.parent.mkdir(parents=True, exist_ok=True)

    scale = width / info.width
    frame_height = int(round(info.height * scale))
    strip_height = 46 if obstacles is not None else 0
    canvas_height = frame_height + PLOT_HEIGHT + strip_height

    writer = cv2.VideoWriter(
        str(output),
        cv2.VideoWriter_fourcc(*"mp4v"),
        info.fps / stride,
        (width, canvas_height),
    )
    if not writer.isOpened():
        raise RuntimeError(f"could not open video writer for {output}")

    peak_speed = float(np.percentile(result.speeds, 99.5)) or 1.0
    surface_times = np.array([s.time for s in surfaces]) if surfaces else None
    detection_times = np.array([d.time for d in detections]) if detections else None
    total_distance = distance_map.total_distance

    try:
        for frame in iter_frames(info, analysis_width=width, stride=stride):
            image = frame.image
            if image.shape[0] != frame_height:
                image = cv2.resize(image, (width, frame_height))
            else:
                image = image.copy()

            surface = None
            if surfaces and surface_times is not None:
                surface = surfaces[int(np.argmin(np.abs(surface_times - frame.time)))]

            speed = float(np.interp(frame.time, result.times, result.speeds))
            distance = distance_map.distance_at(frame.time)

            active = None
            if obstacles:
                for event in obstacles:
                    if event.start_distance <= distance <= event.end_distance:
                        active = event
                        break

            if obstacle_config is not None:
                _draw_character_column(image, obstacle_config, active is not None)

            if surface is None:
                _draw_roi(image, config)
            else:
                _draw_run_line(image, surface)

            if detections and detection_times is not None:
                position = int(np.argmin(np.abs(detection_times - frame.time)))
                if abs(detection_times[position] - frame.time) < 0.2:
                    _draw_detections(image, detections[position], scale * info.width / 960)

            confidence = float(np.interp(frame.time, result.times, result.confidence))

            lines = [
                f"t {frame.time:6.2f}s   frame {frame.index}",
                f"speed {speed:7.1f} px/s  ({speed / peak_speed * 100:4.0f}% of peak)",
                f"distance {distance:9.1f}",
                f"confidence {confidence:.2f}",
            ]
            if surface is not None:
                lines.append(
                    f"ground {surface.coverage:5.1%}  runline {surface.valid_fraction:5.1%}"
                )
            for row, text in enumerate(lines):
                origin = (12, 28 + row * 24)
                cv2.putText(image, text, origin, FONT, 0.6, (0, 0, 0), 3, cv2.LINE_AA)
                cv2.putText(image, text, origin, FONT, 0.6, AMBER, 1, cv2.LINE_AA)

            if speed < result.stop_threshold:
                cv2.putText(
                    image, "STOPPED", (width - 190, 40), FONT, 0.9, RED, 2, cv2.LINE_AA
                )

            if active is not None:
                text = f"OBSTACLE #{active.id}  {active.label}"
                cv2.putText(image, text, (width - 340, 78), FONT, 0.7, (0, 0, 0), 4, cv2.LINE_AA)
                cv2.putText(image, text, (width - 340, 78), FONT, 0.7, RED, 2, cv2.LINE_AA)

            layers = [image, _draw_plot(width, result, frame.time)]
            if obstacles is not None:
                layers.append(
                    _draw_obstacle_strip(
                        width, obstacles, distance_map, float(distance), total_distance
                    )
                )
            writer.write(np.vstack(layers))

            if progress is not None:
                progress.update(1)
    finally:
        writer.release()
