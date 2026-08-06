"""Build a playable level from a hand-authored timeline plus the CV analysis.

Two sources, each doing what it is good at:

* ``timeline.json`` says **what happens and when**, in video seconds. A person
  watching the clip knows that the van at 11s is the thing to jump; no detector
  needs to be trusted for that.
* The CV analysis says **where the ground is on screen**, frame by frame, which
  is exactly the part a person cannot author by hand.

Times become distances through the measured time<->distance map, so the level
stays correct at any playback speed - the same reason the CV level file is
distance-keyed.

The emitted schema (version 2) deliberately avoids top-level arrays and arrays
of arrays so Unity's built-in ``JsonUtility`` can read it, saving a Newtonsoft
dependency.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .decode import VideoInfo
from .distance import DistanceMap
from .level import SurfaceSegment
from .track import Arc, smooth

AUTHORED_VERSION = 3

MARKER_SMOOTHING = 0.55
"""Seconds of Gaussian smoothing applied to the tracked marker path.

An editor interpolates linearly between keyframes, so the raw track has corners
no physical object would have - which is exactly the "jumps are not animated
well" complaint. Smoothing turns where the designer clicked into how a ball
would actually travel.
"""

SEAM_BLEND = 1.2
"""Seconds over which the marker path hands back to the segmented ground line."""

MIN_COLUMN = 0.10
MAX_COLUMN = 0.90
"""Keep the character fully on screen."""

PATH_SMOOTHING = 0.35
"""Seconds of smoothing applied to the whole finished path.

The segmented ground line is measured per frame and carries sampling noise the
hand-drawn marker does not, so without this the character visibly settles down
after the marker's coverage ends.
"""

COLUMN_RETURN = 4.0
"""Seconds to ease back to the default column after the marker stops.

