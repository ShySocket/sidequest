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
from .ledges import load_ledges
from .segment import NO_GROUND
from .track import Arc, smooth

AUTHORED_VERSION = 3

MARKER_SMOOTHING = 0.55
"""Seconds of Gaussian smoothing applied to the tracked marker path.

An editor interpolates linearly between keyframes, so the raw track has corners
no physical object would have - which is exactly the "jumps are not animated
well" complaint. Smoothing turns where the designer clicked into how a ball
would actually travel.
"""

LEDGE_SNAP = 0.09
"""How close the marker must be to a ledge, in frame heights, to ride it.

The marker is hand-placed and lands near the surface rather than exactly on it,
so a tolerance is needed - but too generous a one snaps to a railing the
character was never on.
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

    authoritative: bool = False
    """When set, the timeline IS the choreography: marker arcs are ignored for
    events, nothing re-centres the cues, and every jump happens at exactly its
    written time on every playthrough. Built for the frame-annotated design
    where input only scores and a miss flashes the ball red."""

    ball_scale: float = 1.0
    """Multiplier on the marker-drawn ball diameter."""

    post_marker_column: float = 0.35
    """Where the character rides once the marker stops being animated."""

    path_adjust: list[dict] = None  # type: ignore[assignment]
    """Spans of {from,to,dy}: deliberate vertical nudges to the resting path,
    e.g. 'roll along the middle of the sidewalk, a bit in front'."""

    behind_spans: list[dict] = None  # type: ignore[assignment]
    """OVERRIDE spans that force occlusion even where the measured depth rule
    says no. Occlusion itself is derived automatically (see occlude.py); a
    plain clip needs no spans here."""

    front_spans: list[dict] = None  # type: ignore[assignment]
    """OVERRIDE spans where derived occlusion is suppressed - the escape hatch
    for a detector box the depth rule misreads."""

    occluder_labels: list[str] = None  # type: ignore[assignment]
    """Detection classes the ball may pass behind; None = occlude.py default."""

    size_depth: list[dict] = None  # type: ignore[assignment]
    """Spans of {from,to,y}: the ground-plane y the ball's SIZE reads its depth
    from, where it differs from the ride line. An elevated surface's top edge
    is position, not distance - a hedge top rides high on screen while the
    bush stands at the sidewalk's near edge - and sizing from the top edge
    shrinks the ball as if it had run away from the camera. Place the span
    edges mid-arc: each edge ramps over 0.3s between the depth y and the ride
    line's value AT that edge, and inside a flight the ramp is invisible."""

    @staticmethod
    def load(path: Path) -> Timeline:
        raw = json.loads(path.read_text())
        return Timeline(
            surfaces=sorted(raw.get("surfaces", []), key=lambda s: s["from"]),
            events=sorted(raw.get("events", []), key=lambda e: e["time"]),
            hidden=sorted(raw.get("hidden", []), key=lambda h: h["from"]),
            surface_offsets=raw.get("surfaceOffsets", {}),
            event_windows=raw.get("eventWindows", {}),
            authoritative=bool(raw.get("authoritativeEvents", False)),
            ball_scale=float(raw.get("ballScale", 1.0)),
            post_marker_column=float(raw.get("postMarkerColumn", 0.35)),
            path_adjust=raw.get("pathAdjust", []),
            behind_spans=raw.get("behindSpans", []),
            front_spans=raw.get("frontSpans", []),
            occluder_labels=raw.get("occluderLabels"),
            size_depth=raw.get("sizeDepth", []),
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
    ledges: Path | None = None,
    surfaces_for_clamp: list | None = None,
    detection_data: tuple | None = None,
    speed_curve: tuple[np.ndarray, np.ndarray] | None = None,
    analysis_width: int = 960,
    playback_file: str | None = None,
    path_samples: int = 1400,
    map_samples: int = 1200,
    mask_baker=None,
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
        blended, columns = _apply_marker(
            marker, sample_times, blended, columns, timeline.post_marker_column
        )

    names = [surface_at(timeline, t) for t in sample_times]
    if ledges is not None and marker is not None:
        blended, names = _snap_to_ledges(
            ledges, marker, sample_times, columns, blended, names
        )

    # Smooth the finished article, not just the marker's share of it, so the
    # handover to the segmented line does not read as the character getting the
    # shakes.
    blended = smooth(sample_times, blended, PATH_SMOOTHING)
    columns = np.clip(smooth(sample_times, columns, PATH_SMOOTHING), MIN_COLUMN, MAX_COLUMN)

    if surfaces_for_clamp is not None:
        # Clamp, settle, clamp again: the settle pass smooths the clamp's edges
        # so they do not read as steps, and the second clamp undoes any float
        # the smoothing reintroduced.
        blended = _clamp_to_ground_region(
            surfaces_for_clamp, sample_times, columns, blended, names
        )
        blended = smooth(sample_times, blended, 0.15)
        blended = _clamp_to_ground_region(
            surfaces_for_clamp, sample_times, columns, blended, names
        )
        blended = smooth(sample_times, blended, 0.15)

    # Annotated nudges last, on top of everything measured: "roll along the
    # middle of the sidewalk" or "land above the parked car" are design intent
    # the evidence cannot supply. Eased in and out over 0.4s so a span boundary
    # never reads as a step.
    for span in timeline.path_adjust or []:
        start, end = float(span["from"]), float(span["to"])
        ramp_in = np.clip((sample_times - start) / 0.4, 0.0, 1.0)
        ramp_out = np.clip((end - sample_times) / 0.4, 0.0, 1.0)
        blended = blended + float(span["dy"]) * np.minimum(ramp_in, ramp_out)

    # The SIZE line: depth is where a surface stands, not how tall it is. It
    # equals the ride line except across sizeDepth spans, whose edges ramp
    # over 0.3s between the declared depth y and the ride line's value AT the
    # edge (a constant, so the ramp never dips through the transition ease the
    # ride line itself is doing there).
    size_line = blended.copy()
    for span in timeline.size_depth or []:
        start, end = float(span["from"]), float(span["to"])
        depth_y = float(span["y"])
        edge_in = float(np.interp(start, sample_times, blended))
        edge_out = float(np.interp(end, sample_times, blended))
        inside = (sample_times >= start) & (sample_times <= end)
        ramp_in = np.clip((sample_times - start) / 0.3, 0.0, 1.0)
        ramp_out = np.clip((end - sample_times) / 0.3, 0.0, 1.0)
        values = (
            depth_y
            + (edge_in - depth_y) * (1.0 - ramp_in)
            + (edge_out - depth_y) * (1.0 - ramp_out)
        )
        size_line = np.where(inside, values, size_line)

    path = [
        {
            "d": round(float(distance_map.distance_at(t)), 3),
            "y": round(float(y), 5),
            "x": round(float(x), 5),
            "z": round(float(z), 5),
            "s": name,
        }
        for t, y, x, z, name in zip(sample_times, blended, columns, size_line, names)
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

    if timeline.authoritative:
        # The timeline IS the choreography: annotated takeoffs, landings and
        # heights, identical on every playthrough. Marker arcs and the window
        # fitter would move them, so both are bypassed. What still gets derived
        # is the scoring window and the warning lead, from the gaps between
        # events: a chained pair 0.4s apart cannot share a 0.7s window.
        entries = [dict(e) for e in timeline.events]
        entries.sort(key=lambda e: e["time"])
        # Gaps are measured between SCORED events only: a believability hop
        # (type "hop") is not played, so it must not shrink its neighbours'
        # windows or drag their warnings around.
        scored = [e for e in entries if e.get("type") != "hop"]
        for position, entry in enumerate(scored):
            previous_gap = (
                entry["time"] - scored[position - 1]["time"] if position > 0 else 99.0
            )
            next_gap = (
                scored[position + 1]["time"] - entry["time"]
                if position + 1 < len(scored)
                else 99.0
            )
            entry["window"] = float(
                np.clip(0.8 * min(previous_gap, next_gap), 0.24, 0.7)
            )
            # Long enough to genuinely play to (the 0.55s cap was annotated as
            # leaving no chance to react), short enough not to blur into the
            # previous cue.
            entry["lead"] = float(np.clip(0.7 * previous_gap, 0.45, 0.9))
        for entry in entries:
            if entry.get("type") == "hop":
                entry["window"] = 0.0
                entry["lead"] = 0.0
    else:
        # Marker arcs describe the jumps the designer actually drew, so they
        # replace the hand-typed cues wherever the marker was animated. Beyond
        # that the timeline is all there is.
        marker_end = marker.animated_until if marker is not None else -1.0
        entries = []
        if marker is not None:
            for arc in marker.arcs:
                entries.append(
                    {
                        # The arc's START, not its peak: a tap is a takeoff.
                        "time": arc.start_time,
                        "type": "jump",
                        "label": "marker",
                        "height": arc.height,
                        "airTime": float(np.clip(arc.duration, 0.8, 3.4)),
                        "source": "marker",
                    }
                )
        entries.extend(e for e in timeline.events if float(e["time"]) > marker_end)
        entries.sort(key=lambda e: e["time"])

        if detection_data is not None:
            frames, det_w, det_h = detection_data
            fit_jump_windows(
                entries,
                frames,
                (det_w, det_h),
                sample_times,
                blended,
                columns,
                2 * marker.median_radius if marker is not None else 0.173,
            )
            entries.sort(key=lambda e: e["time"])

    for index, entry in enumerate(entries):
        time = float(entry["time"])
        kind = entry.get("type", "jump")
        if entry.get("window") is not None:
            window = float(entry["window"])
        else:
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
                "airTime": round(float(entry.get("airTime", 0.0)), 3),
                # Scoring window (full seconds) and warning lead, for the
                # press-only-scores design.
                "windowSeconds": round(window, 3),
                "lead": round(float(entry.get("lead", 0.55)), 3),
                # Dodges may carry their own duration ("do not make it too
                # quick"); zero means the game's tuned default.
                "duration": round(float(entry.get("duration", 0.0)), 3),
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

    # Regions of the video the game re-draws IN FRONT of the ball, so it
    # passes visibly behind poles and signs. Derived from the depth rule in
    # occlude.py rather than authored; behindSpans/frontSpans only override.
    from .occlude import (
        OcclusionConfig,
        derive_occluder_tracks,
        foreground_entries,
    )

    occluder_tracks = []
    if detection_data is not None:
        frames, det_w, det_h = detection_data
        occluder_tracks = derive_occluder_tracks(
            frames,
            (det_w, det_h),
            sample_times,
            # The finished resting path: depth orders candidates against where
            # the BALL is, and the path is the one line the game guarantees.
            # Where the path stops meaning depth - riding a rail or hedge top -
            # the elevated flag suppresses instead.
            blended,
            columns,
            OcclusionConfig.from_timeline(timeline),
            elevated=np.array(
                [name not in GROUND_SURFACES for name in names], dtype=bool
            ),
        )
    atlas = mask_baker(occluder_tracks) if mask_baker and occluder_tracks else None

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
        "markerDiameter": round(
            2 * marker.median_radius * timeline.ball_scale, 5
        ) if marker is not None else 0.0,
        "totalDistance": round(distance_map.total_distance, 3),
        "timeToDistance": time_to_distance,
        "path": path,
        "screenSpeed": screen_speed,
        "foreground": foreground_entries(occluder_tracks, distance_map, atlas),
        "foregroundMaskFile": atlas.file_name if atlas is not None else "",
        # The classes the strips were derived from, so verifiers judge the
        # shipped strips against the same truth the derivation used.
        "occluderLabels": list(OcclusionConfig.from_timeline(timeline).labels),
        "events": events,
        "hidden": hidden,
    }


GROUND_SURFACES = {"floor", "sidewalk", "grass"}

EDGE_USABLE = 0.88
"""A ground region whose far edge sits below this is too thin to judge against."""


def fit_jump_windows(
    entries: list[dict],
    detections,
    det_size: tuple[int, int],
    sample_times: np.ndarray,
    ground: np.ndarray,
    columns: np.ndarray,
    marker_diameter: float,
) -> None:
    """Size and centre each jump cue so any accepted tap clears its obstacle.

    The cue windows were guesses; the geometry is not. An obstacle occupies the
    ball's column for a measurable span, the arc delivers height for a
    computable span, and the safe tap range is where the second covers the
    first. Each jump entry is re-centred on the middle of that range and its
    window shrunk to fit inside it, so "tap within the window" genuinely means
    "never touch" - checked later by the audit rather than assumed here.

    When no tap satisfies the arc, the arc is lengthened: the obstacle simply
    takes longer to cross than the drawn air time covers, and a floatier jump
    is the honest resolution, matching the long arcs the designer already drew.
    """
    det_times = np.array([f.time for f in detections])
    det_w, det_h = det_size
    # Deliberately the full diameter, not the radius: the game's perspective
    # scale grows the ball toward the camera, and the fitted window must hold
    # for the largest ball the audit will measure with, plus margin.
    radius = marker_diameter

    def occupancy(entry: dict, duration: float) -> list[tuple[float, float, float]]:
        """(time, ground, required lift) where a box owned by this cue crosses."""
        peak = entry["time"] + duration * 0.5
        skip = {"sidewalk", "railing", "fence", "tree", "bush"}
        out = []
        for probe in np.arange(peak - 0.55, peak + 0.55, 1.0 / 30.0):
            index = int(np.argmin(np.abs(det_times - probe)))
            if abs(det_times[index] - probe) > 0.25:
                continue
            g = float(np.interp(probe, sample_times, ground))
            column = float(np.interp(probe, sample_times, columns))
            for det in detections[index].detections:
                if det.label in skip:
                    continue
                x1, y1, x2, y2 = det.box
                x1, x2 = x1 / det_w, x2 / det_w
                y1, y2 = y1 / det_h, y2 / det_h
                if x2 < column - radius or x1 > column + radius:
                    continue
                if abs(y2 - g) > 0.18:
                    continue
                width_factor = float(np.clip((x2 - x1) / 0.06, 0.45, 1.0))
                required = (
                    min(0.45 * max(g - y1, 0.0), 0.85 * entry["_height"]) * width_factor
                )
                out.append((probe, g, required))
        return out

    for entry in entries:
        if entry.get("type") != "jump":
            continue
        entry["_height"] = float(
            np.clip(entry.get("height", 0.0) or 0.22, 0.12, 0.50)
        )

        air_time = float(entry.get("airTime", 0.0) or 1.0)
        for attempt in range(8):
            occupied = occupancy(entry, air_time)
            if not occupied:
                break

            def lift(tap: float, t: float) -> float:
                phase = (t - tap) / air_time
                if phase <= 0 or phase >= 1:
                    return 0.0
                return entry["_height"] * 4.0 * phase * (1.0 - phase)

            candidates = np.arange(
                occupied[-1][0] - air_time + 0.05, occupied[0][0] + 0.01, 1.0 / 60.0
            )
            ok = np.array(
                [
                    all(lift(tap, t) >= req + 0.03 for t, _, req in occupied)
                    for tap in candidates
                ]
            )
            if ok.any():
                good = candidates[ok]
                centre = float(good.mean())
                half = max(float(good.max() - good.min()) * 0.5, 0.10)
                moved = abs(centre - entry["time"])
                entry["time"] = centre
                entry["window"] = round(half * 2, 3)
                # Re-centring moves the peak the occupancy was measured around;
                # fit once more at the new centre so the two agree.
                if moved > 0.05 and attempt < 7:
                    continue
                break

            # Nothing satisfies: lengthen the arc and try again.
            air_time = min(air_time * 1.2, 3.4)
            entry["airTime"] = round(air_time, 3)

        entry.pop("_height", None)


def _clamp_to_ground_region(
    surfaces: list,
    sample_times: np.ndarray,
    columns: np.ndarray,
    ground: np.ndarray,
    names: list[str],
) -> np.ndarray:
    """Keep the ball inside the visible ground region on ground surfaces.

    The segmented line is the region's *far edge*; anywhere at or below it is
    ground. A resting ball above it has nothing visible under it - the mid-air
    look - so such samples are pulled down to just inside the region. The edge
    is a temporal median over a small window, because a single segmentation
    frame is too noisy to move the character by.
    """
    from .segment import NO_GROUND

    times = np.array([s.time for s in surfaces])
    result = ground.copy()

    for index, (time, column) in enumerate(zip(sample_times, columns)):
        if names[index] not in GROUND_SURFACES:
            continue

        near = np.flatnonzero(np.abs(times - time) <= 0.25)
        edges = []
        for frame in near:
            surface = surfaces[frame]
            if not surface.is_plausible_ground:
                continue
            pixel = int(np.clip(column * surface.width, 0, surface.width - 1))
            window = surface.run_line[max(0, pixel - 25) : pixel + 26]
            window = window[window != NO_GROUND]
            if window.size:
                edges.append(float(np.median(window)) / surface.height)

        if len(edges) < 2:
            continue

        # A wall passage makes the edge flap between open ground and a bottom
        # sliver; the median of a bimodal sample describes nothing that exists,
        # so inconsistent evidence is no evidence.
        if max(edges) - min(edges) > 0.15:
            continue

        edge = float(np.median(edges))
        if edge > EDGE_USABLE:
            continue  # region too thin to judge against

        if result[index] < edge - 0.02:
            result[index] = edge - 0.02

    return result


def _snap_to_ledges(
    path: Path,
    marker: MarkerTrack,
    sample_times: np.ndarray,
    columns: np.ndarray,
    ground: np.ndarray,
    names: list[str],
) -> tuple[np.ndarray, list[str]]:
    """Put the character on the surface it is actually running along.

    The marker says roughly where the designer wanted it; the extracted ledges
    say exactly where a railing or hedge top is. Where the two agree to within
    LEDGE_SNAP, the ledge wins - it is measured per frame and tracks perspective,
    which a hand-drawn path cannot.

    Whichever candidate is nearest the marker is chosen, so the surface is
    identified rather than assumed: the character rides the rail while the
    marker is on the rail, and the hedge while it is on the hedge.
    """
    times, lines, width, height = load_ledges(path)

    result = ground.copy()
    chosen = list(names)
    measured = np.full(sample_times.size, np.nan)

    for index, (time, column) in enumerate(zip(sample_times, columns)):
        if time > marker.animated_until:
            continue

        # The timeline names the surface the character is meant to be on, which
        # geometry cannot infer: standing on a hedge and passing in front of one
        # look identical from a top edge alone. Only that surface's ledge is
        # considered; the ledge then supplies the exact height.
        wanted = names[index]
        if wanted not in lines:
            continue

        frame = int(np.argmin(np.abs(times - time)))
        if abs(times[frame] - time) > 0.5:
            continue

        pixel = int(np.clip(column * width, 0, width - 1))
        window = lines[wanted][frame, max(0, pixel - 20) : pixel + 21]
        window = window[window != NO_GROUND]
        if window.size == 0:
            continue

        value = float(np.median(window)) / height

        # Sanity-gate against the local resting path, never the raw marker: the
        # marker mid-jump is nowhere near any ledge, and gating on it left the
        # hedge unsnapped exactly where its sign-jumps happen. The gate only
        # rejects wild segmentation frames.
        low = int(np.searchsorted(sample_times, time - 0.5))
        high = int(np.searchsorted(sample_times, time + 0.5))
        local = float(np.median(ground[max(0, low) : max(high, low + 1)]))
        if abs(value - local) < 0.35:
            measured[index] = value

    # The ledge is detected in under half the frames, and leaving the resting
    # path in the gaps had the ball hopping between the hedge top and a line a
    # sixth of a frame below it. Within each named span, interpolate the
    # measured ledge across its gaps: the surface is continuous even when its
    # detection is not.
    index = 0
    total = sample_times.size
    while index < total:
        wanted = names[index]
        if wanted not in lines:
            index += 1
            continue

        end = index
        while end + 1 < total and names[end + 1] == wanted:
            end += 1

        span = slice(index, end + 1)
        span_measured = measured[span]
        known = np.isfinite(span_measured)
        if known.sum() >= 2:
            positions = np.arange(span_measured.size)
            result[span] = np.interp(positions, positions[known], span_measured[known])
            for offset in range(index, end + 1):
                chosen[offset] = wanted

        index = end + 1

    return result, chosen


def _apply_marker(
    marker: MarkerTrack,
    sample_times: np.ndarray,
    ground: np.ndarray,
    columns: np.ndarray,
    post_column: float = 0.35,
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

    # The column eases to its post-marker home BEFORE the marker ends,
    # overriding the marker's final drift: the drawn dot parks at the far left
    # edge, and following it there made the chain jump look like the ball was
    # arriving from the side. Easing over [end-2.3, end-0.3] means the column
    # is already settled when the chain arc fires.
    post = float(np.clip(post_column, MIN_COLUMN, MAX_COLUMN))
    ease = np.clip(
        (sample_times - (marker.animated_until - 2.3)) / 2.0, 0.0, 1.0
    )
    ease = ease * ease * (3.0 - 2.0 * ease)
    column = sampled_column * (1.0 - ease) + post * ease

    return (
        ground * (1.0 - weight) + sampled_ground * weight,
        column,
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
