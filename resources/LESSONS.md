# Lessons

Traps this project actually hit, with the fix that worked. Ordered by how much
time each one cost.

## Unity

- **URP's 2D Renderer draws only `"LightMode" = "Universal2D"` passes.** A
  shader with only `UniversalForward` compiles, binds, and silently never
  draws — blank output, no error anywhere. Ship both pass tags in every custom
  shader (`RearCameraBackground`, `BallLit`, `BlobShadow` all do). Only a
  rendered-pixel test catches this class of bug; `VideoBackgroundRenderTests`
  exists for exactly that, and was verified by breaking the shader on purpose.
- **`VideoPlayer` seeks are asynchronous.** Setting `.time` every frame while
  drifted cancels the in-flight seek each time; the decoder never delivers and
  playback freezes *while time appears to advance*. Track `seekCompleted` with
  a timeout, and in native-rate mode don't fight the decoder at all: let the
  video lead and derive distance from its clock (can't drift by construction).
- **A saved scene is a snapshot.** Changing the editor scene-builder does
  nothing for scenes saved before the change, and the failure is silent — the
  user played a weeks-old character for days. Components now self-heal at
  runtime (build the ball, add missing components, disable stale sprites), and
  behaviour-critical values (clip name, marker diameter, air times) live in the
  **level file**, not serialized scene fields.
- **`Awake` order between objects is undefined.** `LevelDirector` called into
  `VideoBackground` before its `Awake` ran → NRE. Initialize-on-demand
  (`EnsureInitialized()`) removes the ordering dependency.
- **`JsonUtility` can't read top-level arrays or jagged arrays.** The level
  schema uses objects-in-lists everywhere specifically so no Newtonsoft
  dependency is needed. A test proves parsing rather than assuming it.
- **Adding an enum value? Prepend deliberately.** `PlaybackMode.Auto` was put
  first so old scenes' serialized `0` *migrates to* Auto instead of pinning
  stale behaviour.
- **Components that read config in `Awake`** (VehicleSpeedController disables
  itself without one) must be built inactive, fields set by reflection, then
  activated — the order `PlayModeTestFactory` uses.

## Batchmode testing

- Frame counts are not time: batchmode runs frames in microseconds, so "wait
  60 frames for the decoder" waits nothing. **Wait on wall clock**
  (`WaitForSecondsRealtime`) for anything involving decode.
- Video seeks never complete in batchmode; drive playback via fast
  `SpeedMultiplier` through the normal play path when capturing at a timestamp.
- Perf numbers from batchmode (0.18 ms/frame) are meaningless — nothing
  presents. Only profile a real player/editor.
- A test that passes must be shown to *fail* on the bug it guards: the render
  test was validated against the deliberately re-broken shader (0 % non-black).

## Video & CV

- **Ship a transcode, analyze the original.** 1080p HEVC is fine to analyze
  once and brutal to decode at 60 fps on a phone; 720p H.264 (no audio) halves
  the size and is what Android reliably decodes.
- **Flow ∝ speed/depth.** Monocular flow can't separate them: a close wall
  reads as 4× acceleration. Band choice and ground-masking reduce it; the fix
  that worked was **physics** — cap believable acceleration (a car can't
  change speed several-fold in a fraction of a second).
- **SAM 2 has no semantics.** Point-prompts on a wall return the wall as
  "ground" (94.5 % coverage vs a 19.5 % median gave it away). Always gate
  masks with plausibility checks; when evidence is implausible, mark the
  sample unmeasurable and interpolate — *don't* fall back to a second bad
  estimator (the fallback measured worse than the artifact).
- **The segmented ground line is the region's far edge**, not the stance line.
  "Inside the region" is supported; only above it floats. Conversely, things
  the character rides (rails, hedge tops) need their **own** top-edge lines —
  the marker's foot was inside the ground region only 38 % of the time because
  it was standing on things *on top of* the ground.
- **Colour tracking needs three gates.** Hue alone latched onto red brick and
  stop signs (marker H≈176 vs brick H≈35): narrow wrapped hue band +
  circularity + frame-to-frame continuity. Prefer a marker colour absent from
  the scene.
- **Editors keyframe linearly.** A hand-animated overlay has corners no
  physical object has — extract *intent* (timing, height, arc span) and let
  the game generate the physical motion; never replay the raw path.
- **Bimodal evidence is no evidence**: medians across wall-flap frames
  (0.51 vs 0.93) described a ground line that existed in no frame.
- Detection boxes of one object arrive as several; union same-class masks
  before taking edges. And 2D box-disjointness is the wrong clearance
  criterion for tall obstacles (see AUDIT_CRITERIA).

## Process

- **Verify every scripted edit.** Two `str.replace` patches silently no-op'd
  (file had drifted) while the script printed "patched" — hours chasing a
  phantom. `grep` for the new text after patching, or use exact-match editing.
- **Measure before optimizing, and validate the metric.** "Peak height at
  t≈61 s" tracked a broad hump that turned out to be *real speed*; jerk
  (Δv/Δt vs median) isolated the actual artifact — 32× → 0.51×.
- **The overlay video is the acceptance test.** Wrong curves and drifting
  lines are obvious on screen and invisible in JSON. Render evidence back onto
  footage at every stage.
- **Fitter and checker must share constants** (and the checker must be the
  stricter one), or the fitter certifies things the audit rejects — three
  rounds of hairline failures came from exactly this.
- Keep giant regenerables out of git (`Library/` was 41 k files / 2.1 GB of
  history); model weights and videos are ignored by pattern.
