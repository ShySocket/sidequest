# How the video-runner is built (and why edits work the way they do)

Read this once per session before the first fix. It is the mental model the
whole editing loop relies on.

## The product

A phone against a car's side window plays prerecorded footage
(`IMG_3775.mov`); a 3D black ball appears to run alongside on real surfaces —
railing, sidewalk, hedge tops — jumping and dodging real obstacles. The goal is
that a viewer believes the ball is physically in the scene. Every editing rule
exists to protect that belief.

## The data flow

```
timeline.json  (design intent, VIDEO SECONDS)  ──┐
CV artifacts   (out/*.surfaces.npz, *.ledges.npz,├─ analyze author ─► out/IMG_3775.authored.json
                *.detections.json, *.track.json) ┘        │                + IMG_3775.occluders.png
                                                          ▼
                                        copy-to-unity.sh ─► SidequestV1/Assets/StreamingAssets/
                                                          │
                                                          ▼
                              LevelDirector ── VideoBackground ── VideoRunnerCharacter/BallView
```

- **`tools/video-analyzer/timeline.json` is the single source of design
  truth.** Nearly every fix is an edit here, then a re-author. Times are video
  seconds — the same unit the in-game top-left clock shows.
- The CV artifacts are cached measurements (ground segmentation, rail/hedge
  top edges, obstacle detection boxes, the hand-animated marker track). They
  are inputs, not things a fix edits.
- `analyze author` merges intent with measurement: ground line from
  segmentation + ledges, jumps/dodges verbatim from the timeline (it is
  authoritative), scoring windows and warning leads derived from gaps between
  scored events, occluder strips derived from the depth rule, silhouettes
  SAM-baked into an atlas.
- The Unity side is deliberately dumb: it replays the level file. Behaviour
  values live in the level, not the scene, because saved scenes are snapshots
  that silently pin stale values.

## The choreography contract

Every jump/dodge/hop fires at exactly its written time on every playthrough.
Player input only **scores** (a press inside the window clears; a miss flashes
the ball red and the motion happens anyway). Consequences:

- Editing a time changes what every player sees; the audit is the gate.
- `hop` events are automatic believability beats (kerbs, level changes) —
  never cued, scored, or counted.
- Arcs integrate on **video time**, so they stay glued to the footage at any
  playback rate (including on-device vehicle-speed mode).

## Coordinates and geometry

- Positions are normalized to the video frame: x in 0..1 of width, y in 0..1
  of height, **y grows downward**. Larger y = lower on screen = nearer the
  camera (screen height is the depth proxy — everything stands on one ground
  plane).
- The world sweeps left→right (+x), so obstacles enter at the left edge and
  cross the ball's column; the ball travels right-to-left and never moves
  horizontally itself — the world comes to it.
- An obstacle's **column-crossing interval** is the span during which its
  detection box's x-range overlaps the ball's column (± ball radius in x,
  which is `r · height/width` ≈ 0.56·r). This interval, read from
  measurements, decides takeoffs and landings.
- Ball diameter is `markerDiameter` (0.138 of frame height) scaled 0.82–1.2 by
  where the SIZE line sits between the path's 5th/95th percentile heights. The
  size line (baked per path sample as `z`) equals the ride line except across
  `sizeDepth` spans: an elevated surface's top edge is position, not distance —
  a hedge top rides high on screen while the bush stands at the sidewalk's near
  edge — and sizing from the top edge shrinks the ball as if it had run from
  the camera.

## Event schema (timeline.json)

```jsonc
{ "time": 27.3, "type": "jump", "label": "sign, on bush",
  "height": 0.3, "airTime": 0.8, "_why": "..." }
{ "time": 12.0, "type": "hop", "label": "down a level", "height": 0.1, "airTime": 0.4 }
{ "time": 57.32, "type": "dodge", "label": "stop sign", "duration": 0.9 }
```

- `height` = arc peak in frame heights; `airTime` = takeoff→landing seconds;
  lift(t) = `4·h·φ(1−φ)` with φ = (t−takeoff)/airTime.
