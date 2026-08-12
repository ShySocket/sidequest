"""Where the ball passes behind foreground objects, measured rather than authored.

The believability trick lives in the game (VideoBackground.cs): a region of the
video is re-drawn in front of the ball, and because the re-drawn pixels are
identical to the pixels beneath them, the only visible effect is the ball
disappearing behind that region. This module decides WHERE and WHEN that
happens, and bakes a per-pixel silhouette so it happens with the object's
actual outline instead of its detection rectangle.

Both decisions are measured from the footage, so a new clip needs no authoring:

* **Depth.** Everything in a car-window clip stands on the same ground plane,
  so screen height is depth: an object whose base sits *below* the ball's
  ground line is nearer the camera, and the ball passes behind it. An object
  whose base sits above the line is farther away and must never occlude - that
  is the exact rule the rest of the pipeline already uses to order obstacles.
* **Overlap.** Only an object near the ball's own column can hide it, so a
  candidate must overlap the column the path says the ball occupies.

The timeline can still override the measurement (``behindSpans`` forces
occlusion through a failed depth test, ``frontSpans`` suppresses it), but they
exist for rescue, not routine: on the clip this project started with, the
derived spans reproduce the two spans that used to be hand-authored. Dodge
cues suppress occlusion automatically - a dodge is a deliberate step toward
the camera, in front of the very object a candidate box would name.

Rectangles alone read wrong: a detection box around a sign is much wider than
its post, so the ball used to vanish while visibly short of the metal. The
silhouette pass fixes that: SAM 2, prompted with each candidate box, returns
the object's actual pixels, and those are packed into a small grayscale atlas
the game samples as an alpha mask.

Everything in the atlas that is not silhouette is BLACK - the background, the
gutters between cells, and any cell whose segmentation failed. Black means
"occlude nothing": a wrong hole bitten out of the ball reads far worse than a
missed occlusion, so every failure mode must degrade to the ball staying
visible. (The first version used a white background and white failure cells;
bilinear sampling bled that white in at cell borders and carved a clean-edged
notch out of the ball with nothing visibly in front of it.)
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .detect import DetectionFrame
from .distance import DistanceMap

OCCLUDER_LABELS = ("pole", "traffic sign")
"""Thin, static, ground-planted things the ball believably slips behind.

Deliberately not cars or bushes: those are jump/ride obstacles whose
choreography already answers for them, and hiding the ball inside a jump
would read as a bug. ``occluderLabels`` in the timeline widens this per clip
(e.g. add "tree" where trunks cross the path).
"""

MIN_SCORE = 0.30
"""Detections below this are too unreliable to redraw the frame over."""

REACH = 0.25
"""Frame-widths either side of the ball's column a candidate may sit.

The strip is invisible until the ball overlaps it, so a generous reach costs
nothing on screen - and it is what keeps a *near* pole trackable: one sweeping
past the camera crosses the ball in under half a second, and a tight reach
sees it too few times to make a track (verified on the 41.5s pole, which a
0.12 reach reduced to two sightings).
"""

DEPTH_MARGIN = 0.03
"""How far below the ball's line a base must sit before it counts as nearer.

Screen height is only a depth proxy while everything shares one ground plane,
and the margin covers where that assumption frays: a grass bank lifts the
ball's line relative to the road plane (the 77.9s pole across the street), a
railing lifts it above the lot beyond (the 1.5s lamp post), and both read as
"barely below". Inside the margin the honest answer is "side by side", and a
ball wrongly swallowed reads far worse than a ball wrongly in front - so the
test demands the base be clearly below, not just not-above.
"""

PAD_X = 0.008
PAD_Y = 0.010
"""Widen emitted boxes slightly so the ball never pokes out of a tight box."""

MIN_TRACK_SECONDS = 0.15
"""An occluder needs two coherent sightings to be real.

Set just under twice the detection stride (5 frames at 30 fps): one sighting
is noise, but two continuity-checked ones are an object - and a near pole
sweeping past the camera does not stay visible much longer than that.
"""

MAX_TRACK_GAP = 0.45
"""Seconds between samples that still count as the same object."""

MAX_CENTER_SPEED = 0.9
"""Frame-widths per second a box centre may move and still be the same object.

