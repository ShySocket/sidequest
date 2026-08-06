# Sidequest

A runner that lives in the world outside a car window. A phone propped against
the glass shows the street going by; a character runs alongside the vehicle —
on sidewalks, railings, hedge tops — jumping and dodging what the street
brings, its progress driven by the vehicle's real motion.

This repo is the **prerecorded-video milestone**: the same game, played over a
recorded clip, with a computer-vision pipeline turning footage into levels. It
exists to prove the mechanics and build the tools the real-time version needs.

## Map

| Path | What |
|---|---|
| `resources/` | **Start here.** Index of reusable pieces, the [pipeline playbook](resources/PIPELINE_PLAYBOOK.md), the [audit spec](resources/AUDIT_CRITERIA.md), and [lessons learned](resources/LESSONS.md) |
| `tools/video-analyzer/` | The offline CV pipeline (`uv run analyze ...`): speed, surfaces, obstacles, marker tracking, level authoring, and the frame-by-frame audit |
| `SidequestV1/` | The Unity 6 project. `Assets/Scripts/VideoLevel/` is the video-runner; `Assets/Scripts/Speed/` the GPS+IMU motion estimator; docs in `Assets/Documentation/` |

## Quickest start

```bash
cd tools/video-analyzer && uv sync
./new-clip.sh /path/to/clip.mov            # full pipeline + audit gate
```

Then in Unity: **Tools > Sidequest > Build Video Runner**, press Play.
Space = jump, S = dodge, R = restart, arrows = scrub, F1 = debug HUD.
