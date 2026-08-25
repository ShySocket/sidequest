# Sidequest CV Runner

An endless-runner where the world is the real world. A 3D sphere rolls over a
live rear-camera background; the vehicle you are riding drives the game speed
(GPS + accelerometer fusion, reused from RunnerPrototype), and two on-device
open-source vision models decide what you run on and what you dodge.

## How it works

| Piece | Source | Role |
| --- | --- | --- |
| YOLOX-tiny (`Assets/Models/yolox_tiny.onnx`, Apache-2.0, Megvii) | 416×416, ~4 Hz via Sentis | Detects cars, buses, trucks, people, stop signs, hydrants, benches, potted plants… → obstacle events |
| Fast-SCNN (`Assets/Models/fast_scnn_288x480.onnx`, Apache-2.0, PINTO model zoo export, Cityscapes) | 480×288, ~2 Hz via Sentis | Classifies road / sidewalk / vegetation / terrain per lane → where the sphere can run and where obstacles may spawn |
| `ObstacleDirector` | pure C# | Fairness engine between detections and spawns (see invariants) |
| Speed stack (`VehicleSpeedController` + `MovementEstimator`) | reused from V1 | Vehicle stops → world freezes instantly; vehicle moves → world moves |

The sphere itself never travels forward — the live camera background and the
approaching obstacles create the motion. Swipe left/right to change lanes,
tap to jump (arrows/A/D + space in the editor). Three lives, score =
distance + dodges.

## Fairness invariants (all enforced by tests)

- No spawns while the vehicle is stopped, moving < 1.5 m/s, or when the
  segmentation model sees no runnable surface (pointed at the sky).
- Every spawn arrives no sooner than 1.1 s at the speed it was created at;
  if the vehicle is so fast that even the farthest spawn would be unfair,
  the spawn is dropped, not compressed.
- The same physical object detected frame after frame spawns once
  (per-lane/kind cooldowns), and total spawn rate is capped.
- At every arrival moment at least one runnable lane stays passable, with
  the separation window widened at low speeds where collision windows are
  physically longer.
- Low obstacles (signs, hydrants, benches, bushes) are only treated as
  jumpable above 6 m/s — below that the jump's air time cannot cover the
  collision window, so they count as full lane blockers.
- Two low obstacles in one lane are spaced ≥ 1 s so both can be jumped.
- A full stop (>1.5 s) resolves the frozen scene (obstacles fade, scored as
  dodges): pulling away never replays a compressed, undodgeable wall.
- Vehicles never spawn on a lane the segmentation calls sidewalk.

## Degradation ladder (what happens when things fail)

| Failure | Behavior |
| --- | --- |
| Camera permission denied / no rear camera | Procedural animated street becomes the background and detector input; game stays playable |
| YOLOX model missing or unsupported | HUD shows "CV off"; procedural spawner feeds the same fairness director |
| Fast-SCNN missing | Surface assumed to be road everywhere |
| GPS denied / indoors | Speed stays 0; HUD says "Waiting for GPS…"; nothing spawns |
| Camera pointed away from the road | No surface → spawns pause; hint appears after 3 s |
| Editor play mode | Procedural street + mock speed provider: fully playable at the desk |

## Building

1. Open `SidequestCV` in Unity 6000.3. Let Sentis (2.1.2) import the two
   ONNX files under `Assets/Models` (check the console for import errors).
2. Run **Tools → Sidequest → Build CV Runner Scene** — this creates
   `Assets/Scenes/CVRunner.unity` fully wired and sets it as the build scene.
3. Press Play to verify with the procedural street, then build to device
   (iOS: usage descriptions and Active Input Handling are already set;
   Android: manifest already declares camera + fine location).

## Tests

`Assets/Tests/EditMode/Cv*.cs` — 26 tests, including:

- YOLOX decode + NMS reproduces a Python onnxruntime reference exactly
  (fixtures under `Assets/Tests/Fixtures`, generated from a real street photo).
- Fast-SCNN class map of that photo reads as road; synthetic maps classify
  sidewalk/rail/none lanes.
- Obstacle director invariants above.
- Five 3-minute simulated commutes (accelerate/cruise/stop cycles, noisy
  4 Hz detector stream): an informed reactive player takes zero hits, a
  player who never reacts is always hit, nothing spawns while blind or
  stopped, and the world freezes exactly when the vehicle does.
