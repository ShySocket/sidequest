# Resources

The reusable pieces of this project, indexed. Everything here was built for
IMG_3775.mov but written to work on any car-window clip.

## Documents in this folder

| Doc | What it is |
|---|---|
| [PIPELINE_PLAYBOOK.md](PIPELINE_PLAYBOOK.md) | Step-by-step: a new clip (+ optional marker overlay) → playable, audited level |
| [AUDIT_CRITERIA.md](AUDIT_CRITERIA.md) | The frame-by-frame audit logic as a portable spec — checks, thresholds, and why each rule is what it is |
| [LESSONS.md](LESSONS.md) | Hard-won gotchas: Unity, video, CV, and process traps this project hit so the next one doesn't |
| [CV_RUNNER_HANDOUT.md](CV_RUNNER_HANDOUT.md) | Index of the live-camera/live-sensor session: SidequestV1 GPS+camera fixes, the SidequestCV game (YOLOX + Fast-SCNN via Sentis), the headless test harness (`testharness/`), and the fixture generator (`scripts/`) |

## The analyzer (`tools/video-analyzer/`)

A `uv`-managed Python package. Every stage is a CLI subcommand of `analyze`,
each caches its output, and all of it runs locally on Apple Silicon (MPS) with
no accounts or API keys. All models are Apache-2.0/MIT (commercial-safe).

| Module | Reusable capability |
|---|---|
| `decode.py` | Video probe + downscaled frame iteration, rotation-aware |
| `speed.py` | Ego-motion speed from sparse LK flow: interior/oncoming rejection, physical acceleration limiting, measured travel direction |
| `distance.py` | The core idea: strictly-monotonic, invertible time↔distance maps. Everything is keyed on distance so a level survives any playback speed |
| `segment.py` | SAM 2 ground segmentation → per-column run line, plausibility gating (wall detection), compact `.npz` caching |
| `detect.py` | Zero-shot obstacle naming with Grounding DINO; label normalization |
| `ledges.py` | Top edges of runnable things (railings, hedge tops) — detection boxes → SAM 2 masks → cleaned per-column lines |
| `track.py` | Coloured-marker tracking (hue+shape+continuity vs. red-brick footage), jump-arc extraction, ground-baseline recovery, parked-marker detection |
| `ambient.py` | Per-path-sample light sampled from the footage, median-normalized — drives the ball's material at runtime |
| `occlude.py` | Ball-behind-object occlusion, derived not authored: depth from box-base vs ball line, per-object tracks, SAM 2 silhouette atlas the game uses as an alpha mask |
| `transcode.py` | Playback-friendly copies (720p H.264, no audio) via the pip-installed static ffmpeg |
| `authored.py` | Timeline + marker + ledges + detections → the level file: ledge snapping, ground clamping, jump-window fitting, seam blending |
| `audit.py` | **The audit**: simulates the character exactly as the game moves it and verifies every frame against footage evidence. Exit code = regression gate |
| `overlay.py` | Debug overlays rendered back onto footage — the human acceptance test |
| `level.py` / `obstacles.py` | The CV-only level path (no marker needed): surface segments, run-line-notch obstacle events |

Runnable end-to-end: `tools/video-analyzer/new-clip.sh` (see the playbook).

## The capture app (`SidequestCapture/`)

Native iOS one-button recorder for making new map clips: 1080p30 video out
the vehicle window + GPS (position/speed) + 100 Hz device motion, all on one
monotonic clock. Sessions land in the Files app / Finder (file sharing
enabled); `resources/scripts/ingest_capture.py` validates a copied session
and emits `speed.csv` (video-time-keyed measured speed — replaces the
optical-flow speed *estimate* in `speed.py`) plus `track.geojson`. See
[SidequestCapture/README.md](../SidequestCapture/README.md).

## The game (`SidequestV1/Assets/Scripts/VideoLevel/`)

| Script | Reusable capability |
|---|---|
| `VideoLevel*.cs` | JsonUtility-safe level schema + binary-search lookups (time↔distance, ground, column, surface, ambient, screen speed) |
| `LevelDirector.cs` | Distance-authoritative playback; Auto mode (native at desk / vehicle-speed on phone); async-seek-safe video sync; runtime self-heal of the GPS stack |
| `VideoRunnerCharacter.cs` | Kinematic character over a moving ground line: per-cue heights and air times, critically damped follow, depth-step dodge |
| `VideoRunnerBallView.cs` | Presence tricks in one place: contact shadow, roll-without-slip, squash/stretch, footage-driven lighting, surface feel |
| `VideoBackground.cs` | Video-frame→world mapping (letterbox-exact), lazy init, RenderTexture sizing, masked foreground strip (occlusion) |
| `Resources/*.shader` | URP shaders with **both** `Universal2D` + `UniversalForward` passes — work under either renderer (see LESSONS) |
| `Editor/VideoRunnerSetup.cs` | Scene generator; `Tools > Sidequest > Build Video Runner` |
| `Tests/` | EditMode contract tests (incl. parsing the real shipped level) + PlayMode pixel test proving the video actually renders |

## Analysis outputs worth keeping (`tools/video-analyzer/out/`, gitignored)

Regenerable caches: `*.surfaces.npz`, `*.ledges.npz`, `*.detections.json`,
`*.track.json`. Cheap to keep, expensive to recompute (SAM 2/DINO passes).
The committed artifacts are `timeline.json` (the design) and the authored level
in `StreamingAssets` (the product).
