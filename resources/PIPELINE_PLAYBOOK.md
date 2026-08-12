# Pipeline playbook: new clip → playable level

Every command runs from `tools/video-analyzer/`. Stages cache to `out/`, so
re-running a later stage never repeats an earlier one. Approximate times are
for an ~90 s clip on an M-series Mac.

Filming tip that pays for itself: **record with Location Services on**. The
clip then carries a GPS track and speed becomes metric instead of relative.
IMG_3775 had no GPS, which is why its distances are "relative units".

## 0. One-time setup

```bash
cd tools/video-analyzer && uv sync
```

## 1. Playback copy (~1 min)

The game never plays the original: analysis wants every pixel once, playback
must decode a frame every 16 ms on a phone.

```bash
uv run analyze transcode /path/to/CLIP.mov -o out/CLIP.play.mp4
```

## 2. CV passes (cacheable; order matters only where noted)

```bash
# Ground segmentation → run line (SAM 2, ~6 min)
uv run analyze surface /path/to/CLIP.mov -o out/CLIP.surfaces.npz --stride 2

# Obstacle naming (Grounding DINO, ~10 min at stride 5)
uv run analyze detect /path/to/CLIP.mov -o out/CLIP.detections.json --stride 5

# Top edges of railings/hedges (needs detections, ~5 min)
uv run analyze ledges /path/to/CLIP.mov -o out/CLIP.ledges.npz \
    --detections out/CLIP.detections.json --stride 5
```

## 3. Marker track (optional but much better than typing a timeline)

Animate a **solid, saturated disc** over the footage showing where the
character should be — jumps included. Avoid red if the scene has brick or stop
signs; the tracker separates them by hue/shape/continuity but a green or cyan
marker removes the problem entirely. If the marker's colour differs from deep
pink, set `TrackConfig.hue` in `track.py` (sample it: the tracker README-level
docstring shows how it was calibrated).

```bash
uv run analyze track /path/to/OVERLAY.mp4 -o out/CLIP.track.json --stride 2
```

The overlay may be shorter than the clip (IMG_3775's covered the first minute);
`animatedUntil` is detected and the hand-typed timeline covers the rest.

## 4. Author the level

Write `timeline.json`: surfaces (`rail`/`floor`/`hedge`/`sidewalk`/`grass`)
with start times, events past the marker's coverage, hidden spans. All in
**video seconds** — the HUD shows the same unit, so tuning is read-off-and-edit.

```bash
uv run analyze author /path/to/CLIP.mov -o out/CLIP.authored.json \
    --surfaces out/CLIP.surfaces.npz \
    --timeline timeline.json \
    --marker out/CLIP.track.json \
    --ledges out/CLIP.ledges.npz \
    --detections out/CLIP.detections.json \
    --playback-file CLIP.play.mp4
```

What authoring does beyond merging: snaps named surfaces to their measured
ledges (interpolating across detection gaps), clamps ground spans inside the
visible ground region, converts marker arcs into cues at the arc **start**
(a tap is a takeoff) carrying the drawn **air time**, and **fits each jump's
cue window from the detections** so any accepted tap clears its obstacle.

### Occlusion (automatic — no timeline entry needed)

The author pass also derives every place the ball should pass **behind** a
foreground object, from one measurement: on a shared ground plane, an object
whose detection-box base sits clearly below the ball's line is nearer the
camera. Candidates near the ball's column are chained into per-object tracks
(flicker dropped), each sighting is SAM-segmented into a silhouette, and the
silhouettes ship as `out/CLIP.occluders.png` next to the level. In the game,
a strip of the video is re-drawn in front of the ball through that silhouette,
so the ball slides behind the pole's actual outline. Dodges, hidden spans and
elevated surfaces (`rail`, `hedge` — the ball fronts what's beyond them)
suppress derivation automatically.

Overrides, only if the eyeball check disagrees with the measurement:

- `"behindSpans": [{"from": s, "to": s}]` — force occlusion (e.g. a pole whose
  base a parked car hides, making it read as farther than it is).
- `"frontSpans": [{"from": s, "to": s}]` — suppress a misread box.
- `"occluderLabels": ["pole", "traffic sign", "tree"]` — widen the class list
  (default: pole, traffic sign).
- `--no-occluder-masks` — skip the SAM pass; occluders degrade to rectangles,
  which still read correctly (the re-drawn pixels match the background).

Verify + eyeball check — `--verify` steps the whole level at video rate
through the game's own strip logic (mirrored 1:1 from `VideoLevel.cs`) and
fails if any frame erases ball pixels with no detected object in front, or if
the atlas has any non-black texel outside a silhouette cell; the stills are
the human acceptance test on top:

```bash
uv run analyze occluders /path/to/CLIP.mov --level out/CLIP.authored.json \
    --detections out/CLIP.detections.json --verify -o out/CLIP.occluders
```

## 5. Audit — the gate

```bash
uv run analyze audit --level out/CLIP.authored.json \
    --surfaces out/CLIP.surfaces.npz --ledges out/CLIP.ledges.npz \
    --detections out/CLIP.detections.json \
    --timeline timeline.json --video /path/to/CLIP.mov
```

Non-zero exit on violations; each prints a time span, a signed margin, and
what the evidence was. Fix causes (timeline times, hidden spans, marker), not
symptoms, and re-run 4→5 until clean. Criteria: [AUDIT_CRITERIA.md](AUDIT_CRITERIA.md).

`--video` arms the overlap check's picture analysis: box-test hits are
confirmed against SAM silhouettes cut from the flagged frames themselves, so
empty box corners (a hatchback's sloped tail) don't fail arcs that are
visually clean. Without it the box verdict stands, erring toward flagging.

## 6. Into Unity

```bash
./copy-to-unity.sh out/CLIP.play.mp4 out/CLIP.authored.json
```

Update `levelFileName` on the LevelDirector (or the constant in
`VideoRunnerSetup.cs`) if the level name changed, then
**Tools > Sidequest > Build Video Runner** and press Play. The scene self-heals,
so replaying after re-authoring needs no rebuild.

## One-shot

`./new-clip.sh /path/to/CLIP.mov [/path/to/OVERLAY.mp4]` runs 1→6 in order.

## Tuning loop, in practice

1. Play; note the on-screen video time where something feels wrong.
2. Edit `timeline.json` at that time (or re-draw the marker span).
3. Re-run author + audit (seconds, everything else is cached), copy, press Play.
