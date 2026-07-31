"""Obstacle events, derived from where the run line breaks.

The character occupies a fixed column of the screen, so an obstacle matters at
exactly the moment it occupies that same column. That makes tracking
unnecessary: rather than following each object across the frame and predicting
when it will arrive, we watch the character's own column and record when the
surface there stops being runnable. The event is then converted from time into
distance, which is what the level file is parameterized by.

A note on the sign, which is counterintuitive. The run line is the *topmost*
ground pixel per column. An object standing on the ground hides the ground
behind it, so the first visible ground in that column is the object's base -
which is *lower* in the image, i.e. a larger y. Obstacles therefore push the run
line **down**, not up. Columns where the object hides the surface completely come
back as NO_GROUND.

What is deliberately not stored: tap timestamps. The level file records where an
obstacle is, and Unity derives the tap window from the jump physics in force at
runtime. Baking tap times would freeze one particular jumpVelocity/gravityScale
pairing into every level file and invalidate all of them on the next retune.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .detect import DetectionFrame
from .distance import DistanceMap
from .segment import NO_GROUND, SurfaceFrame


@dataclass(frozen=True)
class ObstacleConfig:
    character_column: float = 0.35
    """Where the character stands, as a fraction of frame width.

    Matches the viewport position CameraFollow2D holds the player at, which
    RunnerMovementTests asserts stays within 0.1-0.5.
    """

    column_halfwidth: float = 0.03
    """Half-width of the character's footprint, as a fraction of frame width."""

    blocked_column_fraction: float = 0.5
    """Share of footprint columns that must be blocked to count as an obstacle."""

    notch_depth: float = 0.035
    """Run-line displacement counting as blocked, as a fraction of frame height.

    Below this, ordinary curb wobble and segmentation jitter would register as
    obstacles.
    """

    baseline_window: int = 61
    """Temporal median width, in surface frames, for the free-ground baseline."""

    column_smoothing: int = 9
    """Median filter width, in columns, applied to the run line before testing.

    Textured asphalt makes the mask boundary ragged, and those single-column
    spikes were manufacturing obstacles on open road. Real obstacles are many
    columns wide, so a small median removes the noise and leaves them intact.
    """

    max_rise: float = 0.15
    """How far above free ground the run line may sit before the frame is junk.

    Ground viewed sideways cannot suddenly appear near the top of the frame; when
    it does, the mask has latched onto something vertical.
    """

    min_valid_fraction: float = 0.6
    """Least share of columns with ground for a frame to report obstacles.

    "The ground is not visible" is not the same claim as "something is blocking
    the way", and conflating them put a phantom obstacle at t=0.4s where SAM 2
    had found ground in under half the frame. Frames below this contribute to
    surface gaps instead. On IMG_3775.mov this excludes ~9% of frames.
    """

    merge_gap_frames: int = 3
    """Bridge dropouts up to this many surface frames long.

    A single object often blinks out for a frame or two as it crosses, and
    without this it would emit as several obstacles in a row. Counted in frames
    rather than distance because blinking is a per-frame segmentation artefact:
    a distance threshold would mean something different on every clip.
    """

    min_frames: int = 1
    """Discard events shorter than this many surface frames.

    1 keeps everything - a thin pole legitimately crosses in a single sample.
    """


@dataclass(frozen=True)
class ObstacleEvent:
    id: int
    distance: float
    """Distance at which the obstacle reaches the character's column."""

    start_distance: float
    end_distance: float
    label: str
    confidence: float
    blockage: float
    """Peak share of the character's footprint that was unrunnable, 0..1."""

    height: float
    """Obstacle height as a fraction of frame height, 0 if unknown."""

    @property
    def span(self) -> float:
        return self.end_distance - self.start_distance


