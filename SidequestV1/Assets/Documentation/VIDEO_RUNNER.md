# Video Runner

A playable runner over the prerecorded clip. The character stands at a fixed
column of the screen, the video scrolls past, and cues arrive at authored moments.

Separate from `RunnerPrototype`, which is untouched.

## Run it

1. Generate the level and copy the assets (once, or after editing the timeline):

   ```bash
   cd tools/video-analyzer
   uv run analyze author ../../IMG_3775.mov -o out/IMG_3775.authored.json --surfaces out/IMG_3775.surfaces.npz
   ./copy-to-unity.sh
   ```

2. In Unity: **Tools > Sidequest > Build Video Runner**, then press Play.

The menu item regenerates `Assets/Scenes/VideoRunner.unity` from scratch, so
re-running it is always a safe way back to a working scene.

## Controls

| Input | Action |
|---|---|
| Space / click / tap | Jump |
| Down arrow, S, or Left Shift | Dodge (step aside) |
| R | Restart |
| `-` / `=` | Playback speed |
| Left / Right arrow | Scrub 5s |
| F1 | Toggle the debug readout |

The HUD shows the current **video time**, which is the unit `timeline.json` is
written in — so a cue that feels early or late can be read off the screen and
corrected directly.

## Editing the level

`tools/video-analyzer/timeline.json` holds the whole design in video seconds:
which surface the character is on, when each cue lands, and when it is hidden.
Change a number, re-run the two commands above, press Play. No C# involved.

The CV analysis supplies the part a person cannot author by hand: where the
ground sits on screen, frame by frame, from the SAM 2 run line.

## How it fits together

```
timeline.json ──┐
                ├─► analyze author ─► IMG_3775.authored.json ─► StreamingAssets
surfaces.npz ───┘                                                     │
(CV run line)                                                         ▼
                                            LevelDirector ── distance is authority
                                              │      │
                              VideoBackground ┘      └─ VideoRunnerCharacter
```

**Distance is the authority, not video time.** The director integrates forward
motion into a distance, then asks the level which video time to display. Every
cue is stored against distance, so the level stays correct at any playback
speed — including the `VehicleSpeed` mode, where real GPS drives progress.

`VideoBackground` owns the mapping from video-frame coordinates to world space.
The level stores the ground as a normalized y *within the video frame*, so
anything positioning the character has to know where that frame landed on
screen; letterboxing changes it, and getting it wrong puts the feet off the
ground everywhere.

## Direction of travel

Measured, not assumed. On `IMG_3775.mov` the world sweeps left-to-right at
+50 px/frame with 98% of frames agreeing, so the vehicle - and the character -
travel **right-to-left**, and obstacles arrive from the **left** edge.

The analyzer emits this as `travelDirection: -1`, and the character mirrors and
leans into it. Filming out the opposite window flips the sign automatically, with
no code change.

One consequence worth knowing: with `characterColumn` at 0.35, an obstacle only
crosses 35% of the screen before reaching the character. Mirroring the usual
runner layout - character on the right, obstacles entering from the left - would
mean a column nearer 0.65 and more warning. That is a one-flag change
(`--character-column`), but it shifts where each cue lands relative to the
character, so the authored times would want a pass afterwards.

## The character

A black 3D sphere, lit to sit in the filmed world rather than on top of it.

**Lighting is a custom shader, not URP/Lit.** This project renders through URP's
2D Renderer, which draws no `UniversalForward` pass, so URP/Lit is simply
invisible here — and switching the project to a 3D renderer to fix that would put
the existing 2D prototype at risk. `Sidequest/BallLit` ships both pass tags, so it
works under either renderer.

It also has to match a *specific video*, not a generic scene. Light direction and
colour are material properties, set from the footage: shadows in the parking-lot
sections fall to the right and toward the camera, and the light is low and warm.
A black ball has almost no diffuse response, so what actually reads as
three-dimensional is the specular highlight and the fresnel rim — the albedo is
near zero by design.

**The shadow is a blob, deliberately.** A cast shadow needs a surface to land on,
and there is no geometry here: the ground is pixels on a perspective plane that no
flat receiver in the scene matches, so a shadow map would land in the wrong place
or on nothing. A blob can be placed exactly where the level says the ground is.

Four cues do the work of making it look present, in rough order of importance:

| Cue | Why |
|---|---|
| Contact shadow | Without it the ball reads as a sticker. Size and opacity track height — that is what says *airborne* rather than *bigger* |
| Rolling | A ball that translates without rotating looks dragged. Rate comes from the level's measured screen speed, so it matches the world sliding past |
| Perspective scale | The ground line rises and falls as the road nears and recedes; the ball scales with it or it appears to swim |
| Squash and stretch | Stretch through the arc, squash on landing — makes a jump read as effort rather than a slide upward |

## Design notes

**Motion is kinematic, not physics-driven.** The ground here is a line sampled
from the video, not a collider, and it slides around the screen as the car
moves. A `Rigidbody2D` resting on a collider that teleports every frame behaves
badly; integrating a jump arc above a moving ground line is simpler and exactly
reproducible, which matters when cues are authored to specific moments.

**A miss does not end the run.** The point of this build is evaluating the
timeline, and dying at 11s repeatedly would make that harder. Misses are counted
and shown instead.

**A cue counts if it is satisfied anywhere inside its window**, not only at the
exact instant — with a short input buffer so a slightly early tap still lands.

## Playback modes

`LevelDirector.playbackMode`:

- **NativeRate** (default) — advances at the clip's own pace, scaled by the
  speed multiplier. What you want at a desk.
- **VehicleSpeed** — advances from `VehicleSpeedController.GameSpeed`, i.e. real
  GPS and accelerometer motion. Wire the controller into the director's
  `vehicleSpeedController` field and set `referenceGameSpeed` to the game speed
  that should equal the clip's own pace.

The video is driven by `playbackSpeed` rather than by seeking each frame:
seeking 1080p HEVC every frame stutters, while a scaled rate is smooth. A
corrective seek only happens when drift exceeds 0.3 s.

## Assets

`Assets/StreamingAssets/` holds the clip and the level. StreamingAssets is
copied into builds verbatim and never imported, which is what a `VideoPlayer`
URL source needs. The `.mov` is gitignored at 91 MB; the level JSON is committed.

The clip is HEVC in a `.mov`, which plays natively on macOS and iOS. **For
Android, transcode to H.264 MP4** — HEVC support there is inconsistent.

## Tests

`Assets/Tests/EditMode/VideoLevelTests.cs` covers the level file contract:
that `JsonUtility` can read the schema at all (it has no top-level arrays and no
arrays of arrays, precisely so it can), that time and distance round-trip, that
lookups clamp outside the clip, and that the real authored level parses with
every cue inside the level and the ground line on screen throughout.
