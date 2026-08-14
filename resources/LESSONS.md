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
- **`VideoPlayer`'s clock runs ahead of its pictures right after Play.** Cold
  decoder buffers mean the first ~half second shows a frozen frame while
  `player.time` advances — so the ball climbed against a still image ("laggy
  on load"). Hold the run: issue Play once to get frame 0 delivered, pause on
  it briefly, then start — the video and the ball then move together from the
  first visible moment. Restart takes the same path (a seek to 0 is just as
  cold).
- **Choreography and physics must share a clock.** Takeoffs fired on *video*
  time while the arc integrated with `Time.deltaTime` — correct at 1× and
  silently desynchronized at every other rate (fast-forward in captures, and
  the vehicle-speed mode where the video follows the car). A jump authored to
  land at 11.55s landed wherever the wall clock said. Anything authored to
  video moments must advance on video-time deltas; only view-side animation
  (squash recovery, roll) belongs on wall time. Caught by a capture harness
  that fast-forwards — worth keeping for that reason alone.
- **Serialized tuning in a saved scene silently pins old limits.** The jump
  height clamp lived in a `[SerializeField]` range — raising the code default
  would have done nothing, because the scene had 0.50 baked in. Authored cue
  values are trusted (they are audited offline); scene-serialized ranges only
  clamp the fallback path.

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
- **Every occlusion failure mode must leave the ball visible.** The first
  silhouette atlas used a white background and white "segmentation failed"
  cells so samples could fall back to rectangle occlusion. Wrong default:
  bilinear sampling bled the white in at cell borders and bit clean-edged
  notches out of the ball with nothing in front of it, and a generous
  nearest-sample hold parked stale silhouettes where poles used to be.
  Black-everything atlases, an edge hold bounded to half the track's own
  sample spacing, and a strip-touches-ball gate made "behind a real object,
  or invisible" the only reachable states — enforced by
  `analyze occluders --verify`, which mirrors the game's lookup 1:1.
- **A jump is wrong at its endpoints before it is wrong at its peak.** Every
  overlap the frame-by-frame review caught was at a takeoff or a landing,
  never mid-arc: the van's hood had already reached the column before the
  annotated takeoff; the parked car was still crossing the old landing spot.
  Rules that prevent the whole class: (1) the takeoff must precede the
  obstacle reaching the ball's column — measure the crossing from pixels, not
  from when it "feels" due; (2) the landing must wait until the crossing has
  fully passed — extend `airTime`, don't steepen the arc; (3) verify with the
  audit's overlap check (`analyze audit --video`), which walks every frame of
  every arc and confirms box hits against SAM silhouettes. Eyeballing two or
  three frames misses exactly the endpoint frames that matter.
- **Land on the surface where it actually is, not where the timeline says it
  starts.** "Onto the hedge" landed on the hedge's *timeline* start while the
  hedge's leading end was still half a second from the ball's column — the
  ball stood on hedge-top height over sidewalk pixels. A surface handover
  must be keyed to when the surface's own pixels reach the column (read it
  off the detections/ledges), and the hop that gets the ball there lands at
  that moment, not at the label's edge.
- **Level changes need a beat.** A ground line that steps down or up (kerb,
  platform) with the ball just gliding across reads as a glitch; a small
  scripted `hop` (auto, uncued, unscored) at the step is what makes the
  change legible. Corollary: never prompt the player for one — a press that
  scores nothing teaches the wrong lesson.
- **Air time comes from gravity, not from the obstacle alone.** T ≈
  k·√(height) with k calibrated once from an accepted jump (~2.4 s per √frame-
  height on this clip). An arc stretched to outlast a crossing without raising
  its height reads as moon-float — the 2.6 s people jump was "awkward" until
  it became 1.9 s at a taller peak. When a crossing demands more hang than
  gravity gives that height, raise the peak or move the takeoff; never just
  slow the fall.
- **Occlude with the thinnest thing that qualifies.** A ball slipping behind
  a sign post reads right; the same ball swallowed by a car-wide strip reads
  as rolling *under* the car — even when the car really is nearer. And an
  occluder mid-arc where the ball plainly sails **above** the object
  (the grey car at 8 s) makes the landing flicker behind-then-in-front.
  Preference order: pole/sign over vehicle; suppress entirely where the arc
  goes over the top.
- **Exits need punctuation.** A ball that ceases to render reads as a bug; a
  0.45 s expanding, fading puff at the same spot reads as a character leaving
  the stage. Any authored disappearance should spend a few frames saying so.
- **Some "obstacles" are depth stories, not jumps.** When the thing the ball
  grazes is *nearer the camera* than the ball's lane (box base clearly below
  the resting line), no arc geometry fixes the overlap — the honest render is
  the ball passing behind it, which the occlusion depth rule provides for
  free once the class is in `occluderLabels`. Trying to out-jump a
  foreground object produces absurd arcs; occluding behind a same-plane
  object produces a swallowed ball. Depth decides, per object, measured.
- **Depth from screen height needs the right reference line.** For occlusion,
  "box base below the line = nearer the camera" works — but only against the
  **ball's own resting line**, and only while the ball is on a true ground
  surface. Compared against the per-frame segmented line, a parked car notches
  the plane and un-earns a pole the ball plainly fronts; compared while the
  ball rides a railing, a lot post *beyond* the fence reads as nearer. Where
  the assumption can't hold, don't occlude: a ball wrongly swallowed reads far
  worse than one wrongly in front. Every threshold in `occlude.py` was set by
  rendering the disagreeing frames and looking (three of five auto-derived
  candidates were wrong until the reference was fixed; then seven of seven
  were right, including two the hand-authored spans had missed entirely).

- **An arc is one parabola, take-off point to landing point — nothing else.**
  Three rounds of the 7.15s rail→road vault taught this the long way. Lift
  above the *live* line let terrain leak into flight (ball sank mid-rise,
  hovered at a doubled apex, swelled 37% on the way up). Anchoring the rise
  and *blending* the drop into the descent fixed those but kinked the motion
  right after the apex — the blend's onset tripled the downward acceleration
  for a beat, a visible lurch at 7.87s. Any piecewise easing of a flight path
  puts a second-derivative seam somewhere, and the eye finds it. The answer
  was never easing: constant acceleration through the whole arc,
  `g = (√(2h)+√(2(h+d)))²/T²`, take-off speed `√(2gh)`, landing predicted
  from the level at takeoff, touchdown where the parabola meets the live
  line. Flat landings (`d=0`) reduce to the old constants exactly, so only
  the arcs that were wrong changed. Size (not position) still eases across
  the descent — depth is a fact, not physics, and easing it is invisible.
- **The clearance check must exempt what a shipped occluder strip covers.**
  The overlap check always did; clearance didn't, and passed the 40.71s arc
  against a near-plane lamppost by a 0.004 margin of luck — any honest change
  to arc geometry then "broke" it. Occlusion is the exemption, not depth
  alone: the 9.45s van's base also runs past the frame bottom, but no strip
  ships there, so the arc must truly clear it. Sibling checks that walk the
  same frames must share their exemption lists, or a fix in one geometry
  surfaces phantom violations in the other.

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