def free_ground_baseline(surfaces: list[SurfaceFrame], window: int) -> np.ndarray:
    """The run-line level with obstacles removed, per surface frame.

    Uses the per-frame median across columns - obstacles occupy a minority of
    them, so the median tracks free ground - then a temporal median to suppress
    frames where that assumption briefly fails.
    """
    per_frame = np.array(
        [
            np.median(s.run_line[s.run_line != NO_GROUND]) / s.height
            if np.any(s.run_line != NO_GROUND)
            else np.nan
            for s in surfaces
        ],
        dtype=np.float64,
    )

    valid = ~np.isnan(per_frame)
    if not valid.any():
        return np.zeros(len(surfaces))
    if not valid.all():
        per_frame[~valid] = np.interp(
            np.flatnonzero(~valid), np.flatnonzero(valid), per_frame[valid]
        )

    # Shrink the window rather than skipping it on a short clip: without the
    # temporal median the baseline follows whatever junk frame it is handed,
    # and every downstream check is measured against that baseline.
    window = min(window, per_frame.size if per_frame.size % 2 else per_frame.size - 1)
    if window <= 1:
        return per_frame

    half = window // 2
    padded = np.pad(per_frame, half, mode="edge")
    return np.median(np.lib.stride_tricks.sliding_window_view(padded, 2 * half + 1), axis=1)


def surface_profile(surface: SurfaceFrame, config: ObstacleConfig) -> np.ndarray:
    """Normalized run line per column, denoised, with gaps read as fully blocked.

    NO_GROUND maps to 1.0 (the bottom of the frame) before filtering, so a
    blocked column and a deeply displaced one sit on the same scale and the
    median filter can mix them meaningfully.
    """
    profile = np.where(
        surface.run_line == NO_GROUND, 1.0, surface.run_line / surface.height
    ).astype(np.float64)

    window = config.column_smoothing
    if window <= 1 or profile.size < window:
        return profile
    if window % 2 == 0:
        window += 1

    half = window // 2
    padded = np.pad(profile, half, mode="edge")
    return np.median(np.lib.stride_tricks.sliding_window_view(padded, window), axis=1)


def footprint(surface: SurfaceFrame, config: ObstacleConfig) -> np.ndarray:
    """The denoised run line across the character's footprint."""
    centre = int(surface.width * config.character_column)
    half = max(1, int(surface.width * config.column_halfwidth))
    start = max(0, centre - half)
    end = min(surface.width, centre + half + 1)
    return surface_profile(surface, config)[start:end]


def blocked_fraction(
    surface: SurfaceFrame, baseline: float, config: ObstacleConfig
) -> tuple[float, float]:
    """Share of the character's footprint that is unrunnable, and how deeply.

    Returns ``(fraction, depth)`` where depth is the largest normalized run-line
    displacement across the footprint.
    """
    profile = footprint(surface, config)
    if profile.size == 0:
        return 0.0, 0.0

    # Obstacles push the run line DOWN the image (larger y) - see module docstring.
    displacement = profile - baseline
    blocked = displacement > config.notch_depth

    return float(np.mean(blocked)), float(np.max(displacement, initial=0.0))


def _classify(
    detection_frames: list[DetectionFrame],
    detection_times: np.ndarray,
    start_time: float,
    end_time: float,
    column_x: float,
    frame_width: int,
) -> tuple[str, float, float]:
    """Name an obstacle from detections overlapping the character's column.

    Surface classes are skipped: a sidewalk spanning the column is what the
    character runs on, not something to dodge.
    """
    if detection_times.size == 0:
        return "unknown", 0.0, 0.0

    within = np.flatnonzero((detection_times >= start_time) & (detection_times <= end_time))
    if within.size == 0:
        within = [int(np.argmin(np.abs(detection_times - 0.5 * (start_time + end_time))))]

    best_label = "unknown"
    best_score = 0.0
    best_height = 0.0

    for position in within:
        for detection in detection_frames[position].detections:
            if detection.is_surface or detection.label == "tree":
                continue
            if not detection.spans_column(column_x):
                continue
            if detection.score > best_score:
                best_label = detection.label
                best_score = detection.score
                best_height = abs(detection.box[3] - detection.box[1]) / frame_width

    return best_label, best_score, best_height