The world sweeps past at up to ~0.5 frame-widths/s on the source clip;
anything faster than this is one pole handing off to another.
"""

MAX_CENTER_STEP = 0.18
"""Absolute cap on that movement between sightings, whatever the gap.

Without it, a long detection gap scales the speed allowance up far enough to
chain two neighbouring poles into one track - and the game would then sweep
one morphing strip between them instead of switching objects. 0.18 still
admits the fastest genuine mover measured (the 41.5s near pole, 0.16 between
sightings).
"""

DODGE_SUPPRESS_BEFORE = 0.25
DODGE_SUPPRESS_AFTER = 1.0
"""Window around a dodge cue where occlusion is suppressed.

A dodge steps the ball toward the camera, in front of the sign it dodges;
occluding it behind that same sign would undo the move on screen.
"""

MASK_CELL = (64, 128)
"""(width, height) each silhouette is resampled to in the atlas.

Occluders are tall and thin, so cells are too; the box-to-cell mapping is a
plain stretch, undone by the game when it samples.
"""

CELL_GUTTER = 2
"""Black texels around every cell.

The game samples the atlas bilinearly, so a texel at a cell's edge blends
with whatever sits beyond it. A black gutter makes that blend shrink the
silhouette by under a texel; anything else there would grow it into pixels
that occlude the ball with no object present.
"""

ATLAS_WIDTH = 1024


@dataclass(frozen=True)
class OcclusionConfig:
    labels: tuple[str, ...] = OCCLUDER_LABELS
    min_score: float = MIN_SCORE
    reach: float = REACH
    depth_margin: float = DEPTH_MARGIN
    pad_x: float = PAD_X
    pad_y: float = PAD_Y
    min_track_seconds: float = MIN_TRACK_SECONDS
    max_track_gap: float = MAX_TRACK_GAP
    max_center_speed: float = MAX_CENTER_SPEED
    max_center_step: float = MAX_CENTER_STEP
    force_spans: tuple[tuple[float, float], ...] = ()
    suppress_spans: tuple[tuple[float, float], ...] = ()

    @staticmethod
    def from_timeline(timeline) -> "OcclusionConfig":
        """Read the per-clip knobs and overrides out of a Timeline.

        Dodge cues become suppress spans here rather than downstream, so every
        consumer of the config gets the same behavior.
        """
        labels = tuple(timeline.occluder_labels or OCCLUDER_LABELS)
        force = tuple(
            (float(s["from"]), float(s["to"])) for s in (timeline.behind_spans or [])
        )
        suppress = [
            (float(s["from"]), float(s["to"])) for s in (timeline.front_spans or [])
        ]
        for event in timeline.events:
            if event.get("type") == "dodge":
                time = float(event["time"])
                suppress.append(
                    (time - DODGE_SUPPRESS_BEFORE, time + DODGE_SUPPRESS_AFTER)
                )
        # A hidden ball needs no occluder; emitting one just ships dead data.
        for span in timeline.hidden:
            suppress.append((float(span["from"]), float(span["to"])))
        return OcclusionConfig(
            labels=labels, force_spans=force, suppress_spans=tuple(suppress)
        )


@dataclass(frozen=True)
class OccluderSample:
    """One sighting of an occluder: when, and where its (padded) box sits."""

    time: float
    x1: float
    y1: float
    x2: float
    y2: float
    label: str

    @property
    def center(self) -> float:
        return 0.5 * (self.x1 + self.x2)


@dataclass
class OccluderTrack:
    """One physical object the ball passes behind, over its visible span."""

    id: int
    label: str
    samples: list[OccluderSample] = field(default_factory=list)

    @property
    def duration(self) -> float:
        return self.samples[-1].time - self.samples[0].time if self.samples else 0.0


def _in_any(time: float, spans) -> bool:
    return any(a <= time <= b for a, b in spans)


def derive_occluder_tracks(
    detection_frames: list[DetectionFrame],
    det_size: tuple[int, int],
    sample_times: np.ndarray,
    ground: np.ndarray,
    columns: np.ndarray,
    config: OcclusionConfig = OcclusionConfig(),
    elevated: np.ndarray | None = None,
) -> list[OccluderTrack]:
    """Every span where a detected object should hide the ball.

    ``ground`` is the ball's finished resting line and ``columns`` its finished
    x - depth orders candidates against where the ball actually is, and the
    path is the one line the game guarantees. The per-frame segmented line is
    deliberately NOT the reference: parked cars notch it toward the camera and
    a pole mid-lot then reads as "farther" than a ball it plainly fronts.

    ``elevated`` marks samples where the ball rides street furniture (a rail,
    a hedge top) rather than the ground. Furniture like that lines the *near*
    edge of what the camera sees - the designer put the ball on it precisely
    because it is prominent - so the ball is in front of everything planted
    beyond it, and no depth test against an invisible footing can prove
    otherwise. Occlusion is suppressed there unless a behindSpan forces it.

    Pure geometry over the cached detections and the path - no video access,
    no models - so it is cheap enough to run on every authoring pass and
    simple enough to test with synthetic boxes.
    """
    det_w, det_h = det_size
    raw: list[OccluderSample] = []

    for frame in detection_frames:
        time = float(frame.time)
        if _in_any(time, config.suppress_spans):
            continue
        forced = _in_any(time, config.force_spans)
        if (
            not forced
            and elevated is not None
            and bool(np.interp(time, sample_times, elevated.astype(np.float64)) > 0.5)
        ):
            continue

        column = float(np.interp(time, sample_times, columns))
        line = float(np.interp(time, sample_times, ground))

        best: OccluderSample | None = None
        for det in frame.detections:
            if det.label not in config.labels or det.score < config.min_score:
                continue
            x1, y1, x2, y2 = det.box
            x1, x2 = x1 / det_w, x2 / det_w
            y1, y2 = y1 / det_h, y2 / det_h
            # Centre distance, not box overlap: a frame-wide van box "overlaps"
            # a column half a screen from its body, and a strip anchored there
            # fails the very hygiene rule the audit holds strips to.
            if abs(0.5 * (x1 + x2) - column) > config.reach:
                continue
            # The depth rule: base clearly below the ball's line = nearer the
            # camera = the ball goes behind it. A forced span skips the test
            # (the designer has seen something the detector's box misplaces).
            if not forced and y2 < line + config.depth_margin:
                continue
            candidate = OccluderSample(
                time=time,
                x1=max(0.0, x1 - config.pad_x),
                y1=max(0.0, y1 - config.pad_y),
                x2=min(1.0, x2 + config.pad_x),
                y2=min(1.0, y2 + config.pad_y),
                label=det.label,
            )
            if best is None or abs(candidate.center - column) < abs(best.center - column):
                best = candidate

        if best is not None:
            raw.append(best)

    return _group_tracks(raw, config)


def _group_tracks(
    samples: list[OccluderSample], config: OcclusionConfig
) -> list[OccluderTrack]:
    """Chain sightings into per-object tracks, and drop the flicker.

    Continuity is the same argument the marker tracker makes: a real object's
    box centre moves at the speed the world sweeps past, so a jump faster than
    that is a different object, and a single lonely sighting is noise the game
    must never blink an occluder in and out for.
    """
    tracks: list[OccluderTrack] = []
    current: OccluderTrack | None = None

    for sample in samples:
        chains = (
            current is not None
            and sample.label == current.label
            and (gap := sample.time - current.samples[-1].time) <= config.max_track_gap
            and abs(sample.center - current.samples[-1].center)
            <= min(config.max_center_speed * max(gap, 1e-6), config.max_center_step)
        )
        if not chains:
            current = OccluderTrack(id=len(tracks) + 1, label=sample.label)
            tracks.append(current)
        current.samples.append(sample)  # type: ignore[union-attr]

    kept = [
        t
        for t in tracks
        if len(t.samples) >= 2 and t.duration >= config.min_track_seconds
    ]
    for index, track in enumerate(kept):
        track.id = index + 1
    return kept


# --- silhouette masks --------------------------------------------------------


@dataclass
class MaskAtlas:
    """Silhouettes for every track sample, packed into one grayscale image.

    ``uvs`` are in GL convention (v grows upward), matching what Unity's
    ``Texture2D.LoadImage`` produces, so the game applies them verbatim.
    """

    image: np.ndarray
    uvs: dict[tuple[int, int], tuple[float, float, float, float]]
    coverages: dict[tuple[int, int], float]
    file_name: str = ""


class SilhouetteMasker:
    """Lazy-loading SAM 2, prompted with one occluder box at a time."""

    def __init__(self, model_id: str = "facebook/sam2.1-hiera-small", device: str | None = None) -> None:
        self.model_id = model_id
        self._device = device
        self._model = None
        self._processor = None

    def _ensure_loaded(self) -> None:
        if self._model is not None:
            return

        import torch
        from transformers import Sam2Model, Sam2Processor

        if self._device is None:
            self._device = "mps" if torch.backends.mps.is_available() else "cpu"

        self._processor = Sam2Processor.from_pretrained(self.model_id)
        self._model = Sam2Model.from_pretrained(self.model_id).to(self._device).eval()

    def mask(self, image: np.ndarray, box_px: list[float]) -> np.ndarray:
        import cv2
        import torch

        self._ensure_loaded()
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        inputs = self._processor(  # type: ignore[union-attr]
            images=rgb, input_boxes=[[box_px]], return_tensors="pt"
        ).to(self._device)

        with torch.no_grad():
            outputs = self._model(**inputs, multimask_output=False)  # type: ignore[misc]

        masks = self._processor.post_process_masks(  # type: ignore[union-attr]
            outputs.pred_masks.cpu(), inputs["original_sizes"]
        )[0]
        return masks[0, 0].numpy() > 0


def _largest_component(mask: np.ndarray) -> np.ndarray:
    """Keep only the biggest connected blob.

    SAM prompted with a box occasionally grabs a scrap of background at the
    box corner; the object itself is always the dominant component.
    """
    import cv2

    count, labels = cv2.connectedComponents(mask.astype(np.uint8))
    if count <= 2:
        return mask
    sizes = np.bincount(labels.ravel())
    sizes[0] = 0
    return labels == int(np.argmax(sizes))


def bake_mask_atlas(
    video_info,
    tracks: list[OccluderTrack],
    *,
    analysis_width: int = 960,
    masker: SilhouetteMasker | None = None,
    progress=None,
) -> MaskAtlas:
    """SAM-segment every track sample and pack the silhouettes into an atlas.

    The atlas starts black and stays black everywhere a silhouette was not
    positively found - black occludes nothing, and every failure mode must
    leave the ball visible rather than bite a hole in it.
    """
    import cv2

    from .decode import iter_frames

    masker = masker or SilhouetteMasker()
    cell_w, cell_h = MASK_CELL
    pitch_w = cell_w + 2 * CELL_GUTTER
    pitch_h = cell_h + 2 * CELL_GUTTER
    per_row = ATLAS_WIDTH // pitch_w

    wanted: list[tuple[int, int, OccluderSample]] = [
        (track.id, index, sample)
        for track in tracks
        for index, sample in enumerate(track.samples)
    ]
    rows = (len(wanted) + per_row - 1) // per_row
    image = np.zeros((max(rows, 1) * pitch_h, ATLAS_WIDTH), dtype=np.uint8)
    uvs: dict[tuple[int, int], tuple[float, float, float, float]] = {}
    coverages: dict[tuple[int, int], float] = {}

    by_time: dict[float, list[int]] = {}
    for position, (_, _, sample) in enumerate(wanted):
        by_time.setdefault(round(sample.time, 3), []).append(position)

    tolerance = 0.51 / max(video_info.fps, 1.0)
    times = np.array(sorted(by_time))
    matched = 0

    for frame in iter_frames(video_info, analysis_width=analysis_width, stride=1):
        if not times.size or frame.time > times[-1] + tolerance:
            break
        nearest = float(times[int(np.argmin(np.abs(times - frame.time)))])
        if abs(nearest - frame.time) > tolerance:
            continue

        height, width = frame.image.shape[:2]
        for position in by_time.pop(nearest, []):
            track_id, sample_index, sample = wanted[position]
            box_px = [
                sample.x1 * width,
                sample.y1 * height,
                sample.x2 * width,
                sample.y2 * height,
            ]
            mask = masker.mask(frame.image, box_px)
            mask = _largest_component(mask)

            x1, y1 = int(box_px[0]), int(box_px[1])
            x2, y2 = max(int(box_px[2]), x1 + 1), max(int(box_px[3]), y1 + 1)
            crop = mask[y1:y2, x1:x2].astype(np.uint8) * 255
            coverage = float(crop.mean() / 255.0) if crop.size else 0.0
            coverages[(track_id, sample_index)] = coverage

            # An empty or wall-to-wall mask says the segmentation failed, not
            # that the object is invisible; the cell stays black and this
            # sighting occludes nothing.
            if 0.02 < coverage < 0.98:
                resized = cv2.resize(crop, MASK_CELL, interpolation=cv2.INTER_AREA)
                row, col = divmod(position, per_row)
                y0 = row * pitch_h + CELL_GUTTER
                x0 = col * pitch_w + CELL_GUTTER
                image[y0 : y0 + cell_h, x0 : x0 + cell_w] = resized
            matched += 1
            if progress is not None:
                progress.update(1)

        times = np.array(sorted(by_time))

    atlas_h = image.shape[0]
    for position, (track_id, sample_index, _) in enumerate(wanted):
        row, col = divmod(position, per_row)
        y0 = row * pitch_h + CELL_GUTTER
        x0 = col * pitch_w + CELL_GUTTER
        u1 = x0 / ATLAS_WIDTH
        u2 = (x0 + cell_w) / ATLAS_WIDTH
        # Image rows count down from the top; GL's v counts up from the bottom.
        v2 = 1.0 - y0 / atlas_h
        v1 = 1.0 - (y0 + cell_h) / atlas_h
        uvs[(track_id, sample_index)] = (u1, v1, u2, v2)

    return MaskAtlas(image=image, uvs=uvs, coverages=coverages)


# --- verification: the game's own strip logic, held against the footage -----


EDGE_HOLD_FRACTION = 0.5
"""How far past a track's end the last sighting may linger, as a fraction of
that track's own sample spacing.

