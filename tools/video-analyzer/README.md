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

```bash
uv run analyze speed ../../IMG_3775.mov -o out/level.json --overlay out/overlay.mp4
```

`--overlay` renders the debug video, which is the real acceptance test: a wrong
speed curve is obvious on screen and invisible in JSON.

Useful flags: `--stride N` (analyse every Nth frame), `--analysis-width`
(default 960), `--overlay-stride`.

```bash
uv run pytest
```

## Status

| Stage | State |
|---|---|
| Decode, time↔distance map | done, unit tested |
| Ego-motion speed curve | done — **provisional**, see below |
| Surface segmentation (sidewalk / railing) | not started |
| Obstacle detection + tracking | not started |
| Level file emit | speed and distance only |

## Known limitation: depth confounds speed

Optical flow scales as `speed / depth`, so scene depth leaks into the speed
estimate.

Measuring five horizontal bands of the frame on `IMG_3775.mov` showed the near
ground beside the car is decisively the most stable proxy — IQR/median **0.62**,
against 1.29–1.70 for bands looking at the mid-field or horizon — because the
ground sits at a roughly fixed distance while scenery does not. That is why
`SpeedConfig.roi_top/roi_bottom` default to the bottom of the frame.

It is a reduction, not a cure. Where the car passes close to a building the wall
fills the band: this clip spikes **4.5×** at t≈61 s, which is depth, not
acceleration. Narrowing the band only reaches 3.3×, so geometry alone cannot fix
it — visible directly in the overlay, where the ROI at t≈60.7 s contains nothing
but brick.

The real fix is to restrict flow to pixels genuinely on the ground plane, using
the road/sidewalk masks from the segmentation stage. `estimate_speed()` already
takes a `ground_mask` callable for this. **Treat the current speed curve as
provisional until those masks exist.**