The marker ends parked at the far left because that is where the designer
stopped animating, not a decision to stand there for the rest of the run - and
obstacles arrive from that edge, so staying would leave no reaction time.
"""

DEFAULT_SURFACE = "floor"


@dataclass(frozen=True)
class MarkerTrack:
    """A hand-animated marker: what the designer wanted, not what was inferred."""

    times: np.ndarray
    x: np.ndarray
    y: np.ndarray
    arcs: list[Arc]
    animated_until: float
    median_radius: float

    @staticmethod
    def load(path: Path) -> MarkerTrack:
        raw = json.loads(path.read_text())
        samples = raw["samples"]
        return MarkerTrack(
            times=np.array([s["t"] for s in samples], dtype=np.float64),
            x=np.array([s["x"] for s in samples], dtype=np.float64),
            y=np.array([s["y"] for s in samples], dtype=np.float64),
            arcs=[
                Arc(a["start"], a["peak"], a["end"], a["height"]) for a in raw.get("arcs", [])
            ],
            animated_until=float(raw.get("animatedUntil", 0.0)),
            median_radius=float(raw.get("medianRadius", 0.0)),
        )


@dataclass(frozen=True)
class Timeline:
    surfaces: list[dict]
    events: list[dict]
    hidden: list[dict]
    surface_offsets: dict[str, float]
    event_windows: dict[str, float]

    @staticmethod
    def load(path: Path) -> Timeline:
        raw = json.loads(path.read_text())
        return Timeline(
            surfaces=sorted(raw.get("surfaces", []), key=lambda s: s["from"]),
            events=sorted(raw.get("events", []), key=lambda e: e["time"]),
            hidden=sorted(raw.get("hidden", []), key=lambda h: h["from"]),
            surface_offsets=raw.get("surfaceOffsets", {}),
            event_windows=raw.get("eventWindows", {}),
        )


def surface_at(timeline: Timeline, time: float) -> str:
    """Which surface the character is on at a given video time."""
    current = DEFAULT_SURFACE
    for entry in timeline.surfaces:
        if time >= entry["from"]:
            current = entry["surface"]
    return current


def _sample_ground(
    segments: list[SurfaceSegment],
    distance_map: DistanceMap,
    times: np.ndarray,
) -> np.ndarray:
    """Ground height at each time, from the CV run line.

    The CV pass leaves gaps where no surface was visible. The authored level
    still needs a height everywhere - the character has to be somewhere even
    while hidden - so gaps are interpolated across.
    """
    points: list[tuple[float, float]] = []
    for segment in segments:
        points.extend(segment.path)
    if not points:
        return np.full(times.size, 0.6)

    points.sort()
    distances = np.array([d for d, _ in points])
    heights = np.array([y for _, y in points])

    return np.interp(np.asarray(distance_map.distance_at(times)), distances, heights)


def build_authored_level(
    video: Path,
    info: VideoInfo,
    distance_map: DistanceMap,
    segments: list[SurfaceSegment],
    timeline: Timeline,
    *,
    character_column: float,
    travel_direction: int = -1,
    marker: MarkerTrack | None = None,
    speed_curve: tuple[np.ndarray, np.ndarray] | None = None,
    analysis_width: int = 960,
    playback_file: str | None = None,
    path_samples: int = 1400,
    map_samples: int = 1200,
) -> dict:
    duration = float(distance_map.times[-1])
    sample_times = np.linspace(float(distance_map.times[0]), duration, path_samples)

    ground = _sample_ground(segments, distance_map, sample_times)
    offsets = np.array(
        [timeline.surface_offsets.get(surface_at(timeline, t), 0.0) for t in sample_times]
    )

    # A surface change is a step in height. Ease it over a short window so the
    # character steps up onto the rail rather than teleporting - the same
    # continuity concern the CV path had.
    blended = ground + _smooth_step(offsets, sample_times, ramp=0.45)
    columns = np.full(sample_times.size, character_column)

    if marker is not None:
        blended, columns = _apply_marker(marker, sample_times, blended, columns)

    # Smooth the finished article, not just the marker's share of it, so the
    # handover to the segmented line does not read as the character getting the
    # shakes.
    blended = smooth(sample_times, blended, PATH_SMOOTHING)
    columns = np.clip(smooth(sample_times, columns, PATH_SMOOTHING), MIN_COLUMN, MAX_COLUMN)

    path = [
        {
            "d": round(float(distance_map.distance_at(t)), 3),
            "y": round(float(y), 5),
            "x": round(float(x), 5),
            "s": surface_at(timeline, t),
        }
        for t, y, x in zip(sample_times, blended, columns)
    ]

    # Screen speed, in frame widths per second. The ball needs this to roll
    # without slipping: angular rate is speed / radius, and both have to be in
    # the same units, which pixels at some arbitrary analysis width are not.
    screen_speed = []
    if speed_curve is not None:
        speed_times, speed_values = speed_curve
        sampled = np.interp(sample_times, speed_times, speed_values) / float(analysis_width)
        screen_speed = [
            {"d": round(float(distance_map.distance_at(t)), 3), "v": round(float(v), 5)}
            for t, v in zip(sample_times, sampled)
        ]

    map_times = np.linspace(float(distance_map.times[0]), duration, map_samples)
    time_to_distance = [
        {"t": round(float(t), 5), "d": round(float(distance_map.distance_at(t)), 3)}
        for t in map_times
    ]

    events = []

    # Marker arcs describe the jumps the designer actually drew, so they replace
    # the hand-typed cues wherever the marker was animated. Beyond that the
    # timeline is all there is.
    marker_end = marker.animated_until if marker is not None else -1.0
    entries: list[dict] = []
    if marker is not None:
        for arc in marker.arcs:
            entries.append(
                {
                    "time": arc.peak_time,
                    "type": "jump",
                    "label": "marker",
                    "height": arc.height,
                    "source": "marker",
                }
            )
    entries.extend(e for e in timeline.events if float(e["time"]) > marker_end)
    entries.sort(key=lambda e: e["time"])

    for index, entry in enumerate(entries):
        time = float(entry["time"])
        kind = entry.get("type", "jump")
        window = float(timeline.event_windows.get(kind, 0.55))
        events.append(
            {
                "id": index,
                "type": kind,
                "label": entry.get("label", kind),
                "time": round(time, 3),
                "distance": round(float(distance_map.distance_at(time)), 3),
                "window": round(window, 3),
                # Per-cue height, so a jump drawn small stays small rather than
                # every jump using one tuned constant.
                "height": round(float(entry.get("height", 0.0)), 4),
                "source": entry.get("source", "timeline"),
                # Half-width in distance, not seconds: the game tracks distance,
                # and a fixed number of seconds would be a different amount of
                # road depending on how fast the car was going just there.
                "windowDistance": round(
                    float(distance_map.distance_at(min(time + window * 0.5, duration)))
                    - float(distance_map.distance_at(time)),
                    3,
                ),
            }
        )

    hidden = [
        {
            "startDistance": round(float(distance_map.distance_at(float(span["from"]))), 3),
            "endDistance": round(float(distance_map.distance_at(float(span["to"]))), 3),
            "startTime": round(float(span["from"]), 3),
            "endTime": round(float(span["to"]), 3),
        }
        for span in timeline.hidden
    ]

    return {
        "version": AUTHORED_VERSION,
        "source": {
            "file": video.name,
            "fps": round(info.fps, 6),
            "width": info.width,
            "height": info.height,
            "duration": round(info.duration, 4),
        },
        # The clip the game plays, which is not the clip that was analysed: the
        # game uses a smaller, cheaper transcode. Recording it here rather than in
        # the scene means swapping clips needs no scene rebuild.
        "playbackFile": playback_file or video.name,
        "distanceUnits": "relative",
        "characterColumn": character_column,
        # -1 = the character travels right-to-left across the screen. Measured
        # from optical flow, not assumed: filming out the other window flips it.
        "travelDirection": travel_direction,
        # The size the movement was drawn at, so the game does not depend on a
        # tuning value baked into a saved scene.
        "markerDiameter": round(2 * marker.median_radius, 5) if marker is not None else 0.0,
        "totalDistance": round(distance_map.total_distance, 3),
        "timeToDistance": time_to_distance,
        "path": path,
        "screenSpeed": screen_speed,
        "events": events,
        "hidden": hidden,
    }


def _apply_marker(
    marker: MarkerTrack,
    sample_times: np.ndarray,
    ground: np.ndarray,
    columns: np.ndarray,
) -> tuple[np.ndarray, np.ndarray]:
    """Replace the inferred path with the designer's, where they drew one.

    The marker's own vertical baseline is used rather than the segmented ground
    line: where the two disagree, the designer's intent wins. Past the point they
    stopped animating, the segmented line takes over again, eased across so the
    handover is not a step.
    """
    from .track import ground_baseline

    baseline = ground_baseline(marker.times, marker.y)
    marker_ground = smooth(marker.times, baseline, MARKER_SMOOTHING)
    marker_column = smooth(marker.times, marker.x, MARKER_SMOOTHING)

    sampled_ground = np.interp(sample_times, marker.times, marker_ground)
    sampled_column = np.interp(sample_times, marker.times, marker_column)
    sampled_column = np.clip(sampled_column, MIN_COLUMN, MAX_COLUMN)

    # 1 while the marker was animated, easing to 0 over the seam.
    weight = np.clip((marker.animated_until - sample_times) / SEAM_BLEND, 0.0, 1.0)
    weight = weight * weight * (3.0 - 2.0 * weight)

    # Ease from where the marker left the character back to the default column
    # over COLUMN_RETURN seconds, rather than holding the far edge for the rest
    # of the run.
    default_column = float(columns[-1]) if columns.size else 0.35
    ease = np.clip((sample_times - marker.animated_until) / COLUMN_RETURN, 0.0, 1.0)
    ease = ease * ease * (3.0 - 2.0 * ease)
    held = float(np.clip(marker_column[-1], MIN_COLUMN, MAX_COLUMN))
    after = held + (default_column - held) * ease

    return (
        ground * (1.0 - weight) + sampled_ground * weight,
        after * (1.0 - weight) + sampled_column * weight,
    )


def _smooth_step(values: np.ndarray, times: np.ndarray, ramp: float) -> np.ndarray:
    """Ease step changes in ``values`` over ``ramp`` seconds.

    Surfaces change instantly in the timeline, but the character has to travel
    between them; a hard step would read as a teleport rather than a hop.
    """
    if values.size < 2:
        return values.copy()

    result = values.astype(np.float64).copy()
    changes = np.flatnonzero(np.diff(values) != 0)

    for index in changes:
        start_time = times[index]
        window = (times >= start_time) & (times <= start_time + ramp)
        if window.sum() < 2:
            continue
        before, after = values[index], values[index + 1]
        alpha = np.clip((times[window] - start_time) / ramp, 0.0, 1.0)
        # Smoothstep, so the transition leaves and arrives with zero slope.
        result[window] = before + (after - before) * (alpha * alpha * (3 - 2 * alpha))
        result[times > start_time + ramp] = after

    return result


def write_authored(path: Path, level: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(level, indent=1))
