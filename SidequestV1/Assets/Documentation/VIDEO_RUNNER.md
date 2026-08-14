# Video Runner

A playable runner over the prerecorded clip. The character stands at a fixed
column of the screen, the video scrolls past, and cues arrive at authored moments.

**The choreography is authoritative.** Every jump and dodge is predetermined and
identical on every playthrough: at each event's authored time the motion happens,
whether or not the player pressed anything. Input only *scores* — a press inside
the event's window clears it; a miss flashes the ball red and the arc plays out
anyway. This keeps the motion exactly what was audited frame by frame, while the
game stays a game.

A third event type, `hop`, is pure choreography: a small automatic hop at a
level change (a kerb, the raised platform at 16.35s) that is never cued,
scored, or missed — it exists to make a step in the ground line read as a
move rather than a glitch.

**Air times obey gravity.** T ≈ k·√height, with k calibrated once from an
accepted jump — an arc stretched to outlast an obstacle without gaining
height reads as moon-float. Dodges may carry their own `duration` (the 57.3s
stop-sign step is deliberately unhurried). When the ball enters a hidden
span it leaves with a puff — a soft disc that expands and fades where it
stood — because ceasing to render reads as a bug, not an exit.

**The run starts when the video does.** Right after Play a video decoder's
clock runs ahead of its pictures, which used to read as a frozen frame with
the ball already climbing. The director now waits for the first delivered
frame, holds on it briefly, and then starts — ball and footage moving
together from the beginning. Restart re-runs the same hold.

Separate from `RunnerPrototype`, which is untouched.

## Run it

1. Make a playback copy of the clip (once per clip):

   ```bash
   cd tools/video-analyzer
   uv run analyze transcode ../../IMG_3775.mov -o out/IMG_3775.play.mp4
   ```

2. Generate the level and copy both into StreamingAssets (repeat after editing
   the timeline):

   ```bash
   uv run analyze track /path/to/IMG_3775_V1.mp4 -o out/IMG_3775.track.json --stride 2
   uv run analyze author ../../IMG_3775.mov -o out/IMG_3775.authored.json \
       --surfaces out/IMG_3775.surfaces.npz --marker out/IMG_3775.track.json
   ./copy-to-unity.sh
   ```

3. In Unity: **Tools > Sidequest > Build Video Runner**, then press Play.

The menu item regenerates `Assets/Scenes/VideoRunner.unity` from scratch, so
re-running it is always a safe way back to a working scene.

## Controls

| Input | Action |
|---|---|
| Space / click / tap | Score the nearest open cue (jump or dodge) |
| R | Restart |
| `-` / `=` | Playback speed |
| Left / Right arrow | Scrub 5s |
| T | Toggle the timer |
| F1 | Toggle the debug readout |

A large clock sits top-left showing **video time in seconds**, plus the source
frame number — the units `timeline.json` and the audit are written in. Read a
time off it, note what should change, and type that number straight into the
timeline. It updates every frame (the rest of the HUD refreshes ten times a
second) because a stale reading is the one thing an annotation clock must never
show. `T` hides it for a clean capture.

## Where the movement comes from

A marker animated by hand over the footage (`IMG_3775_V1.mp4`, the red circle)
describes how the character should move far better than anything inferred: it
carries intent. `analyze track` follows it and turns it into the level's ground
path, its horizontal position, and its jumps.

Finding it needs more than colour. Red brick fills large parts of this footage,
and a loose red threshold latches onto buildings — measured, that inflated the
marker's apparent radius from 0.09 to 0.25 of frame height between t=32s and
t=48s. Three constraints separate them: a narrow hue band (marker H=176 against
brick H=35), a circularity test, and continuity between frames.

The tracked arcs agree with the hand-typed timeline closely — 8 of 10 within
0.7s, the 26.4s sign exact — and found one the timeline missed, the hop at 7.8s
where the character leaves the rail.

**The marker supplies timing, position and height; the game supplies the
animation.** An editor interpolates linearly between keyframes, so the raw track
has corners no physical object would have — which is what made the original jumps
look wrong. Replaying it verbatim would replay that. Instead the game takes each
arc's peak time and height and generates a real ballistic curve, with air time
scaling as the square root of height so a tall jump hangs rather than being
flung.

The marker stops being animated at 46.5s of 87.6s. Past that the hand-typed
timeline in `timeline.json` still supplies cues, and the column eases back from
where the marker was left to the default rather than holding the far screen edge
for the rest of the run.

## Riding real surfaces

The ground segmentation answers "where is the road", which is not the question.
Measured against the tracked marker, its foot lies inside the segmented ground
only **38% of the time**, and where it does not it is almost always *above* it —
because the character runs along railings, hedges and kerbs sitting on top of the
road plane.

