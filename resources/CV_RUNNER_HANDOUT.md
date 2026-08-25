# CV Runner & Live-Sensor Session — Resource Handout

Everything produced in the 2026-08-05/06 session, where it lives, and what
it is for. Two threads of work: making **RunnerPrototype** (SidequestV1)
actually work on a phone, and building **SidequestCV** — the runner whose
world comes from the live rear camera through open-source vision models.

## 1. Live-sensor fixes in `SidequestV1/` (edit in place, already applied)

| Resource | Used for |
|---|---|
| `Assets/Scripts/Speed/UnityGpsSpeedProvider.cs` | Immediate stop/start: forwards every windowed GPS estimate instead of swallowing low-speed readings (~3 s less stop latency), and no longer dead-ends on iOS first launch (`isEnabledByUser` is false there until `Start()` shows the permission dialog) |
| `Assets/Scripts/Speed/MovementEstimator.cs` | 5 s GPS-restart suppression after a motion-confirmed stop, so the lagging position-window can't make the character crawl while the vehicle sits still; accelerometer restarts stay instant |
| `Assets/Scripts/Speed/VehicleSpeedController.cs` | Zeroes game speed the same frame the estimator confirms stationary (no smoothing-filter bleed-out) |
| `Assets/Scripts/Camera/RearCameraBackground.cs` | Camera toggle hardening: explicit 1280×720@30 request (some Android devices otherwise deliver tiny frames), scene-switch fixes, texture reapply on rebind |
| `DefaultRunnerConfiguration.asset` + `RunnerConfiguration.cs` + `RunnerPrototypeSetup.cs` | Tuned defaults in all three places so scene rebuilds don't regress: GPS update distance 1 m → 0.1 m (fixes keep flowing at a standstill), desired accuracy 20 m → 10 m, consecutive-low-speed gate 3 → 1 |
| `ProjectSettings/ProjectSettings.asset` (`activeInputHandler: 2`) | Active Input Handling = **Both** — `Input.location` lives in the legacy module and hangs/fails when the legacy handler is fully disabled. Needs an editor restart |
| `Assets/Tests/EditMode/MovementEstimatorTests.cs` (2 new tests) | Regression pins for the GPS-lag suppression: lagged windowed speed after a real stop must not restart motion; real GPS motion after the window must |

## 2. The CV game — `SidequestCV/` (duplicate project, video assets dropped)

| Resource | Used for |
|---|---|
| `Assets/Models/yolox_tiny.onnx` (20 MB, Apache-2.0, Megvii YOLOX release) | On-device obstacle detection (COCO 80 classes) via Sentis, 416×416 letterboxed, ~4 Hz |
| `Assets/Models/fast_scnn_288x480.onnx` (4.5 MB, Apache-2.0, PINTO model zoo export, Cityscapes 19 classes) | On-device surface segmentation: which lanes are road / sidewalk / vegetation, ~2 Hz. mmseg normalization is baked into the compiled Sentis graph |
| `Assets/Scripts/CV/*.cs` | The pure-logic pipeline, all headless-testable: `YoloxPostProcessor` (grid decode + class-wise NMS), `LetterboxMath`, `SurfaceAnalyzer` (class map → per-lane surfaces), `DetectionEventMapper` (boxes → lane events), **`ObstacleDirector`** (the fairness engine — reaction-time contract, cooldown dedup, never-block-every-lane, speed-aware windows), `CollisionJudge`, `CvClassCatalog` |
| `Assets/Scripts/CVGame/*.cs` | The MonoBehaviour layer: `LiveCameraFeed` (rear camera + procedural-street fallback), `DetectionRunner` / `SegmentationRunner` (Sentis workers, async GPU readback, graceful no-model degradation), `SphereRunnerController` (lanes, swipe/tap, jump physics), `ObstacleField` (pooled primitive obstacles), `CvGameDirector` (game loop, stop-resolves-scene rule, HUD hints), `ProceduralStreetTexture`, `DebugDetectionOverlay` (F1 / three-finger tap) |
| `Assets/Editor/CvRunnerSetup.cs` | One-click scene assembly: **Tools → Sidequest → Build CV Runner Scene** |
| `Assets/Tests/EditMode/Cv*.cs` (26 tests) | The proof it works: fixture-exact decoder tests, surface classification, every director invariant, and five 3-minute simulated commutes where an informed player must take **zero** hits and a passive player must be hit |
| `Assets/Tests/Fixtures/` | Reference tensors and expected results generated from the real ONNX models (see §3 generator) — the C# decoder is pinned to the Python reference |
| `Assets/Documentation/CVRunner.md` | The game's own manual: architecture, fairness invariants, degradation ladder, build steps |

## 3. New tools in this folder

| Resource | Used for |
|---|---|
| [`testharness/run_tests.sh`](testharness/run_tests.sh) | Runs all 71 pure-logic tests (`v1`, `cv`, or `all`) **without opening Unity**, using Unity's own bundled Roslyn + .NET runtime. The only way to test while the editor holds the project lock. Verified green from this location |
| [`testharness/UnityShim.cs`](testharness/UnityShim.cs) | Managed reimplementation of the UnityEngine math the speed/CV code touches (Mathf, Vector3, Quaternion incl. Slerp/Euler/AngleAxis) so it compiles outside Unity |
| [`testharness/NUnitShim.cs`](testharness/NUnitShim.cs) | Just-enough NUnit (Assert/Is constraints/TestContext) for the same purpose |
| [`testharness/Runner.cs`](testharness/Runner.cs) | Reflection-based test runner: finds `[Test]` methods, reports PASS/FAIL, exit code for CI |
| [`testharness/reference_street.jpg`](testharness/reference_street.jpg) | The reference photo (bus + pedestrians on a road) behind all CV fixtures — keep it or fixtures can't be regenerated hermetically |
| [`scripts/generate_cv_fixtures.py`](scripts/generate_cv_fixtures.py) | Regenerates `SidequestCV/Assets/Tests/Fixtures/` by running the actual ONNX models in onnxruntime and applying the reference decode/NMS. Run only when models or the photo change, then re-run the C# tests. Verified to reproduce the committed fixtures byte-for-byte |

## 4. Where the big things intentionally are NOT

- The **models** stay in `SidequestCV/Assets/Models/` (Unity imports them);
  they are not duplicated here. Re-download sources: YOLOX-tiny from the
  Megvii YOLOX GitHub release `0.1.1rc0`; Fast-SCNN from PINTO model zoo
  `228_Fast-SCNN` (`resources.tar.gz` on their S3 mirror).
- The Python venv for the fixture generator is disposable —
  `python3 -m venv .venv && .venv/bin/pip install numpy onnxruntime pillow`.