@dataclass(frozen=True)
class ColumnProfile:
    """Per-frame state of the character's column, shared by both consumers.

    Obstacle extraction and path building need the same underlying measurements
    and must agree about which frames are usable; computing it once keeps them
    from drifting apart.
    """

    baseline: np.ndarray
    height: np.ndarray
    """Denoised run line over the character's footprint, normalized.

    A median across the footprint rather than the value at the exact centre
    column, so it is measured over the same span as ``fraction``. Sampling one
    column while judging blockage over many let an outlier column through as a
    39%-of-frame path jump at t~37.0s.
    """

    fraction: np.ndarray
    """Share of the footprint that is unrunnable."""

    blocked: np.ndarray
    """An obstacle occupies the column."""

    trustworthy: np.ndarray
    """The frame's surface is good enough to believe at all."""

    @property
    def free_ground(self) -> np.ndarray:
        """Frames measuring genuine, unobstructed ground."""
        return self.trustworthy & ~self.blocked


def column_profile(surfaces: list[SurfaceFrame], config: ObstacleConfig) -> ColumnProfile:
    if not surfaces:
        empty_bool = np.zeros(0, dtype=bool)
        empty = np.zeros(0)
        return ColumnProfile(empty, empty, empty, empty_bool, empty_bool)

    baseline = free_ground_baseline(surfaces, config.baseline_window)

    fractions = np.empty(len(surfaces))
    heights = np.empty(len(surfaces))
    for position, surface in enumerate(surfaces):
        fractions[position], _ = blocked_fraction(surface, baseline[position], config)
        heights[position] = float(np.median(footprint(surface, config)))

    # Three independent ways a frame can be untrustworthy:
    #   - the mask is not a ground plane at all (a wall filling the frame),
    #   - too little ground was found to say anything,
    #   - the run line sits far ABOVE free ground, which no ground surface does.
    # The last one matters: at t~70.1s a wall slipped through the coverage band
    # at 58.8% and put the run line at the very top of the frame, which would
    # have teleported the character by 90% of frame height.
    trustworthy = np.array(
        [
            s.is_plausible_ground and s.valid_fraction >= config.min_valid_fraction
            for s in surfaces
        ]
    ) & (heights > baseline - config.max_rise)

    return ColumnProfile(
        baseline=baseline,
        height=heights,
        fraction=fractions,
        blocked=(fractions >= config.blocked_column_fraction) & trustworthy,
        trustworthy=trustworthy,
    )


def extract_obstacles(
    surfaces: list[SurfaceFrame],
    distance_map: DistanceMap,
    detection_frames: list[DetectionFrame] | None = None,
    config: ObstacleConfig | None = None,
) -> list[ObstacleEvent]:
    """Find obstacle events along the character's column, in distance."""
    config = config or ObstacleConfig()
    if not surfaces:
        return []

    profile = column_profile(surfaces, config)
    fractions = profile.fraction
    blocked = profile.blocked
    times = np.array([s.time for s in surfaces])
    distances = np.asarray(distance_map.distance_at(times))

    detection_times = (
        np.array([f.time for f in detection_frames]) if detection_frames else np.array([])
    )
    frame_width = surfaces[0].width
    column_x = frame_width * config.character_column

    # Contiguous runs of blocked frames.
    padded = np.concatenate([[False], blocked, [False]])
    edges = np.diff(padded.astype(np.int8))
    starts = np.flatnonzero(edges == 1)
    ends = np.flatnonzero(edges == -1) - 1

    spans: list[tuple[int, int]] = []
    for start, end in zip(starts, ends):
        if spans and start - spans[-1][1] - 1 <= config.merge_gap_frames:
            spans[-1] = (spans[-1][0], int(end))
        else:
            spans.append((int(start), int(end)))

    events: list[ObstacleEvent] = []
    for start, end in spans:
        if end - start + 1 < config.min_frames:
            continue

        start_distance = float(distances[start])
        end_distance = float(distances[end])
        peak = start + int(np.argmax(fractions[start : end + 1]))
        label, score, height = _classify(
            detection_frames or [],
            detection_times,
            float(times[start]),
            float(times[end]),
            column_x,
            frame_width,
        )

        events.append(
            ObstacleEvent(
                id=len(events),
                distance=float(distances[peak]),
                start_distance=start_distance,
                end_distance=end_distance,
                label=label,
                confidence=score,
                blockage=float(np.max(fractions[start : end + 1])),
                height=height,
            )
        )

    return events
