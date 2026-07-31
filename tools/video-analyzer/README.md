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

Segment the ground surface first — it is cached, and the speed pass consumes it:

```bash
uv run analyze surface ../../IMG_3775.mov -o out/surfaces.npz --stride 2
```

```bash
uv run analyze speed ../../IMG_3775.mov -o out/level.json --surfaces out/surfaces.npz --overlay out/overlay.mp4
```

`--overlay` renders the debug video, which is the real acceptance test: a wrong
speed curve or a drifting run line is obvious on screen and invisible in JSON.

Useful flags: `--stride N` (analyse every Nth frame), `--analysis-width`
(default 960), `--overlay-stride`.

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
| Obstacle detection + tracking | detection validated, not yet integrated |
| Level file emit | speed, distance and surfaces |

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
