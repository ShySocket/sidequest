---
name: fixgame
description: >
  Apply a described gameplay/motion change at an approximate video timestamp to
  the Sidequest video-runner (the black ball running over IMG_3775 footage), by
  driving the full measure → edit timeline.json → author → audit → render-verify
  → ship-to-Unity loop. Use this skill whenever the user reports something about
  the ball's movement, jumps, dodges, landings, occlusion, surfaces, timing,
  warnings, or realism at or around a time in the clip — e.g. "at 27.4 the ball
  clips the sign", "the jump at 34 looks floaty", "it should go behind the pole
  around 41", "make it land on the sidewalk at 18.6" — even if they don't say
  "fix" or name a file. Any timestamped complaint or request about the
  video-runner's motion is this skill's job.
---

# fixgame — timestamped motion fixes for the video-runner

The input is a change plus a rough time ("~26s the ball doesn't look on the
hedge"). The output is that change **measured, applied, proven, and shipped** —
audit-clean, frame-verified, running in Unity. Never eyeball a number when the
pipeline can measure it; never declare a fix done that a rendered frame hasn't
confirmed.

First use in a session: read `references/architecture.md` (how the game is
built, the event schema, the realism rules). If anything behaves strangely,
`resources/LESSONS.md` records every trap this project has already hit — check
it before debugging fresh.

All commands below run from the repo root unless noted. The analyzer commands
run from `tools/video-analyzer/`.

## Step 0 — Translate the report into a target

The user's timestamp is an estimate read off the in-game clock; the real moment
may sit ±1s away. Restate the request as a *verifiable claim about frames*
before touching anything, e.g. "no frame in 27.3–28.1 shows the ball's disk
intersecting the sign" or "at 18.62 the ball's bottom rests on the sidewalk
kerb". That claim is what Step 4 will check.

## Step 1 — Measure before deciding anything

```bash
uv run --project tools/video-analyzer python .claude/skills/fixgame/scripts/fixgame_probe.py \
    probe --from <t-2> --to <t+2> --step 0.15
```

This prints, per time step: the ball's ground line, column, surface, any active
arc, every detection box near the column, and shipped occluder strips. From it,
read the numbers that will drive the edit:

- **Column-crossing interval** of the obstacle: the span where its box x-range
  overlaps the ball's column. Takeoff must precede its start; landing must
  follow its end. This is the single most important measurement — jumps go
  wrong at their endpoints, not their peaks.
- **Required lift** to clear something: `ground_y − obstacle_top_y` (y grows
  downward), held through the whole crossing, plus a little margin.
- **Depth relation**: obstacle base below the ball's line ⇒ it is nearer the
  camera ⇒ the honest treatment is occlusion or a dodge, not a jump.

Then render the current behaviour so the before-state is on record:

```bash
... fixgame_probe.py render --from <t-2> --to <t+2> --step 0.2 -o /tmp/fix_before.jpg
```

Look at the sheet. Confirm the user's complaint is what you think it is —
half the past mistakes came from fixing a different frame than the one the
user meant.

## Step 2 — Edit `tools/video-analyzer/timeline.json`

The timeline is the single source of design truth; almost every fix is here.
Choose the mechanism by what the complaint actually is:

| Complaint shape | Mechanism |
|---|---|
| touches / clips / lands on an obstacle | move `time` earlier or `airTime` longer around the measured crossing; raise `height` last |
| floaty / hangs / awkward pause | set `airTime` toward gravity: `T = 2.44·√height` (grade all arcs with `fixgame_probe.py grade`) |
| should go behind something | `behindSpans` entry (or add its class to `occluderLabels` and let the depth rule decide) |
| wrongly hidden / should stay in front | `frontSpans` entry |
| needs a sidestep, not a jump | `dodge` event (optional `duration` for an unhurried one) |
| ground-level change looks like a glitch | uncued `hop` event (h≈0.1, T≈0.4) |
| rides too high/low on a surface | `pathAdjust` {from,to,dy} — remember +dy is DOWN/nearer |
| lands off its surface | move the surface's `from` (and the landing) to when its pixels reach the column, read from the probe |
| appears/disappears timing | `hidden` span edges (both edges puff automatically) |

Constraints that keep every edit honest:

- Gravity: airTime at or under `2.44·√height`; under reads as bounce energy
  (fine for hops), over reads as float. Peak lift + ball diameter must stay
  on screen: `ground − height − 0.14 > 0`.
- The event `label` names the obstacle — the audit polices exactly that class,
  so keep labels honest ("sign, on bush", "two cars", "off the hedge").
- Chained events: scoring windows/leads derive from gaps between *scored*
  events at author time; keep ≥0.3s of ground between a landing and the next
  takeoff unless a chain is the intent.
- Write a `_why` on anything non-obvious — the file is the design record.

Unity code is the fix only when the *mechanism* is wrong (a rendering,
scoring, or clock behaviour), not a moment. `references/architecture.md` has
the file map; sync any script change into the test mirror before Step 5.

## Step 3 — Re-author

```bash
cd tools/video-analyzer
uv run analyze author ../../IMG_3775.mov -o out/IMG_3775.authored.json \
    --surfaces out/IMG_3775.surfaces.npz --timeline timeline.json \
    --marker out/IMG_3775.track.json --ledges out/IMG_3775.ledges.npz \
    --detections out/IMG_3775.detections.json --playback-file IMG_3775.play.mp4
```

(Re-runs SAM for occluder silhouettes; a couple of minutes is normal.)

## Step 4 — Audit: the gate

```bash
uv run analyze audit --level out/IMG_3775.authored.json \
    --surfaces out/IMG_3775.surfaces.npz --ledges out/IMG_3775.ledges.npz \
    --detections out/IMG_3775.detections.json --timeline timeline.json \
    --video ../../IMG_3775.mov
```

Non-zero exit = violations, each with a time span and margin. `--video` matters:
it confirms overlap candidates against SAM silhouettes cut from the flagged
frames, so empty box corners don't fail visually-clean arcs. Fix causes in the
timeline (using the Step-1 measurements to size the fix), never thresholds in
the audit — the criteria in `resources/AUDIT_CRITERIA.md` are the product's
definition of "looks real". Loop 2→4 until **0 violations**. If occlusion
changed, also verify the strip contract:

```bash
uv run analyze occluders ../../IMG_3775.mov --level out/IMG_3775.authored.json \
    --detections out/IMG_3775.detections.json --verify --verify-only
```

## Step 5 — Render-verify the exact claim

```bash
... fixgame_probe.py render --from <t-2> --to <t+2> --step 0.2 -o /tmp/fix_after.jpg
```

Read the sheet with the Step-0 claim in hand and judge every frame — takeoff,
each flight step, landing. A clean audit plus a wrong-looking frame means the
claim was mis-stated; go back to Step 0, not forward. Send the before/after
sheets to the user with `SendUserFile` — they are the evidence the fix landed.

## Step 6 — Ship and prove it in-engine

```bash
cd tools/video-analyzer && ./copy-to-unity.sh
```

The user's editor usually holds the project lock, so tests run against the
mirror at `~/Library/Caches/sidequest-compilecheck` (sync first — and never
keep a mirror in the session scratchpad; its cleaner eats files mid-session):

```bash
MIRROR=~/Library/Caches/sidequest-compilecheck
rsync -a SidequestV1/Assets/Scripts/ "$MIRROR/Assets/Scripts/"
rsync -a SidequestV1/Assets/StreamingAssets/IMG_3775.authored.json \
         SidequestV1/Assets/StreamingAssets/IMG_3775.occluders.png \
         "$MIRROR/Assets/StreamingAssets/"
cp SidequestV1/Packages/manifest.json SidequestV1/Packages/packages-lock.json "$MIRROR/Packages/"
/Applications/Unity/Hub/Editor/6000.3.20f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -runTests -testPlatform EditMode -projectPath "$MIRROR" \
  -testResults "$MIRROR/fixE.xml" -logFile "$MIRROR/fixE.log"
/Applications/Unity/Hub/Editor/6000.3.20f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -runTests -testPlatform PlayMode -projectPath "$MIRROR" \
  -testResults "$MIRROR/fixP.xml" -logFile "$MIRROR/fixP.log" \
  -testFilter "GameOverTests|PlayerJumpTests|RunnerMovementTests|VideoBackgroundRenderTests|GpsFailureBehaviorTests"
```

Both suites must be fully green (73 EditMode + 6 PlayMode at time of writing).
For a change whose look only the engine can prove (occlusion strips, the red
miss-flash, the poof, dodge feel), drop a `CaptureShots.cs` into the mirror's
`Assets/Tests/PlayMode/` with the moments to capture — the pattern (drive
`LevelDirector.SpeedMultiplier`, wait on `VideoTime`, `camera.Render()` into a
RenderTexture; ScreenCapture does not work in batchmode) is in
`references/architecture.md`'s doc pointers and in past captures. Remove it
from the mirror when done; it never ships to the main project.

## Step 7 — Record and commit

- If the fix taught a new reusable rule (a new failure mode, a new fix order),
  add it to `resources/LESSONS.md` or `resources/AUDIT_CRITERIA.md`.
- Commit the touched set together so the repo never holds a level that
  disagrees with its timeline: `timeline.json`, any pipeline/Unity code, both
  StreamingAssets artifacts (`IMG_3775.authored.json`,
  `IMG_3775.occluders.png`), and doc updates. Commit message: what changed on
  screen and why, plus the verification line (audit clean, tests green,
  frames verified).

## Done means

Every item, or the fix is not done: the Step-0 claim confirmed in rendered
frames; audit 0 violations (with `--video`); occlusion verify clean if strips
changed; Unity suites green; before/after sheets sent to the user; committed.
