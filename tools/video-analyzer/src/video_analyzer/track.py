"""Track a coloured marker drawn over the footage.

A marker animated by hand over the clip is a far better description of how the
character should move than anything inferred: it carries intent. Where the
segmentation pipeline can only say where the ground is, this says where the
designer wanted the character to be, jumps included.

Colour alone is not enough to find it. Red brick fills large parts of this
footage, and a loose red threshold latches onto buildings - measured, that
inflated the marker's apparent radius from 0.09 to 0.25 of frame height between
t=32s and t=48s, and scattered its position. Three constraints separate the two:

* **Hue.** The marker sits at H=176 (deep pink) against brick at H=35 (orange).
  Matching a narrow band around the marker's own colour, with wraparound, is the
  single most effective filter.
* **Shape.** The marker is a filled disc. Brick regions are not circular, so a
  circularity test rejects them even when a few pixels pass on colour.
* **Continuity.** It cannot teleport. Candidates near the previous position are
  preferred, which resolves the remaining ambiguity.
"""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np

from .decode import VideoInfo, iter_frames


@dataclass(frozen=True)
class TrackConfig:
    hue: int = 176
    hue_tolerance: int = 9
    min_saturation: int = 130
    min_value: int = 150

    min_circularity: float = 0.62
    """4*pi*area / perimeter^2; a perfect disc is 1."""

    min_area_fraction: float = 0.0006
    max_area_fraction: float = 0.06
    """Plausible marker area as a fraction of the frame."""

    max_jump: float = 0.25
    """Largest believable movement between samples, in frame widths."""

    ignore_bottom_right: tuple[float, float] = (0.60, 0.84)
    """Editor watermarks sit here and are often the same pink as the marker."""


@dataclass(frozen=True)
class DotSample:
    time: float
    x: float
    y: float
    radius: float
    """All three normalized: x by frame width, y and radius by frame height."""


def _mask(image: np.ndarray, config: TrackConfig) -> np.ndarray:
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    height, width = image.shape[:2]

    # Hue is circular, so a band around 176 wraps past 180 back to 0.
    low = (config.hue - config.hue_tolerance) % 180
    high = (config.hue + config.hue_tolerance) % 180
    saturation_value_low = (config.min_saturation, config.min_value)

    if low < high:
        mask = cv2.inRange(hsv, (low, *saturation_value_low), (high, 255, 255))
    else:
        mask = cv2.bitwise_or(
            cv2.inRange(hsv, (low, *saturation_value_low), (180, 255, 255)),
            cv2.inRange(hsv, (0, *saturation_value_low), (high, 255, 255)),
        )

    x0, y0 = config.ignore_bottom_right
    mask[int(height * y0):, int(width * x0):] = 0
    return cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))


def _best_candidate(
    mask: np.ndarray,
    config: TrackConfig,
    previous: tuple[float, float] | None,
) -> tuple[float, float, float] | None:
    height, width = mask.shape
    frame_area = float(height * width)

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    best = None
    best_score = -1.0
    for contour in contours:
        area = cv2.contourArea(contour)
        if not (
            config.min_area_fraction * frame_area
            <= area
            <= config.max_area_fraction * frame_area
        ):
            continue

        perimeter = cv2.arcLength(contour, True)
        if perimeter <= 0:
            continue

        circularity = 4.0 * np.pi * area / (perimeter * perimeter)
        if circularity < config.min_circularity:
            continue

        moments = cv2.moments(contour)
        if moments["m00"] <= 0:
            continue

        cx = moments["m10"] / moments["m00"] / width
        cy = moments["m01"] / moments["m00"] / height

        # Prefer round and large, then prefer near where it was last seen.
        score = circularity * np.sqrt(area)
        if previous is not None:
            distance = np.hypot(cx - previous[0], cy - previous[1])
            if distance > config.max_jump:
                continue
            score /= 1.0 + 6.0 * distance

        if score > best_score:
            best_score = score
            best = (cx, cy, float(np.sqrt(area / np.pi) / height))

    return best


def track_dot(
    info: VideoInfo,
    config: TrackConfig | None = None,
    *,
    analysis_width: int = 640,
    stride: int = 1,
    progress: object = None,
) -> list[DotSample]:
    """Follow the marker through the clip, one sample per analysed frame."""
    config = config or TrackConfig()

    samples: list[DotSample] = []
    previous: tuple[float, float] | None = None

    for frame in iter_frames(info, analysis_width=analysis_width, stride=stride):
        candidate = _best_candidate(_mask(frame.image, config), config, previous)

        if candidate is None and previous is not None:
            # Allow one unconstrained retry: a genuinely fast move can exceed
            # max_jump, and refusing it would strand the tracker behind.
            candidate = _best_candidate(_mask(frame.image, config), config, None)

        if candidate is not None:
            samples.append(DotSample(frame.time, candidate[0], candidate[1], candidate[2]))
            previous = (candidate[0], candidate[1])

        if progress is not None:
            progress.update(1)

    return samples