- The event **label names the obstacle** — the overlap audit polices exactly
  that class (`"sign"`→traffic sign, `"car"/"van"/"suv"`→vehicles,
  `"people"`→person, `"chain"/"fence"`→chain/fence). A label naming nothing
  ("off the hedge") is checked against nothing.
- `duration` on a dodge overrides the tuned 0.55s step (used when a dodge
  should read unhurried).

Other timeline sections: `surfaces` (named spans; the ledge extraction supplies
exact heights), `pathAdjust` ({from,to,dy} nudges eased over 0.4s — design
intent on top of measurement), `sizeDepth` ({from,to,y} spans declaring the
ground-plane y the ball's SIZE reads its depth from, where the ride line is
elevated; place edges mid-arc — each ramps 0.3s between the depth y and the
ride line's value at that edge), `behindSpans` / `frontSpans` (force/suppress
occlusion where the depth rule mis-reads), `occluderLabels` (classes allowed to
occlude; currently pole, traffic sign, car), `hidden` (ball off-screen spans —
both edges get a dust-puff in game).

## Occlusion

Derived, not authored: an object whose box **base sits clearly below the
ball's resting line** (≥0.03 margin) is nearer the camera, and a strip of
video is re-drawn in front of the ball there, masked to the object's SAM
silhouette. Thin classes (pole, sign) are preferred over cars — a ball behind
a sign post reads right, a car-wide strip reads as rolling under the car.
Elevated surfaces (rail, hedge) suppress derivation; dodges suppress ±(0.25,
1.0)s; `frontSpans` suppress; `behindSpans` force.

## The physics of belief (the rules fixes must keep)

1. **Gravity sets air time.** T ≈ 2.44·√height (calibrated on this clip from
   the accepted SUV jump). Longer reads as moon-float; shorter reads as bounce
   energy and is fine for small hops.
2. **No frame of an arc may show the ball overlapping its named obstacle** —
   takeoff, flight, or landing. Enforced by `analyze audit --video`, which
   confirms box hits against SAM silhouettes cut from the flagged frames.
3. **Jumps go wrong at their endpoints, not their peaks.** Takeoff must
   precede the obstacle reaching the column; landing must wait until the
   crossing has fully passed. Fix order: earlier takeoff → later landing →
   taller arc → (if the object is genuinely nearer) occlusion. Never shrink
   the ball or pull the path off its measured line.
4. **Land on the surface where its pixels actually are**, not where the
   timeline label starts — key surface handovers to when the surface reaches
   the ball's column, measured from detections/ledges.
5. **Level changes need a beat** — a small `hop`, or fold the change into an
   adjacent arc. A ground step glided across reads as a glitch.
6. **Entrances and exits need punctuation** — hidden spans puff on both edges.
7. **Choreography and physics share the video clock.** Anything authored to
   video moments must advance on video-time deltas (already true in the
   character; keep it true in new code).

## Unity file map (for the rare code-side fix)

| Concern | File |
|---|---|
| Playback, distance authority, start-hold | `Assets/Scripts/VideoLevel/LevelDirector.cs` |
| Scheduled arcs, scoring, dodges, video-time clock | `VideoRunnerCharacter.cs` |
| Ball rendering: shadow, roll, squash, flash, poof | `VideoRunnerBallView.cs` |
| Video quad, frame→world mapping, occluder strip | `VideoBackground.cs` |
| Level file schema (JsonUtility mirror) | `VideoLevelData.cs` / `VideoLevel.cs` |
| HUD, timer clock, cue prompts | `VideoRunnerHud.cs` |
| Ball lighting (golden-hour tuned) | `Assets/Resources/BallLit.shader` |
| Scene rebuild menu | Tools > Sidequest > Build Video Runner |

Deep background: `SidequestV1/Assets/Documentation/VIDEO_RUNNER.md` (design),
`resources/AUDIT_CRITERIA.md` (every audit check and threshold),
`resources/LESSONS.md` (traps already hit — read before debugging anything
strange), `resources/PIPELINE_PLAYBOOK.md` (full pipeline for a NEW clip).