MIRRORS VideoLevel.TryGetForeground - change both together. The bound is what
makes occlusion turn off *immediately*: with a generous hold the mask froze at
a stale position for up to half a second while the world swept on, and the
stale silhouette bit a hole in the ball with nothing visibly in front of it.
"""

SAME_OBJECT_GAP_FRACTION = 0.012
"""Max distance gap (fraction of total) still interpolated within one id.
MIRRORS VideoLevel.TryGetForeground (tolerance * 2)."""


def strip_at(distance: float, entries: list[dict], total_distance: float):
    """The strip the game would show at ``distance``, or None.

    A 1:1 mirror of VideoLevel.TryGetForeground so the verifier audits the
    exact behavior the player sees - change both together.
    """
    if not entries:
        return None

    after = 0
    while after < len(entries) and entries[after]["d"] < distance:
        after += 1
    before = after - 1

    a = entries[before] if before >= 0 else None
    b = entries[after] if after < len(entries) else None

    if (
        a is not None
        and b is not None
        and a["id"] == b["id"]
        and a["id"] != 0
        and b["d"] - a["d"] <= SAME_OBJECT_GAP_FRACTION * total_distance
    ):
        span = b["d"] - a["d"]
        alpha = min(max((distance - a["d"]) / span, 0.0), 1.0) if span > 0 else 0.0
        box = tuple(
            a[k] * (1 - alpha) + b[k] * alpha for k in ("x1", "y1", "x2", "y2")
        )
        return box, (a if alpha < 0.5 else b)

    def edge_hold(index: int) -> float:
        sample = entries[index]
        spacing = float("inf")
        if index > 0 and entries[index - 1]["id"] == sample["id"]:
            spacing = min(spacing, sample["d"] - entries[index - 1]["d"])
        if index + 1 < len(entries) and entries[index + 1]["id"] == sample["id"]:
            spacing = min(spacing, entries[index + 1]["d"] - sample["d"])
        return 0.0 if spacing == float("inf") else spacing * EDGE_HOLD_FRACTION

    nearest, gap = -1, float("inf")
    if a is not None:
        nearest, gap = before, distance - a["d"]
    if b is not None and b["d"] - distance < gap:
        nearest, gap = after, b["d"] - distance
    if nearest < 0 or gap > edge_hold(nearest):
        return None

    sample = entries[nearest]
    return (sample["x1"], sample["y1"], sample["x2"], sample["y2"]), sample


def verify_occlusion(
    level: dict,
    atlas: np.ndarray | None,
    detection_frames: list[DetectionFrame],
    det_size: tuple[int, int],
    labels: tuple[str, ...] = OCCLUDER_LABELS,
    fps: float = 30.0,
) -> tuple[list[str], dict]:
    """Prove the occlusion contract: behind a real object, or invisible.

    Two checks, both from the shipped artifacts outward:

    * **Atlas hygiene.** Every texel outside the cells' content rectangles
      must be black. Anything else bleeds into strip edges under bilinear
      sampling and occludes with no object present - the exact bug this
      verifier was written to catch.
    * **Phantom occlusion.** Step the level at video rate, run the *game's
      own* strip lookup and ball-overlap gate (mirrored above), rasterize
      which ball pixels the mask actually erases, and demand every erased
      pixel lie inside a detection box of an occluder class from the nearest
      detection frame (small slack for detection stride). An erased pixel
      with no object there is a hole bitten out of the ball.
    """
    import cv2

    entries = level.get("foreground", [])
    violations: list[str] = []
    stats = {"frames_checked": 0, "frames_occluding": 0, "worst_phantom": 0.0}
    if not entries:
        return violations, stats

    map_t = np.array([p["t"] for p in level["timeToDistance"]])
    map_d = np.array([p["d"] for p in level["timeToDistance"]])
    path_d = np.array([p["d"] for p in level["path"]])
    path_y = np.array([p["y"] for p in level["path"]])
    path_x = np.array([p["x"] for p in level["path"]])
    total = float(level["totalDistance"])
    frame_aspect = level["source"]["width"] / level["source"]["height"]

    heights = np.sort(path_y)
    depth_far = float(heights[int(len(heights) * 0.05)])
    depth_near = float(heights[int(len(heights) * 0.95)])
    marker = float(level.get("markerDiameter", 0.173)) or 0.173

    det_times = np.array([f.time for f in detection_frames])

    if atlas is not None:
        content = np.zeros(atlas.shape, dtype=bool)
        ah, aw = atlas.shape
        for entry in entries:
            if not entry.get("mask"):
                continue
            y1 = int(round((1 - entry["v2"]) * ah))
            y2 = int(round((1 - entry["v1"]) * ah))
            x1 = int(round(entry["u1"] * aw))
            x2 = int(round(entry["u2"] * aw))
            content[y1:y2, x1:x2] = True
        stray = int((atlas[~content] > 0).sum())
        if stray:
            violations.append(
                f"atlas: {stray} non-black texels outside silhouette cells - "
                "these bleed into strip edges and occlude with nothing there"
            )

    # The ball, exactly as the game computes it (mirrors VideoRunnerCharacter
    # and the audit's motion model).
    def air_height(time: float) -> float:
        for event in level["events"]:
            if event["type"] not in ("jump", "hop"):
                continue
            height = min(max(event.get("height") or 0.22, 0.05), 0.80)
            duration = event.get("airTime") or 1.0
            since = time - event["time"]
            if 0.0 <= since <= duration:
                phase = since / duration
                return height * 4.0 * phase * (1.0 - phase)
        return 0.0

    hidden = [(h["startTime"], h["endTime"]) for h in level.get("hidden", [])]
    duration = float(map_t[-1])
    disc = [
        (dx, dy)
        for dx in np.linspace(-1, 1, 15)
        for dy in np.linspace(-1, 1, 15)
        if dx * dx + dy * dy <= 1.0
    ]

    for time in np.arange(0.0, duration, 1.0 / fps):
        if any(a <= time <= b for a, b in hidden):
            continue
        distance = float(np.interp(time, map_t, map_d))
        shown = strip_at(distance, entries, total)
        stats["frames_checked"] += 1
        if shown is None:
            continue
        (x1, y1, x2, y2), sample = shown

        column = float(np.interp(distance, path_d, path_x))
        ground = float(np.interp(distance, path_d, path_y))
        span = depth_near - depth_far
        depth = 0.0 if span <= 0 else min(max((ground - depth_far) / span, 0), 1)
        diameter = marker * (0.82 + (1.2 - 0.82) * depth)
        ry = diameter * 0.5
        rx = ry / frame_aspect
        center_y = ground - air_height(time) - ry

        # The gate, as the game applies it: no overlap, no strip.
        if x2 < column - rx or x1 > column + rx:
            continue
        if y2 < center_y - ry or y1 > center_y + ry:
            continue
        stats["frames_occluding"] += 1

        index = int(np.argmin(np.abs(det_times - time)))
        truth = []
        if abs(det_times[index] - time) <= 0.1:
            det_w, det_h = det_size
            for det in detection_frames[index].detections:
                if det.label not in labels:
                    continue
                bx1, by1, bx2, by2 = det.box
                truth.append(
                    (bx1 / det_w - 0.06, by1 / det_h - 0.06,
                     bx2 / det_w + 0.06, by2 / det_h + 0.06)
                )

        cell = None
        if atlas is not None and sample.get("mask"):
            ah, aw = atlas.shape
            cy1 = int(round((1 - sample["v2"]) * ah))
            cy2 = int(round((1 - sample["v1"]) * ah))
            cx1 = int(round(sample["u1"] * aw))
            cx2 = int(round(sample["u2"] * aw))
            cell = atlas[cy1:cy2, cx1:cx2]

        phantom = 0
        for dx, dy in disc:
            px = column + dx * rx
            py = center_y + dy * ry
            if not (x1 <= px <= x2 and y1 <= py <= y2):
                continue
            if cell is not None:
                # Bilinear, exactly as the GPU samples the atlas.
                fx = (px - x1) / max(x2 - x1, 1e-6) * (cell.shape[1] - 1)
                fy = (py - y1) / max(y2 - y1, 1e-6) * (cell.shape[0] - 1)
                x0i, y0i = int(fx), int(fy)
                x1i = min(x0i + 1, cell.shape[1] - 1)
                y1i = min(y0i + 1, cell.shape[0] - 1)
                tx, ty = fx - x0i, fy - y0i
                value = (
                    cell[y0i, x0i] * (1 - tx) * (1 - ty)
                    + cell[y0i, x1i] * tx * (1 - ty)
                    + cell[y1i, x0i] * (1 - tx) * ty
                    + cell[y1i, x1i] * tx * ty
                ) / 255.0
                # The shader's smoothstep(0.35, 0.65) crosses 0.5 at 0.5.
                if value < 0.5:
                    continue
            if not any(
                tx1 <= px <= tx2 and ty1 <= py <= ty2
                for tx1, ty1, tx2, ty2 in truth
            ):
                phantom += 1

        fraction = phantom / len(disc)
        stats["worst_phantom"] = max(stats["worst_phantom"], fraction)
        if fraction > 0.04:
            violations.append(
                f"t={time:.2f}s: {fraction:.0%} of the ball erased with no "
                f"detected occluder there (track {sample['id']})"
            )

    return violations, stats


def foreground_entries(
    tracks: list[OccluderTrack],
    distance_map: DistanceMap,
    atlas: MaskAtlas | None = None,
) -> list[dict]:
    """The level-file rows: distance-keyed boxes, track ids, and mask UVs.

    Distance-keyed like everything else in the level, so occlusion stays glued
    to the footage at any playback speed. The id is what lets the game
    interpolate between samples of the same object instead of snapping a strip
    across the screen from one pole to the next.
    """
    entries: list[dict] = []
    for track in tracks:
        for index, sample in enumerate(track.samples):
            entry = {
                "d": round(float(distance_map.distance_at(sample.time)), 3),
                "id": track.id,
                "x1": round(sample.x1, 4),
                "y1": round(sample.y1, 4),
                "x2": round(sample.x2, 4),
                "y2": round(sample.y2, 4),
            }
            if atlas is not None and (track.id, index) in atlas.uvs:
                u1, v1, u2, v2 = atlas.uvs[(track.id, index)]
                entry.update(
                    {
                        "mask": 1,
                        "u1": round(u1, 5),
                        "v1": round(v1, 5),
                        "u2": round(u2, 5),
                        "v2": round(v2, 5),
                    }
                )
            entries.append(entry)

    entries.sort(key=lambda e: e["d"])
    return entries