def resample(
    samples: list[DotSample], times: np.ndarray
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Interpolate the track onto ``times``, returning (x, y, radius)."""
    if not samples:
        raise RuntimeError("marker was never found; check the colour settings")

    source = np.array([s.time for s in samples])
    return (
        np.interp(times, source, [s.x for s in samples]),
        np.interp(times, source, [s.y for s in samples]),
        np.interp(times, source, [s.radius for s in samples]),
    )


def ground_baseline(times: np.ndarray, y: np.ndarray, window: float = 3.0) -> np.ndarray:
    """The line the marker returns to between jumps.

    A rolling maximum of y (screen y grows downward, so the maximum is the
    lowest point) recovers the ground under the arcs, then a mean pass smooths
    the staircase that leaves behind.
    """
    if times.size == 0:
        return y.copy()

    baseline = np.empty_like(y)
    for index, time in enumerate(times):
        near = np.abs(times - time) <= window * 0.5
        baseline[index] = np.max(y[near]) if near.any() else y[index]

    smoothed = np.empty_like(baseline)
    for index, time in enumerate(times):
        near = np.abs(times - time) <= window * 0.5
        smoothed[index] = np.mean(baseline[near]) if near.any() else baseline[index]
    return smoothed


@dataclass(frozen=True)
class Arc:
    """One excursion of the marker above the ground: a jump."""

    start_time: float
    peak_time: float
    end_time: float
    height: float
    """Peak rise above the baseline, as a fraction of frame height."""

    @property
    def duration(self) -> float:
        return self.end_time - self.start_time


def find_arcs(
    times: np.ndarray,
    y: np.ndarray,
    baseline: np.ndarray,
    *,
    min_height: float = 0.05,
    min_duration: float = 0.15,
    merge_gap: float = 0.25,
) -> list[Arc]:
    """Find where the marker rose clear of the ground.

    Screen y decreases upward, so a jump is where ``baseline - y`` is positive.
    """
    rise = baseline - y
    above = rise > min_height
    if not above.any():
        return []

    padded = np.concatenate([[False], above, [False]])
    edges = np.diff(padded.astype(np.int8))
    starts = list(np.flatnonzero(edges == 1))
    ends = list(np.flatnonzero(edges == -1) - 1)

    spans: list[tuple[int, int]] = []
    for start, end in zip(starts, ends):
        # Keyframed motion often dips briefly mid-arc; bridge those rather than
        # reporting two jumps where the designer drew one.
        if spans and times[start] - times[spans[-1][1]] <= merge_gap:
            spans[-1] = (spans[-1][0], int(end))
        else:
            spans.append((int(start), int(end)))

    arcs: list[Arc] = []
    for start, end in spans:
        if times[end] - times[start] < min_duration:
            continue
        peak = start + int(np.argmax(rise[start : end + 1]))
        arcs.append(
            Arc(
                start_time=float(times[start]),
                peak_time=float(times[peak]),
                end_time=float(times[end]),
                height=float(rise[peak]),
            )
        )

    return arcs


def smooth(times: np.ndarray, values: np.ndarray, window: float) -> np.ndarray:
    """Gaussian-weighted smoothing over a time window.

    Marker paths come out of an editor as straight segments between keyframes,
    so they have corners a physical object would never have. Smoothing here is
    what turns "where the designer clicked" into "how a ball would travel".
    """
    if times.size < 3 or window <= 0:
        return values.copy()

    sigma = max(window * 0.5, 1e-6)
    result = np.empty_like(values, dtype=np.float64)
    for index, time in enumerate(times):
        offsets = times - time
        near = np.abs(offsets) <= window
        weights = np.exp(-0.5 * (offsets[near] / sigma) ** 2)
        result[index] = float(np.sum(weights * values[near]) / np.sum(weights))
    return result


def parked_after(times: np.ndarray, x: np.ndarray, y: np.ndarray,
                 tolerance: float = 0.01, minimum_span: float = 3.0) -> float:
    """When the marker stopped being animated, or the clip end.

    The designer stops moving it before the clip ends, and the frozen tail must
    not be read as a deliberate instruction to stand still.
    """
    if times.size == 0:
        return 0.0

    for index in range(times.size - 1, 0, -1):
        if (abs(x[index] - x[-1]) > tolerance) or (abs(y[index] - y[-1]) > tolerance):
            end = float(times[index])
            return end if times[-1] - end >= minimum_span else float(times[-1])
    return float(times[-1])
