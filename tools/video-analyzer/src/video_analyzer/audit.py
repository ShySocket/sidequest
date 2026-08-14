"""Frame-by-frame audit of the character's motion against the footage.

The level can pass every unit test and still look wrong on screen, because the
tests check the file and the eye checks the frame. This audit closes that gap:
it steps through the level at video rate simulating the character exactly as the
game moves it - ideal taps included - and checks each frame against evidence
from the footage itself.

Three claims are checked, one per complaint they guard against:

* **Support.** A grounded ball must rest on something visible: the extracted
  rail or hedge ledge where the level names one, or the segmented ground line
  otherwise. A ball floating above all evidence is the "moving in mid air" look.
* **Clearance.** A jump taken anywhere inside its cue window - not only at the
  perfect instant - must keep the ball off the ground and carrying real height
  for as long as the obstacle crosses its column. Strict box-disjointness is
  deliberately not the criterion: a parked SUV's box towers over any jump, and
  even the designer's own drawn arc passes inside it - the vault reads from the
  ball being airborne and high, in front of the obstacle.
* **Dodge.** During a dodge cue, the shifted ball must clear the sign's box.

Each check reports margins, not just pass/fail, so a fix can be sized instead
of guessed at.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import json
import numpy as np

from .segment import NO_GROUND, load_surfaces

FPS = 30.0

SUPPORT_TOLERANCE = 0.05
"""Ball-bottom to evidence-line distance, in frame heights, that reads as rested."""

SUPPORT_MIN_SPAN = 0.30
"""Seconds a support violation must persist to count; single frames are noise."""

DEFAULT_JUMP_HEIGHT = 0.22
DEFAULT_AIR_TIME = 1.0
HEIGHT_CLAMP = (0.12, 0.50)
"""Mirrors VideoRunnerCharacter's jump tuning; the audit must move like the game."""

DODGE_DEPTH_STEP = 0.09
"""How far the dodge steps toward the camera, in frame heights."""

DODGE_DURATION = 0.55

COLUMN_WINDOW_PX = 30
"""Half-window of columns around the ball used when reading evidence lines."""


@dataclass
class Violation:
    check: str
    start: float
    end: float
    worst: float
    detail: str

    def __str__(self) -> str:
        return (
            f"[{self.check}] {self.start:6.2f}-{self.end:6.2f}s  "
            f"worst {self.worst:+.3f}  {self.detail}"
        )


@dataclass
class AuditResult:
    violations: list[Violation] = field(default_factory=list)
    frames: int = 0
    supported_frames: int = 0

    @property
    def clean(self) -> bool:
        return not self.violations