So each runnable thing gets its own line. `analyze ledges` takes the detection
box for a railing or hedge, prompts SAM 2 with it, and reads the topmost pixel
per column. Detections of one class are unioned first: a hedge comes back as five
boxes covering parts of one continuous thing.

**The timeline names the surface; the ledge supplies the height.** Geometry alone
cannot tell standing on a hedge from passing in front of one, and letting it pick
the nearest ledge put the character on a hedge during stretches that are plainly
floor. So `timeline.json` says which surface, and the extracted ledge says
exactly where its top edge is, per frame, tracking perspective as the car moves.

Current run, matching the described design:

| Time | Surface |
|---|---|
| 0–7.9 s | rail |
| 7.9–24.4 s | floor |
| 24.5–30 s | hedge — jumped onto, signs cleared from up there, slid off at 30 |
| 30–77.5 s | sidewalk |
| 77.5 s+ | grass |

Surface changes how the ball reads: on a rail it rolls faster and its contact
shadow narrows, because that is most of what distinguishes a narrow rail from
open ground.

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

**The lighting comes from the footage, not a scene light.** The analyzer samples
the pixels around where the ball will be, per frame, normalized so the clip's
median daylight is white. The game multiplies its tuned material colours by that
sample: driving through tree shade dims and cools the ball, a sunlit wall warms
it, and the contact shadow fades in shade the way real shadows lose their edge.
The size sweep likewise comes from the level's own path — its 5th/95th
percentile ground heights set the far/near scale range, so it adapts per clip.

Four cues do the work of making it look present, in rough order of importance:

| Cue | Why |
|---|---|
| Contact shadow | Without it the ball reads as a sticker. Size and opacity track height — that is what says *airborne* rather than *bigger* |
| Rolling | A ball that translates without rotating looks dragged. Rate comes from the level's measured screen speed, so it matches the world sliding past |
| Perspective scale | The ground line rises and falls as the road nears and recedes; the ball scales with it or it appears to swim. **Not mid-rise, though** — see below |
| Squash and stretch | Stretch through the arc, squash on landing — makes a jump read as effort rather than a slide upward. Driven by speed as a fraction of take-off, so it means the same at any jump height |

## Design notes

**An arc is ONE parabola in screen space — terrain never leaks into flight.**
The 7.15s car vault takes off from the rail and lands on the road, 0.21 of a
frame lower. Measuring the arc's lift above the *live* ground line let that
drop eat the rise (the ball sagged mid-ascent, hovered at a doubled apex, and
its size grew 37% on the way up); a first repair — hold the take-off ground
through the rise, blend the drop into the descent — fixed those but put a
kink right after the apex: the blend's onset added ~2.6/s² of extra downward
acceleration to gravity's 1.35, a visible lurch at 7.87s. The real rule is
plain ballistics: the flight is a single parabola from the take-off point to
the landing point, peaking `height` above the take-off line, under **constant**
acceleration `g = (√(2h) + √(2(h+d)))²/T²` with take-off speed `√(2gh)`,
where `d` is the landing spot's drop below the take-off (read from the level
at takeoff). For a flat landing (`d = 0`) these reduce exactly to the old
`8h/T²` and `4h/T`, so flat arcs are untouched; a landing below the take-off
simply falls farther than it rose, under the same gravity — which is what
real projectiles do. Touchdown is where the parabola meets the live line.
Only the ball's *size* eases from the take-off spot's depth to the landing
spot's across the descent (`√fallen`, smoothstepped — the descent's own
clock); the contact shadow stays on the live terrain line and reads the
altitude honestly. `audit`'s `arc_params`/`arc_state_at` (and the occluder
verify's motion model) fly the same math, so every offline check places the
ball exactly as the game does.

**Motion is kinematic, not physics-driven.** The ground here is a line sampled
from the video, not a collider, and it slides around the screen as the car
moves. A `Rigidbody2D` resting on a collider that teleports every frame behaves
badly; integrating a jump arc above a moving ground line is simpler and exactly
reproducible, which matters when cues are authored to specific moments.

**A miss does not end the run.** The motion is authoritative, so a missed press
cannot desynchronize anything: the ball flashes red (the albedo itself is pulled
toward red — a black ball reflects almost nothing, so tinting only the light was
invisible), the miss is counted, and the choreographed arc happens regardless.

