"""Time <-> distance mapping.

The level file is parameterized by *distance travelled*, never by video time.
See the milestone plan for why: obstacles baked at a timestamp stop lining up
the moment the player drives at a speed other than the one the clip was shot
at, whereas distance-parameterized events stay correct at any playback rate.

This module owns that mapping and nothing else, so it stays pure and testable.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

# Distance must strictly increase for the map to be invertible. A vehicle
# stopped at a red light produces a flat span, which would make time_at()
# ambiguous and cause the video to jump-cut across the stop. Rather than excise
# those frames (which would need an actual edit), we floor the speed at a small
# epsilon so the map stays strictly monotonic and the video merely crawls
# through the stop. The untouched spans are reported separately by
# find_stopped_spans() so the overlay and the game can treat them specially.
MIN_SPEED_EPSILON = 1e-3


@dataclass(frozen=True)
class StoppedSpan:
    """A span where the vehicle was not moving, in video time."""

    start_time: float
    end_time: float

    @property
    def duration(self) -> float:
        return self.end_time - self.start_time


@dataclass(frozen=True)
class DistanceMap:
    """A strictly increasing map between video time and travelled distance.

    ``times`` and ``distances`` are parallel arrays, both strictly increasing.
    Distances are in *relative* units (see the plan: this clip carries no GPS
    track, so there is no metric ground truth to calibrate against).
    """

    times: np.ndarray
    distances: np.ndarray

    def __post_init__(self) -> None:
        if self.times.ndim != 1 or self.distances.ndim != 1:
            raise ValueError("times and distances must be 1-D")
        if self.times.size != self.distances.size:
            raise ValueError(
                f"times ({self.times.size}) and distances ({self.distances.size}) "
                "must be the same length"
            )
        if self.times.size < 2:
            raise ValueError("need at least two samples to build a map")
        if not np.all(np.diff(self.times) > 0):
            raise ValueError("times must be strictly increasing")
        if not np.all(np.diff(self.distances) > 0):
            raise ValueError("distances must be strictly increasing")

    @property
    def total_distance(self) -> float:
        return float(self.distances[-1])

    @property
    def duration(self) -> float:
        return float(self.times[-1] - self.times[0])

    def distance_at(self, time: float | np.ndarray) -> float | np.ndarray:
        """Distance travelled by ``time``. Clamps outside the clip."""
        result = np.interp(time, self.times, self.distances)
        return float(result) if np.isscalar(time) else result

    def time_at(self, distance: float | np.ndarray) -> float | np.ndarray:
        """Video time at which ``distance`` is reached. Clamps outside the clip.

        This is the query the game makes every frame: it integrates the player's
        own speed into a distance, then asks which video time to display.
        """
        result = np.interp(distance, self.distances, self.times)
        return float(result) if np.isscalar(distance) else result

    def resample(self, count: int) -> tuple[np.ndarray, np.ndarray]:
        """Resample to ``count`` evenly spaced points for serialization.

        The per-frame map is far denser than the game needs; this keeps the
        level file small while preserving the shape of the curve.
        """
        if count < 2:
            raise ValueError("count must be at least 2")
        times = np.linspace(self.times[0], self.times[-1], count)
        return times, np.interp(times, self.times, self.distances)


def build_distance_map(
    times: np.ndarray,
    speeds: np.ndarray,
    *,
    min_speed: float = MIN_SPEED_EPSILON,
) -> DistanceMap:
    """Integrate a speed curve into a strictly increasing distance map.

    ``speeds`` are in relative units per second and are floored at ``min_speed``
    (see MIN_SPEED_EPSILON) so the resulting map is always invertible.
    """
    times = np.asarray(times, dtype=np.float64)
    speeds = np.asarray(speeds, dtype=np.float64)

    if times.size != speeds.size:
        raise ValueError(
            f"times ({times.size}) and speeds ({speeds.size}) must be the same length"
        )
    if times.size < 2:
        raise ValueError("need at least two samples to integrate")
    if not np.all(np.diff(times) > 0):
        raise ValueError("times must be strictly increasing")

    clamped = np.maximum(speeds, min_speed)

    # Trapezoidal integration, which is exact for the piecewise-linear speed
    # curve we actually have (flow is sampled per frame pair and interpolated).
    intervals = np.diff(times)
    midpoint_speeds = 0.5 * (clamped[:-1] + clamped[1:])
    distances = np.concatenate([[0.0], np.cumsum(intervals * midpoint_speeds)])

    return DistanceMap(times=times, distances=distances)


def find_stopped_spans(
    times: np.ndarray,
    speeds: np.ndarray,
    *,
    speed_threshold: float,
    min_duration: float = 0.5,
) -> list[StoppedSpan]:
    """Find spans where speed stays below ``speed_threshold``.

    ``min_duration`` suppresses single-frame flow dropouts, which are common
    when the view is briefly filled by a passing vehicle.
    """
    times = np.asarray(times, dtype=np.float64)
    speeds = np.asarray(speeds, dtype=np.float64)

    if times.size != speeds.size:
        raise ValueError(
            f"times ({times.size}) and speeds ({speeds.size}) must be the same length"
        )
    if times.size == 0:
        return []

    below = speeds < speed_threshold
    spans: list[StoppedSpan] = []

    # Walk the boundaries of each contiguous run of below-threshold samples.
    padded = np.concatenate([[False], below, [False]])
    edges = np.diff(padded.astype(np.int8))
    starts = np.flatnonzero(edges == 1)
    ends = np.flatnonzero(edges == -1) - 1

    for start, end in zip(starts, ends):
        span = StoppedSpan(float(times[start]), float(times[end]))
        if span.duration >= min_duration:
            spans.append(span)

    return spans