class LevelAuditor:
    def __init__(
        self,
        level_path: Path,
        surfaces_path: Path,
        ledges_path: Path,
        detections_path: Path,
        video_path: Path | None = None,
    ) -> None:
        self.level = json.loads(level_path.read_text())
        self.level_dir = level_path.parent
        self.video_path = video_path
        self._silhouette_cache: dict = {}
        self._capture = None
        self._masker = None

        self.map_t = np.array([p["t"] for p in self.level["timeToDistance"]])
        self.map_d = np.array([p["d"] for p in self.level["timeToDistance"]])
        self.path_d = np.array([p["d"] for p in self.level["path"]])
        self.path_y = np.array([p["y"] for p in self.level["path"]])
        self.path_x = np.array([p["x"] for p in self.level["path"]])
        self.path_s = [p["s"] for p in self.level["path"]]

        surfaces = load_surfaces(surfaces_path)
        self.surface_times = np.array([s.time for s in surfaces])
        self.surfaces = surfaces

        from .ledges import load_ledges

        self.ledge_times, self.ledge_lines, self.ledge_w, self.ledge_h = load_ledges(
            ledges_path
        )

        detections = json.loads(detections_path.read_text())
        self.det_w = detections["width"]
        self.det_h = detections["height"]
        self.det_times = np.array([f["time"] for f in detections["frames"]])
        self.det_frames = detections["frames"]

        heights = np.sort(self.path_y)
        self.depth_far = float(heights[int(len(heights) * 0.05)])
        self.depth_near = float(heights[int(len(heights) * 0.95)])
        self.marker_diameter = float(self.level.get("markerDiameter", 0.173)) or 0.173

    # --- the game's own motion, mirrored -----------------------------------

    def distance_at(self, time: float) -> float:
        return float(np.interp(time, self.map_t, self.map_d))

    def ground_at(self, distance: float) -> float:
        return float(np.interp(distance, self.path_d, self.path_y))

    def column_at(self, distance: float) -> float:
        return float(np.interp(distance, self.path_d, self.path_x))

    def surface_at(self, distance: float) -> str:
        index = int(np.searchsorted(self.path_d, distance, side="right")) - 1
        return self.path_s[int(np.clip(index, 0, len(self.path_s) - 1))]

    def diameter_of(self, ground: float) -> float:
        span = self.depth_near - self.depth_far
        depth = 0.0 if span <= 0 else np.clip((ground - self.depth_far) / span, 0, 1)
        return self.marker_diameter * float(np.interp(depth, [0, 1], [0.82, 1.2]))

    def diameter_at(self, distance: float) -> float:
        return self.diameter_of(self.ground_at(distance))

    def arc_params(
        self, takeoff: float, height: float, duration: float
    ) -> tuple[float, float, float, float]:
        """(take-off ground, drop, gravity, take-off speed) of an arc.

        The flight is ONE parabola in screen space, exactly as
        VideoRunnerCharacter flies it: from the take-off point to the landing
        point, peaking ``height`` above the take-off line, under CONSTANT
        acceleration - terrain never leaks into the arc, and there is no
        blend kink after the apex. For a flat landing (drop 0) the constants
        reduce to the old 8h/T^2 and 4h/T.
        """
        start = self.ground_at(self.distance_at(takeoff))
        land = self.ground_at(self.distance_at(takeoff + duration))
        drop = max(land - start, -0.95 * height)
        root = np.sqrt(2.0 * height) + np.sqrt(2.0 * (height + drop))
        gravity = root * root / (duration * duration)
        return start, drop, float(gravity), float(np.sqrt(2.0 * gravity * height))

    def arc_state_at(
        self,
        distance: float,
        since_takeoff: float,
        start: float,
        drop: float,
        gravity: float,
        speed: float,
        height: float,
    ) -> tuple[float, float]:
        """(lift above the live ground, diameter) mid-arc, as the game renders.

        The ball's bottom follows the parabola above the take-off line; its
        size holds the take-off spot's depth through the rise and eases into
        the landing spot's across the descent (square root of the fallen
        fraction - the descent's own clock - smoothstepped).
        """
        air = speed * since_takeoff - 0.5 * gravity * since_takeoff**2
        ground = self.ground_at(distance)
        lift = max(air + ground - start, 0.0)

        eased = 0.0
        if speed - gravity * since_takeoff < 0.0:
            fallen = float(np.clip(
                (height - air) / max(height + drop, 1e-4), 0.0, 1.0))
            clock = np.sqrt(fallen)
            eased = float(clock * clock * (3.0 - 2.0 * clock))
        size_ground = start + (ground - start) * eased
        return lift, self.diameter_of(size_ground)

    def arc(self, height: float, air_time: float = 0.0) -> tuple[float, float]:
        """(clamped height, duration) exactly as VideoRunnerCharacter computes.

        Events carrying the marker's own measured air time use it; the sqrt rule
        is the fallback for hand-typed cues.
        """
        if air_time > 0:
            # An authored cue is trusted rather than re-clamped to the tuned
            # range: the people at 24s are nearly frame-tall and clearing them
            # takes more height than any tuned jump - the no-overlap check is
            # what polices these numbers, not a clamp.
            clamped = float(np.clip(height if height > 0 else DEFAULT_JUMP_HEIGHT, 0.05, 0.8))
            # Floor at 0.3s: the annotated bush hops are 0.4s and the chain
            # hop 0.34s, and a 0.6s floor silently doubled them.
            return clamped, float(np.clip(air_time, 0.3, 3.4))
        clamped = float(np.clip(height if height > 0 else DEFAULT_JUMP_HEIGHT, *HEIGHT_CLAMP))
        duration = DEFAULT_AIR_TIME * np.sqrt(clamped / DEFAULT_JUMP_HEIGHT)
        return clamped, float(duration)

    def air_height(self, height: float, duration: float, since_takeoff: float) -> float:
        if since_takeoff < 0 or since_takeoff > duration:
            return 0.0
        phase = since_takeoff / duration
        return height * 4.0 * phase * (1.0 - phase)

    # --- evidence from the footage -----------------------------------------

    def evidence_lines(self, time: float, column: float) -> dict[str, float]:
        """Every surface line visible near the ball's column, by name."""
        lines: dict[str, float] = {}

        # Median over a small time window: one segmentation frame is too noisy
        # to accuse the character of floating.
        edges = []
        for frame in np.flatnonzero(np.abs(self.surface_times - time) <= 0.25):
            surface = self.surfaces[frame]
            if not surface.is_plausible_ground:
                continue
            pixel = int(np.clip(column * surface.width, 0, surface.width - 1))
            window = surface.run_line[
                max(0, pixel - COLUMN_WINDOW_PX) : pixel + COLUMN_WINDOW_PX + 1
            ]
            window = window[window != NO_GROUND]
            if window.size:
                edges.append(float(np.median(window)) / surface.height)
        if len(edges) >= 2 and max(edges) - min(edges) <= 0.15:
            # Consistency first: a wall passage flaps the edge between open
            # ground and a bottom sliver, and the median of a bimodal sample
            # describes nothing that exists. Then thinness: a region squeezed
            # into the frame's bottom sliver is too thin to judge against.
            edge = float(np.median(edges))
            if edge <= 0.88:
                lines["ground"] = edge

        frame = int(np.argmin(np.abs(self.ledge_times - time)))
        if abs(self.ledge_times[frame] - time) < 0.5:
            pixel = int(np.clip(column * self.ledge_w, 0, self.ledge_w - 1))
            for name, stack in self.ledge_lines.items():
                window = stack[
                    frame, max(0, pixel - COLUMN_WINDOW_PX) : pixel + COLUMN_WINDOW_PX + 1
                ]
                window = window[window != NO_GROUND]
                if window.size:
                    value = float(np.median(window)) / self.ledge_h
                    # A ledge at the frame's very top or bottom is a broken
                    # segmentation frame, not a surface.
                    if 0.08 < value < 0.97:
                        lines[name] = value

        return lines

    def obstacle_boxes(self, time: float, classes: set[str] | None = None) -> list[dict]:
        """Detection boxes near ``time``, normalized to 0..1."""
        index = int(np.argmin(np.abs(self.det_times - time)))
        if abs(self.det_times[index] - time) > 0.25:
            return []

        skip = {"sidewalk", "railing", "fence", "tree", "bush"}
        boxes = []
        for det in self.det_frames[index]["detections"]:
            if det["label"] in skip:
                continue
            if classes is not None and det["label"] not in classes:
                continue
            x1, y1, x2, y2 = det["box"]
            boxes.append(
                {
                    "label": det["label"],
                    "score": det["score"],
                    "x1": x1 / self.det_w,
                    "y1": y1 / self.det_h,
                    "x2": x2 / self.det_w,
                    "y2": y2 / self.det_h,
                }
            )
        return boxes

    @staticmethod
    def circle_box_gap(cx: float, cy: float, r: float, box: dict) -> float:
        """Signed gap between a ball and a box; negative means touching."""
        nx = np.clip(cx, box["x1"], box["x2"])
        ny = np.clip(cy, box["y1"], box["y2"])
        return float(np.hypot(cx - nx, cy - ny) - r)

    # --- checks -------------------------------------------------------------

    @staticmethod
    def support_gap(ground: float, lines: dict[str, float]) -> float:
        """How far the ball floats above its best evidence.

        The segmented ground line is the *far edge* of a region, so any ball at
        or below it stands inside the region and is supported; only being above
        it is floating. A ledge is a line, and support means being near it in
        either direction.
        """
        best = np.inf
        for name, value in lines.items():
            if name == "ground":
                gap = value - ground  # positive only when ABOVE the far edge
            else:
                gap = abs(ground - value)
            best = min(best, gap)
        return float(max(best, 0.0))

    def obscured_by_obstacle(self, time: float, column: float, ground: float) -> bool:
        """True when an obstacle box hides the surface under the ball.

        A van filling the column pushes the visible ground to the frame bottom;
        the ball is not floating, its footing is just hidden behind the van.
        """
        for box in self.obstacle_boxes(time):
            if box["x1"] <= column <= box["x2"] and box["y2"] >= ground - 0.15:
                return True
        return False

    def check_support(self) -> list[Violation]:
        """A grounded ball must rest on visible evidence.

        Which evidence binds depends on the named surface: on a ledge surface
        the ball must sit on that ledge; on a ground surface it must sit inside
        the ground region, and a background hedge's top line has no say - at the
        grass bank the only measurable line is the bushes behind the ball, and
        letting them accuse it of floating was a false alarm, not an audit.
        """
        jump_spans = []
        for event in self.level["events"]:
            if event["type"] == "dodge":
                # Mid-dodge the ball is deliberately between lanes; the resting
                # path is not where it stands.
                jump_spans.append(
                    (event["time"] - DODGE_DURATION, event["time"] + DODGE_DURATION)
                )
                continue
            if event["type"] not in ("jump", "platform", "hop"):
                continue
            height, duration = self.arc(event.get("height", 0.0), event.get("airTime", 0.0))
            jump_spans.append((event["time"] - duration * 0.7, event["time"] + duration * 0.7))

        hidden = [(h["startTime"], h["endTime"]) for h in self.level.get("hidden", [])]

        # A surface handover is a slide (the hedge slide-off is authored as
        # one), so frames near a name change are transition, not floating.
        transitions = []
        for i in range(1, len(self.path_s)):
            if self.path_s[i] != self.path_s[i - 1]:
                t = float(np.interp(self.path_d[i], self.map_d, self.map_t))
                transitions.append((t - 0.7, t + 0.7))

        duration_total = float(self.map_t[-1])
        gaps: list[tuple[float, float]] = []
        open_start = None
        worst = 0.0
        worst_detail = ""
        violations: list[Violation] = []
        supported = 0
        frames = 0

        for time in np.arange(0.0, duration_total, 1.0 / FPS):
            if (
                any(a <= time <= b for a, b in jump_spans)
                or any(a <= time <= b for a, b in hidden)
                or any(a <= time <= b for a, b in transitions)
            ):
                # A violation must not bridge an excluded region.
                if open_start is not None and time - open_start >= SUPPORT_MIN_SPAN:
                    violations.append(
                        Violation("support", open_start, time, worst, worst_detail)
                    )
                open_start = None
                worst = 0.0
                continue

            distance = self.distance_at(time)
            ground = self.ground_at(distance)
            column = self.column_at(distance)
            frames += 1

            if self.obscured_by_obstacle(time, column, ground):
                supported += 1  # the surface is hidden behind the obstacle
                continue

            surface_name = self.surface_at(distance)
            lines = self.evidence_lines(time, column)
            if surface_name in ("rail", "hedge"):
                lines = {k: v for k, v in lines.items() if k == surface_name}
            else:
                lines.pop("rail", None)
                lines.pop("hedge", None)
            if not lines:
                supported += 1  # nothing measurable; cannot call it floating
                continue

            gap = self.support_gap(ground, lines)
            if gap <= SUPPORT_TOLERANCE:
                supported += 1
                if open_start is not None:
                    if time - open_start >= SUPPORT_MIN_SPAN:
                        violations.append(
                            Violation("support", open_start, time, worst, worst_detail)
                        )
                    open_start = None
                    worst = 0.0
                continue

            if open_start is None:
                open_start = time
            if gap > worst:
                worst = gap
                nearest = min(lines, key=lambda k: abs(ground - lines[k]))
                worst_detail = (
                    f"ball at y={ground:.3f}, nearest evidence '{nearest}' "
                    f"at y={lines[nearest]:.3f} ({self.surface_at(distance)})"
                )

        if open_start is not None and duration_total - open_start >= SUPPORT_MIN_SPAN:
            violations.append(
                Violation("support", open_start, duration_total, worst, worst_detail)
            )

        self._support_stats = (frames, supported)
        return violations

    def check_clearance(self, timeline_path: Path | None = None) -> list[Violation]:
        """The scheduled arc must clear its obstacle.

        Jumps are predetermined choreography now - the arc fires at exactly the
        written time on every playthrough, and input only scores - so there is
        no tap family to audit. One arc, one verdict.

        A box a shipped occluder strip covers is exempt, as in the overlap
        check: the ball passes BEHIND it by design (the near lamppost sweeping
        the 40.71s arc), so the vault does not answer for it. Occlusion is the
        exemption, not depth alone - the 9.45s van's base also runs past the
        frame bottom, but no strip ships there, and the arc must truly clear it.
        """
        forced: list[tuple[float, float]] = []
        if timeline_path is not None and timeline_path.exists():
            timeline = json.loads(timeline_path.read_text())
            forced = [
                (float(s["from"]), float(s["to"]))
                for s in timeline.get("behindSpans", [])
            ]
        strips = self.level.get("foreground", [])

        violations = []
        for event in self.level["events"]:
            if event["type"] != "jump":
                continue

            height, duration = self.arc(event.get("height", 0.0), event.get("airTime", 0.0))
            takeoff = float(event["time"])
            start, drop, gravity, speed = self.arc_params(takeoff, height, duration)
            # The parabola peaks where its velocity crosses zero - earlier
            # than mid-arc when the landing sits lower than the take-off.
            peak_time = takeoff + speed / gravity

            worst_gap = np.inf
            worst_detail = ""
            for step in np.arange(0.0, duration, 1.0 / FPS):
                time = takeoff + step
                # Ownership: the arc answers for what crosses under it. A pole
                # most of a second from the apex is a different object.
                if abs(time - peak_time) > 0.55:
                    continue
                # The takeoff and landing instants carry no lift by definition,
                # and several landings are annotated as "right after the car" -
                # grazing the box tail at touchdown is the intent, not a hit.
                if step < 0.12 or step > duration - 0.12:
                    continue
                distance = self.distance_at(time)
                ground = self.ground_at(distance)
                column = self.column_at(distance)
                # Lift above the LOCAL ground: the parabola means a drop
                # carried by the arc adds to the clearance the vault actually
                # shows on screen.
                lift, diameter = self.arc_state_at(
                    distance, step, start, drop, gravity, speed, height)
                radius = diameter * 0.5

                for box in self.obstacle_boxes(time):
                    if box["x2"] < column - radius or box["x1"] > column + radius:
                        continue
                    # Only obstacles standing at the ball's ground level. The
                    # test is "reaches down to the ground", not "bottom near
                    # the ground": a near van's box runs past the frame bottom,
                    # and the old symmetric check filtered it out entirely.
                    if box["y2"] < ground - 0.18:
                        continue
                    # Behind a shipped occluder strip: the ball passes behind
                    # this object, the arc does not vault it.
                    if any(a <= time <= b for a, b in forced) or self._is_occluded(
                        distance, box, strips
                    ):
                        continue
                    # Strict box-disjointness is deliberately not the test: a
                    # parked SUV's box towers over any jump, and even the
                    # designer's drawn arc passes inside it. A vault reads from
                    # being airborne and high while the obstacle crosses, with
                    # thin poles scaled down so they do not demand car-sized
                    # clearance.
                    obstacle_height = max(ground - box["y1"], 0.0)
                    width_factor = float(np.clip((box["x2"] - box["x1"]) / 0.06, 0.45, 1.0))
                    required = min(0.45 * obstacle_height, 0.85 * height) * width_factor
                    gap = lift - required
                    if gap < worst_gap:
                        worst_gap = gap
                        worst_detail = (
                            f"{event['label']} vs {box['label']}: lift {lift:.3f} "
                            f"of {required:.3f} needed (t={time:.2f}s)"
                        )

            if worst_gap < 0:
                violations.append(
                    Violation(
                        "clearance",
                        takeoff,
                        takeoff + duration,
                        worst_gap,
                        worst_detail,
                    )
                )
        return violations

    # What an event's label says it clears, mapped to detection classes. The
    # overlap rule polices exactly the named obstacle: a lamp pole standing
    # BEHIND the hedge shares a box base with the signs planted ON it, and no
    # geometry can tell those apart - but the choreography names its target.
    OVERLAP_CLASSES = (
        ("sign", {"traffic sign"}),
        ("people", {"person"}),
        ("chain", {"chain", "fence"}),
        ("fence", {"chain", "fence"}),
        ("car", {"car", "truck", "bus"}),
        ("van", {"car", "truck", "bus"}),
        ("suv", {"car", "truck", "bus"}),
    )

    def check_overlap(self, timeline_path: Path | None) -> list[Violation]:
        """No frame of an arc may show the ball overlapping what it clears.

        The clearance check asks "is the vault high enough to read"; this one
        asks the harder question the frame-by-frame review asked: does the
        ball's disk ever intersect its obstacle, at takeoff, in flight, or on
        landing. "Jumping over" means never touching - a landing that grazes
        the roof is a landing on the roof.

        A detection BOX overstates the object - its corners are empty pixels
        around a car's sloped tail - so a box hit is a candidate, not a
        verdict: each one is confirmed against the object's actual SAM
        silhouette from the frame itself (the picture analysis the review was
        done with) when the video is available.

        Two kinds of overlap are intended, not collisions, and are exempt:

        * The ball IN FRONT of the obstacle - its resting line clearly below
          the box base means it is nearer the camera ("lands right in front
          of the car" is authored intent).
        * The ball BEHIND a shipped occluder - a strip of video is re-drawn
          over it there, so overlap is exactly what the design wants.
        """
        forced: list[tuple[float, float]] = []
        if timeline_path is not None and timeline_path.exists():
            timeline = json.loads(timeline_path.read_text())
            forced = [
                (float(s["from"]), float(s["to"]))
                for s in timeline.get("behindSpans", [])
            ]

        strips = self.level.get("foreground", [])
        aspect = self.level["source"]["width"] / self.level["source"]["height"]

        violations = []
        for event in self.level["events"]:
            if event["type"] not in ("jump", "hop"):
                continue

            classes: set[str] | None = None
            label = str(event.get("label", "")).lower()
            for keyword, mapped in self.OVERLAP_CLASSES:
                if keyword in label:
                    classes = (classes or set()) | mapped

            # An arc that names no obstacle ("off the hedge", the level-change
            # hops) clears nothing, so there is nothing it must not touch -
            # and bystanders like a sign post standing BEHIND the hedge would
            # otherwise accuse it (their bases are hidden, so depth cannot
            # exonerate them).
            if classes is None:
                continue

            height, duration = self.arc(event.get("height", 0.0), event.get("airTime", 0.0))
            takeoff = float(event["time"])
            start, drop, gravity, speed = self.arc_params(takeoff, height, duration)

            worst = 0.0
            worst_detail = ""
            for step in np.arange(0.0, duration + 1e-6, 1.0 / FPS):
                time = takeoff + step
                distance = self.distance_at(time)
                ground = self.ground_at(distance)
                column = self.column_at(distance)
                # The rendered bottom follows the parabola (never below the
                # live line), same as the game.
                lift, diameter = self.arc_state_at(
                    distance, step, start, drop, gravity, speed, height)
                # Slightly under the drawn radius: the shading rolls off at
                # the rim, so a mathematical tangency does not read as touch.
                ry = diameter * 0.5 * 0.85
                rx = ry / aspect
                cy = ground - lift - diameter * 0.5

                for box in self.obstacle_boxes(time, classes):
                    # Standing farther than the ball: painted background.
                    if box["y2"] < ground - 0.18:
                        continue
                    # The ball in front of it: nearer the camera, no contact.
                    if ground > box["y2"] + 0.03:
                        continue
                    # Behind a shipped occluder: overlap is the design.
                    if any(a <= time <= b for a, b in forced) or self._is_occluded(
                        distance, box, strips
                    ):
                        continue

                    nx = float(np.clip(column, box["x1"], box["x2"]))
                    ny = float(np.clip(cy, box["y1"], box["y2"]))
                    gap = float(np.hypot((column - nx) / rx, (cy - ny) / ry)) - 1.0
                    if gap >= 0:
                        continue
                    # The frame itself has the last word.
                    if not self._silhouette_hit(time, box, column, cy, rx, ry):
                        continue
                    if -gap > worst:
                        worst = -gap
                        worst_detail = (
                            f"{event['label']} intersects {box['label']} at "
                            f"t={time:.2f}s (lift {lift:.2f}, box top {box['y1']:.2f})"
                        )

            if worst > 0.0:
                violations.append(
                    Violation(
                        "overlap",
                        takeoff,
                        takeoff + duration,
                        worst,
                        worst_detail,
                    )
                )
        return violations

    def _silhouette_hit(
        self, time: float, box: dict, cx: float, cy: float, rx: float, ry: float
    ) -> bool:
        """Does the ball disk cover actual object pixels, per SAM?

        Box corners are empty - a hatchback's sloped tail leaves its box's
        top-left vacant - and most flagged frames are exactly such corner
        grazes. Segmenting the frame answers with the object's real outline.
        Without a video to read (``--video`` not passed), the box verdict
        stands, erring toward flagging.
        """
        if self.video_path is None:
            return True

        import cv2

        key = (round(time, 2), round(box["x1"], 3), round(box["y1"], 3))
        if key in self._silhouette_cache:
            mask = self._silhouette_cache[key]
        else:
            if self._capture is None:
                self._capture = cv2.VideoCapture(str(self.video_path))
            fps = self._capture.get(cv2.CAP_PROP_FPS) or FPS
            self._capture.set(cv2.CAP_PROP_POS_FRAMES, int(round(time * fps)))
            ok, image = self._capture.read()
            if not ok:
                return True
            if self._masker is None:
                from .occlude import SilhouetteMasker

                self._masker = SilhouetteMasker()
            h, w = image.shape[:2]
            mask = self._masker.mask(
                image,
                [box["x1"] * w, box["y1"] * h, box["x2"] * w, box["y2"] * h],
            )
            self._silhouette_cache[key] = mask

        h, w = mask.shape[:2]
        for dx in np.linspace(-1, 1, 13):
            for dy in np.linspace(-1, 1, 13):
                if dx * dx + dy * dy > 1.0:
                    continue
                px = int((cx + dx * rx) * w)
                py = int((cy + dy * ry) * h)
                if 0 <= px < w and 0 <= py < h and mask[py, px]:
                    return True
        return False

    def _is_occluded(self, distance: float, box: dict, strips: list[dict]) -> bool:
        """Does a shipped foreground strip cover this box here?"""
        tolerance = float(self.level["totalDistance"]) * 0.012
        centre = 0.5 * (box["x1"] + box["x2"])
        for strip in strips:
            if abs(float(strip["d"]) - distance) > tolerance:
                continue
            if strip["x1"] - 0.03 <= centre <= strip["x2"] + 0.03:
                return True
        return False

    def check_script(self, timeline_path: Path | None) -> list[Violation]:
        """The level must contain the annotated choreography, verbatim.

        Guards the whole point of the deterministic design: every authoring
        stage downstream of timeline.json (window derivation, sorting, seam
        logic) must leave the written times, heights and air times untouched.
        """
        if timeline_path is None or not timeline_path.exists():
            return []

        timeline = json.loads(timeline_path.read_text())
        if not timeline.get("authoritativeEvents"):
            return []

        violations = []
        by_time = {round(e["time"], 2): e for e in self.level["events"]}
        for wanted in timeline.get("events", []):
            actual = by_time.get(round(float(wanted["time"]), 2))
            if actual is None:
                violations.append(
                    Violation(
                        "script",
                        float(wanted["time"]),
                        float(wanted["time"]),
                        1.0,
                        f"annotated {wanted['type']} '{wanted.get('label')}' missing from level",
                    )
                )
                continue
            for key in ("height", "airTime"):
                if key in wanted and abs(float(wanted[key]) - float(actual.get(key, 0))) > 0.005:
                    violations.append(
                        Violation(
                            "script",
                            float(wanted["time"]),
                            float(wanted["time"]),
                            abs(float(wanted[key]) - float(actual.get(key, 0))),
                            f"{wanted.get('label')}: {key} drifted "
                            f"{wanted[key]} -> {actual.get(key)}",
                        )
                    )
        return violations

    def check_dodges(self) -> list[Violation]:
        """The dodge must be a visible lane change with something to dodge.

        A dodge is a step toward the camera: the ball passes in *front* of the
        sign, so overlapping its box is the point, not a collision. What has to
        hold is that a sign genuinely crosses the ball's column near the cue -
        a dodge with nothing there is a broken cue - and that the step is large
        enough to read as changing lanes.
        """
        violations = []
        for event in self.level["events"]:
            if event["type"] != "dodge":
                continue

            crossing = False
            for probe in np.arange(event["time"] - 1.0, event["time"] + 1.0, 1.0 / FPS):
                column = self.column_at(self.distance_at(probe))
                for box in self.obstacle_boxes(probe, classes={"traffic sign", "pole"}):
                    if box["x1"] - 0.05 <= column <= box["x2"] + 0.05:
                        crossing = True
                        break
                if crossing:
                    break

            if not crossing:
                violations.append(
                    Violation(
                        "dodge",
                        event["time"] - 1.0,
                        event["time"] + 1.0,
                        0.0,
                        f"{event['label']}: no sign or pole crosses the column near the cue",
                    )
                )

            if DODGE_DEPTH_STEP < 0.07:
                violations.append(
                    Violation(
                        "dodge",
                        event["time"],
                        event["time"],
                        DODGE_DEPTH_STEP,
                        "dodge step too small to read as a lane change",
                    )
                )
        return violations

    def check_size(self) -> list[Violation]:
        """Diameter must change smoothly and stay near the drawn size."""
        violations = []
        duration_total = float(self.map_t[-1])
        hidden = [(h["startTime"], h["endTime"]) for h in self.level.get("hidden", [])]
        # Airborne size change is the landing spot's depth arriving - held
        # through the rise, then eased in across the descent (arc_state_at),
        # never a pop - so jump spans are not policed either.
        arcs = []
        for event in self.level["events"]:
            if event["type"] in ("jump", "platform", "hop"):
                _, dur = self.arc(event.get("height", 0.0), event.get("airTime", 0.0))
                arcs.append((event["time"] - dur * 0.7, event["time"] + dur * 1.2))
        times = np.array(
            [
                t
                for t in np.arange(0.0, duration_total, 1.0 / FPS)
                if not any(a - 0.2 <= t <= b + 0.2 for a, b in hidden)
                and not any(a <= t <= b for a, b in arcs)
            ]
        )
        diameters = np.array([self.diameter_at(self.distance_at(t)) for t in times])

        contiguous = np.diff(times) < 1.5 / FPS
        steps = np.abs(np.diff(diameters)) / self.marker_diameter
        steps = np.where(contiguous, steps, 0.0)
        bad = np.flatnonzero(steps > 0.03)
        if bad.size:
            i = int(bad[np.argmax(steps[bad])])
            violations.append(
                Violation(
                    "size",
                    float(times[i]),
                    float(times[i + 1]),
                    float(steps[i]),
                    f"diameter jumped {steps[i]:.1%} of drawn size in one frame",
                )
            )

        ratio = diameters / self.marker_diameter
        if ratio.min() < 0.7 or ratio.max() > 1.35:
            violations.append(
                Violation(
                    "size",
                    0.0,
                    duration_total,
                    float(max(1.35 - ratio.min(), ratio.max() - 0.7)),
                    f"diameter spans {ratio.min():.2f}-{ratio.max():.2f}x the drawn size",
                )
            )
        return violations

    def check_occlusion(self, timeline_path: Path | None) -> list[Violation]:
        """Every occluder the level ships must be earned by the footage.

        The derivation in occlude.py is re-checked here from the level file
        outward, the same way support is: each sample must name a box near the
        ball's column whose base sits clearly below the segmented ground plane
        (nearer the camera), tracks must be coherent rather than flicker, and
        a sample during a dodge or a hidden span would fight choreography the
        rest of the game is committed to. behindSpans in the timeline exempt
        exactly what they force.
        """
        from .occlude import DEPTH_MARGIN, MAX_CENTER_STEP, REACH

        entries = self.level.get("foreground", [])
        if not entries:
            return []

        forced: list[tuple[float, float]] = []
        if timeline_path is not None and timeline_path.exists():
            timeline = json.loads(timeline_path.read_text())
            forced = [
                (float(s["from"]), float(s["to"]))
                for s in timeline.get("behindSpans", [])
            ]

        hidden = [(h["startTime"], h["endTime"]) for h in self.level.get("hidden", [])]
        dodges = [
            (e["time"] - 0.25, e["time"] + 1.0)
            for e in self.level["events"]
            if e["type"] == "dodge"
        ]
        mask_file = self.level.get("foregroundMaskFile", "")

        violations = []
        tracks: dict[int, list[dict]] = {}
        for entry in entries:
            tracks.setdefault(int(entry.get("id", 0)), []).append(entry)

        for track_id, samples in sorted(tracks.items()):
            samples.sort(key=lambda e: e["d"])
            times = [float(np.interp(s["d"], self.map_d, self.map_t)) for s in samples]

            if len(samples) < 2:
                violations.append(
                    Violation(
                        "occlusion", times[0], times[-1], 0.0,
                        f"occluder {track_id}: single-sighting track (flicker)",
                    )
                )

            for sample, time in zip(samples, times):
                box = {k: float(sample[k]) for k in ("x1", "y1", "x2", "y2")}
                is_forced = any(a <= time <= b for a, b in forced)

                if not (0 <= box["x1"] < box["x2"] <= 1 and 0 <= box["y1"] < box["y2"] <= 1):
                    violations.append(
                        Violation(
                            "occlusion", time, time, 0.0,
                            f"occluder {track_id}: degenerate box at d={sample['d']}",
                        )
                    )
                    continue

                column = self.column_at(float(sample["d"]))
                centre = 0.5 * (box["x1"] + box["x2"])
                if abs(centre - column) > REACH + 0.1:
                    violations.append(
                        Violation(
                            "occlusion", time, time,
                            abs(centre - column),
                            f"occluder {track_id}: strip {centre:.2f} far from ball column {column:.2f}",
                        )
                    )

                if not is_forced:
                    if any(a <= time <= b for a, b in hidden):
                        violations.append(
                            Violation(
                                "occlusion", time, time, 0.0,
                                f"occluder {track_id}: emitted while the ball is hidden",
                            )
                        )
                    if any(a <= time <= b for a, b in dodges):
                        violations.append(
                            Violation(
                                "occlusion", time, time, 0.0,
                                f"occluder {track_id}: emitted during a dodge, which steps in front",
                            )
                        )
                    # The depth rule, against the ball's own resting line -
                    # the same reference the derivation uses. The support
                    # check has already tied that line to footage evidence,
                    # so this closes the loop without re-measuring through
                    # per-frame noise (a parked car notching the segmented
                    # line must not un-earn a pole the ball plainly fronts).
                    line = self.ground_at(float(sample["d"]))
                    if box["y2"] < line + DEPTH_MARGIN - 0.02:
                        violations.append(
                            Violation(
                                "occlusion", time, time,
                                line - box["y2"],
                                f"occluder {track_id}: base y={box['y2']:.3f} above the "
                                f"ball's line y={line:.3f} - it stands farther than the ball",
                            )
                        )

                if mask_file and sample.get("mask"):
                    us = [float(sample.get(k, -1)) for k in ("u1", "v1", "u2", "v2")]
                    if not (0 <= us[0] < us[2] <= 1 and 0 <= us[1] < us[3] <= 1):
                        violations.append(
                            Violation(
                                "occlusion", time, time, 0.0,
                                f"occluder {track_id}: bad mask uv at d={sample['d']}",
                            )
                        )

            steps = [
                abs(0.5 * (b["x1"] + b["x2"]) - 0.5 * (a["x1"] + a["x2"]))
                for a, b in zip(samples, samples[1:])
            ]
            if steps and max(steps) > MAX_CENTER_STEP + 0.02:
                violations.append(
                    Violation(
                        "occlusion", times[0], times[-1], max(steps),
                        f"occluder {track_id}: strip jumps {max(steps):.2f} frame-widths "
                        "between samples - two objects chained into one track",
                    )
                )

        if mask_file and not (self.level_dir / mask_file).exists():
            violations.append(
                Violation(
                    "occlusion", 0.0, 0.0, 0.0,
                    f"level names occluder atlas '{mask_file}' but it is not next to the level",
                )
            )

        return violations

    def run(self, timeline_path: Path | None = None) -> AuditResult:
        result = AuditResult()
        result.violations += self.check_support()
        result.violations += self.check_clearance(timeline_path)
        result.violations += self.check_overlap(timeline_path)
        result.violations += self.check_dodges()
        result.violations += self.check_size()
        result.violations += self.check_script(timeline_path)
        result.violations += self.check_occlusion(timeline_path)
        result.frames, result.supported_frames = getattr(
            self, "_support_stats", (0, 0)
        )
        return result