**Windows and warning leads are derived, not hand-tuned.** At authoring time
each event's scoring window is `clamp(0.8 × the smaller neighbouring gap, 0.24,
0.7)` seconds and its HUD warning lead is `clamp(0.7 × the gap before it, 0.45,
0.9)` — so an isolated jump is forgiving with a genuinely playable warning,
while the tight hedge chain gets correspondingly tight windows. Hops are
invisible to this derivation (they are not played, so they must not shrink a
neighbour's window). Both ship in the level file per event (`windowSeconds`,
`lead`).

**The ball passes behind foreground poles.** The level carries the detection
boxes of poles the ball's column crosses during marked spans; the game re-draws
that strip of the video in front of the ball. The pixels are identical to the
background, so the only visible effect is the ball sliding behind the pole.
Occluders prefer the thinnest thing that qualifies — a ball behind a sign post
reads right, while a car-wide strip reads as rolling *under* the car — and are
suppressed where an arc plainly goes over the top of the object.

## Playback modes

`LevelDirector.playbackMode`:

- **Auto** (default) — NativeRate at a desk, VehicleSpeed on a phone, mirroring
  VehicleSpeedController's own Auto (Mock in the editor, GPS on device). The
  whole chain follows the hardware it runs on, so a build taken into a vehicle
  moves with the vehicle without any settings changed.
- **NativeRate** — advances at the clip's own pace, scaled by the speed
  multiplier.
- **VehicleSpeed** — advances from `VehicleSpeedController.GameSpeed`. If the
  scene never wired a controller, the director builds the whole stack at runtime
  (providers, permission service, configuration defaults), so stale scenes work
  on device too. `referenceGameSpeed` sets which game speed equals the clip's
  own pace.

The video is driven by `playbackSpeed` rather than by seeking each frame:
seeking 1080p HEVC every frame stutters, while a scaled rate is smooth. A
corrective seek only happens when drift exceeds 0.3 s.

## Assets and performance

`Assets/StreamingAssets/` holds the playback clip and the level. StreamingAssets
is copied into builds verbatim and never imported, which is what a `VideoPlayer`
URL source needs. The video is gitignored; the level JSON is committed.

**The game does not play the original clip.** Analysis and playback want
different things from the same footage: analysis runs once, offline, and wants
every pixel; playback has to decode a frame every 16 ms on a phone while the game
also renders. `analyze transcode` produces a 720p H.264 copy with no audio track —
**a quarter of the pixels, 96 MB down to 46 MB**, and H.264 is the codec Android
decodes reliably where HEVC does not. Analysis still uses the original, so nothing
measured is degraded by it.

Which file to play is recorded in the level (`playbackFile`), not in the scene.
A scene holding a stale name silently played the wrong file or nothing at all.

Other things that were costing frames, all of them work repeated every frame to
reach the same answer:

| Was | Now |
|---|---|
| `new MaterialPropertyBlock()` per frame | Allocated once, reused |
| Shadow opacity uploaded every frame | Only when the value moves |
| Background layout recomputed every frame | Only on a resolution or aspect change |
| `VideoPlayer.playbackSpeed` assigned every frame | Only when it changes — each assignment reaches into the native player |
| HUD strings rebuilt twice a frame by IMGUI | Rebuilt 10x a second, read during OnGUI |

## The frame-by-frame audit

`uv run analyze audit` steps through the level at video rate, simulating the
character exactly as the game moves it, and checks every frame against evidence
from the footage. It exits non-zero on violations, so it works as a regression
gate. Three guarantees, currently all clean at 99.7% measurable support:

- **Support** — a grounded ball rests on the named surface's measured line, or
  inside the segmented ground region. No mid-air look.
- **Clearance** — each scheduled arc (takeoff at the authored time, the authored
  air time, a lift-based clearance test) keeps the ball high while its obstacle
  crosses the column. Only the obstacle *owning* the arc is checked (within
  0.55 s of the peak), and the first and last 0.12 s are exempt — "land right
  after the car" is the authored intent, not a violation.
- **Overlap** — no frame of any arc may show the ball's disk intersecting the
  obstacle its label names, at takeoff, in flight, or on landing. Box hits are
  confirmed against SAM silhouettes from the frames themselves
  (`analyze audit --video`), because a detection rectangle's corners are empty
  pixels. Exempt: the ball in front of the obstacle (nearer the camera), or
  behind a shipped occluder strip.
- **Dodge** — each dodge cue has a real sign crossing the column, and the dodge
  is a visible lane change: a step toward the camera, the ball growing as it
  nears, passing in front of the sign.
- **Script conformance** — the emitted level's events match `timeline.json`
  verbatim (time, height, air time within 0.005), so nothing between the design
  and the shipped file can silently drift.

Strict box-disjointness is deliberately not the clearance criterion: a parked
SUV's box towers over any jump and even the designer's drawn arc passes inside
it. The vault reads from being airborne and high, in front of the obstacle.

## Tests

`Assets/Tests/EditMode/VideoLevelTests.cs` covers the level file contract:
that `JsonUtility` can read the schema at all (it has no top-level arrays and no
arrays of arrays, precisely so it can), that time and distance round-trip, that
lookups clamp outside the clip, and that the real authored level parses with
every cue inside the level and the ground line on screen throughout.
