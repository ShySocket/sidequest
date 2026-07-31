# video-analyzer

Turns a car-window video into a Sidequest **level file**: the surfaces the
character runs along, the obstacles it must clear, and the speed curve that ties
video progress to how fast the player is actually moving.

Runs offline on a dev machine. Unity consumes only the resulting JSON.

## Why distance, not time

Every event in the level file is stored against **distance travelled**, never
against video time. Storing an obstacle at "t = 41.7 s" only works if the player
drives at exactly the speed the clip was shot at; store it at a distance and it
stays correct at any playback rate, including stopped.

So the game integrates its own speed into a distance, asks `time_at(distance)`,
and drives `VideoPlayer.playbackSpeed` from the ratio of desired to native rate.
See `distance.py`.

## Setup

Requires [uv](https://docs.astral.sh/uv/). Python 3.12 is pinned — 3.13 is
ahead of parts of the CV stack.

```bash
cd tools/video-analyzer
uv sync
```

## Usage

Four passes. The first two are cached to disk, so the cheap final pass can be
re-run freely while tuning.

```bash
uv run analyze surface ../../IMG_3775.mov -o out/surfaces.npz --stride 2
```

```bash
uv run analyze detect ../../IMG_3775.mov -o out/detections.json --stride 5
```

```bash
uv run analyze level ../../IMG_3775.mov -o out/level.json --surfaces out/surfaces.npz --detections out/detections.json --overlay out/overlay.mp4
```

`analyze level` runs the speed pass internally. `--detections` is optional — the
level is complete without it, just with every obstacle labelled `unknown`, since
timing comes from the run line and detection only supplies names.

`--overlay` renders the debug video, which is the real acceptance test: a wrong
speed curve, a drifting run line, or a mistimed obstacle is obvious on screen and
invisible in JSON.

Useful flags: `--stride N` (analyse every Nth frame), `--analysis-width`
(default 960), `--character-column` (default 0.35), `--overlay-stride`.

```bash
uv run pytest
```

Runtimes on an M5 (24 GB), for the 88 s / 2627-frame clip:

| Pass | Rate | Wall clock |
|---|---|---|
| Optical flow | 165 fps | 16 s |
| SAM 2 surface (stride 2) | 3.9 fps | 5 min 38 s |
| Grounding DINO detection | 1.0 fps | ~46 min |

Everything runs locally on MPS. No API keys, no accounts, no cloud.

## Status

| Stage | State |
|---|---|
| Decode, time↔distance map | done, unit tested |
| Ego-motion speed curve | done |
| Ground surface + run line (SAM 2) | done |
| Obstacle events | done |
| Obstacle naming (Grounding DINO) | done, zero-shot |
| Level file emit | done |
| Unity consumption | not started — next milestone |

## How obstacles are found

Not by tracking objects across the frame. The character occupies a fixed screen
column, so an obstacle matters at exactly the moment it occupies that column —
which the run line already answers directly. Watching the character's own column
for the surface becoming unrunnable gives the event, with no tracker, no motion
prediction, and no time-to-collision estimate.

Two properties fall out of that choice:

- **Unlisted obstacles still count.** Anything standing on the ground interrupts
  the mask, whether or not a class list anticipated it.
- **Events are consistent with the surface by construction.** A separate detector
  could report an obstacle the character never actually meets.

Detection then only supplies *names*, which is why it can be sampled at
`--stride 5` while the surface pass runs at `--stride 2`.

### Results on IMG_3775.mov

88 s, 2627 frames. 39 obstacles, one every ~2.2 s; 22 named (10 car, 9 bush,
3 traffic sign), 17 left `unknown` — still real obstacles, just unnamed.

| Check | Result |
|---|---|
| `time → distance → time` round trip | 0.0000 ms error (a frame is 33.4 ms) |
| `timeToDistance` strictly monotonic | yes, so always invertible |
| Runnable coverage | 90.1%, with 7.5% in 12 gaps |
| Obstacles ordered, non-overlapping | yes |
| Obstacles inside a runnable segment | 38 / 39 |
| Path continuity (Δy between points) | median 0.004, p99 0.056, max 0.172 |

Four things had to be fixed to get the path continuous, each caught by measuring
rather than by eye:

1. Ragged mask edges on textured asphalt manufactured obstacles on open road →
   median filter across columns.
2. Frames where SAM 2 barely found ground reported "blocked" when they meant
   "cannot see" → `min_valid_fraction`.
3. A wall at t≈70.1 s slipped the coverage band at 58.8% and put the run line at
   the top of the frame → `max_rise`.
4. Path height was sampled at one column while blockage was judged over the whole
   footprint, so an outlier column produced a 39%-of-frame jump → both now
   measured over the same footprint.

Max path jump went 0.90 → 0.39 → 0.17 of frame height across those fixes.

### Known limitations

- **False positives remain.** Spot-checking classified obstacles, a clear
  majority are real (a "DO NOT ENTER" sign and a parked van both land exactly in
  the character column), but marginal frames with low ground coverage still
  produce the occasional phantom. Low `confidence` and low `ground` coverage are
  the signals to filter on.
- **17 of 39 obstacles are unnamed.** Geometry found them; detection did not
  cover them at `--stride 5`. Lower the stride to trade time for coverage.
- **No ground truth.** No GPS track in this clip, so obstacle timing has been
  validated by construction and by inspection, not against measurements.

### What is deliberately not stored

Tap timestamps. The level file records where an obstacle *is*; Unity derives the
tap window from the jump physics in force at runtime (`jumpVelocity`,
`gravityScale`). Baking tap times would freeze one particular jump tuning into
every level file and invalidate all of them on the next retune. A test asserts
the level file contains no tap field.

## Level file

The contract with Unity. Distances are relative units; `y` values are normalized
0–1 against frame height, so a level survives a change of analysis resolution.

```jsonc
{
  "version": 1,
  "source": { "file": "IMG_3775.mov", "fps": 29.978, "width": 1920, "height": 1080,
              "duration": 87.63 },
  "distanceUnits": "relative",
  "characterColumn": 0.35,          // where the character stands, fraction of width
  "totalDistance": 41396.2,

  "timeToDistance": [[0.0, 0.0], [0.0334, 5.7]],   // strictly monotonic, invertible

  "surfaceSegments": [              // stretches with runnable ground
    { "startDistance": 0.0, "endDistance": 3612.2,
      "path": [[0.0, 0.612], [31.5, 0.615]] }      // [distance, normalized y]
  ],
  "surfaceGaps": [                  // nowhere to run: wall passages
    { "startDistance": 28900.0, "endDistance": 29667.0 }
  ],
  "obstacles": [
    { "id": 7, "class": "car", "distance": 8546.2,
      "startDistance": 8415.0, "endDistance": 8677.6,
      "blockage": 1.0,              // peak share of footprint unrunnable
      "height": 0.22, "confidence": 0.81 }
  ],
  "stoppedSpans": []                // video time, for playback handling
}
```

Unity reads this by integrating its own `GameSpeed` into a distance, then asking
`timeToDistance` which video time to display. Note `JsonUtility` cannot
deserialize top-level arrays, so the Unity side needs Newtonsoft.

## Depth, and why speed is relative

Optical flow scales as `speed / depth`, and monocular video gives no depth, so
a scene that suddenly gets closer reads as one that suddenly got faster. This
clip carries no GPS track either, so there is no ground truth to calibrate
against. **Speed is therefore relative, not metric** — which is all the game
needs, since playback is driven by ratios.

Three things reduce the artefact, and it is worth being precise about which one
actually mattered:

1. **ROI choice.** Measuring five horizontal bands showed the near ground is by
   far the most stable proxy (IQR/median **0.62** against 1.29–1.70 for bands
   looking at the mid-field or horizon), because it holds a roughly fixed depth.
   Hence the defaults in `SpeedConfig`.
2. **Ground masking** (`--surfaces`). Restricts flow to SAM 2's ground mask.
   Helps the peak modestly; barely moves the underlying roughness.
3. **Acceleration limiting.** This is the fix. Peak speed change went from
   **32× median per second** — physically impossible for a car — to **0.51×**,
   the configured cap. A 63× reduction.

Measured on `IMG_3775.mov`, where the car passes within a metre of a brick wall
at t≈61 s and the flow field briefly triples:

| Configuration | max jerk (×median/s) |
|---|---|
| Fixed ROI band | 31.98 |
| + ground mask | 32.72 |
| + acceleration limit | **0.51** |

A caution on reading the curve: the broad hump remaining around t≈60–64 s is
probably *real*. Comparable humps appear at t≈33 s and t≈70 s where nothing
unusual is in frame. Only the narrow needle was an artefact, and it is gone.

Where SAM 2 finds no plausible ground — coverage of 94.5% at t≈61 s, against a
median of 19.5%, means it segmented the wall — the sample is marked unmeasurable
and interpolated rather than guessed. Falling back to the fixed band there
measured *worse* (2.18× → 2.37× peak), because the band is looking at the same
wall.

To get metric speed, film with Location Services on: the clip then carries a GPS
track and the constant can be fitted properly.
