# The frame-by-frame audit, as a portable spec

Implementation: `tools/video-analyzer/src/video_analyzer/audit.py`. This
document is the logic itself, written so it can be re-implemented elsewhere —
e.g. inside Unity for the future real-time version — or re-tuned deliberately
rather than by accident.

**Premise.** A level can pass every unit test and still look wrong, because
tests check the file and the eye checks the frame. The audit closes that gap:
simulate the character *exactly as the game moves it* (same arc maths, same
per-cue heights and air times, taps included) at video rate, and hold every
frame against evidence measured from the footage. Report signed margins, not
pass/fail, so fixes can be sized. Non-zero exit = regression gate.

The one meta-rule that made it work: **when the audit and the game disagree
about motion, one of them is wrong and it must be found** — every constant here
(arc formula, clamps, dodge step) exists identically in
`VideoRunnerCharacter`/`VideoRunnerBallView`, or the audit certifies fiction.

---

## Check 1 — Support ("never looks like it's floating")

For every frame where the character is grounded (not jumping, dodging, hidden,
or within ±0.7 s of a surface handover — a handover is a slide, by design):

- **Ledge surfaces** (`rail`, `hedge`): the ball's ground line must be within
  `SUPPORT_TOLERANCE = 0.05` frame-heights of that surface's measured top edge.
  Only the *named* surface's ledge binds — a background hedge's top has no say
  over a ball standing on grass.
- **Ground surfaces** (`floor`, `sidewalk`, `grass`): the segmented line is the
  region's **far edge**, so anywhere at or below it is supported. Only being
  *above* it is floating. (Getting this wrong flagged half the level.)

Evidence discipline, learned violation by violation:

- Median over ±0.25 s of frames — one segmentation frame can't accuse anyone.
- **Bimodal rejection**: if the edge samples spread > 0.15 (wall passages flap
  between open ground and a bottom sliver), the median describes nothing that
  exists → no evidence, not bad evidence.
- Thin-region rejection: far edge below y = 0.88 → region too thin to judge.
- Ledge lines at the frame's extremes (y < 0.08 or > 0.97) are broken frames.
- **Occlusion exemption**: an obstacle box overlapping the column whose bottom
  reaches the ball's ground hides the footing — a van in front of the ball is
  not the ball floating.
- Violations must persist ≥ 0.30 s and never bridge an exempt region.
- No measurable evidence ⇒ *supported* (can't call it floating), and the
  supported-% reported separately keeps this honest.

## Check 2 — Clearance ("tap in the window ⇒ never touch")

For each jump cue — where the cue time is the **arc start** (a tap is a
takeoff) and the cue carries its drawn `airTime`:

- Audit taps at the window's early edge, centre, and late edge. The guarantee
  is to the player, not the ideal replay.
- Arc: `lift(t) = 4·h·φ(1−φ)`, `φ = (t − tap)/airTime`, `h` clamped to
  [0.12, 0.50], `airTime` to [0.6, 3.4] — the game's own formula.
- Ownership: the cue owns boxes crossing the column within **±0.55 s of the
  drawn peak**. A pole most of a second away is a different object.
- Candidate boxes: non-surface classes, overlapping the ball's x-extent, whose
  bottom sits within 0.18 of the ball's ground (things standing in its path).
- Requirement per frame the box overlaps:
  `lift ≥ min(0.45·obstacleHeight, 0.85·h) · widthFactor`, with
  `widthFactor = clamp(boxWidth/0.06, 0.45, 1.0)`.

**Why not box-disjointness:** the designer's own drawn arc passes *inside* the
SUV's box — a parked SUV's box towers over any physical jump. What reads as a
vault is being airborne and high while the obstacle crosses. The width factor
stops thin poles demanding car-sized clearance (a near-measure-zero feasible
window otherwise).

The dual of this check lives in authoring (`fit_jump_windows`): windows are
*computed* so the check passes — occupancy measured from detections, feasible
tap range solved, cue re-centred on it (iterated, since re-centring moves the
peak), window sized to fit, and the arc lengthened when the obstacle crosses
slower than the drawn air time covers. Fitter must be ≥ as conservative as the
audit (it uses the full marker diameter as radius, plus slack).

## Check 3 — Dodge ("moving aside makes sense")

A dodge is a **lane change toward the camera**: the ball steps down
(`+0.09` frame-heights, sinusoidal out-and-back over 0.55 s), grows via depth
scaling, and passes *in front of* the sign — overlapping its box is the point,
not a collision. Audited claims:

- A sign/pole genuinely crosses the column within ±1 s of the cue (a dodge
  with nothing there is a broken cue).
- The step is ≥ 0.07 frame-heights (big enough to read as a lane change).

## Check 4 — Size ("the ball is the size it should be")

Reference diameter = the marker's own drawn size (`markerDiameter`). Perspective
scale = lerp over the level's **own** 5th–95th percentile ground heights.
Violations: any single-frame step > 3 % of the drawn diameter, or the total
range outside 0.7–1.35× drawn. Exempt: hidden spans (invisible balls can't look
wrong) and jump arcs (the landing spot's depth arriving is a smooth ramp).

---

## Threshold table

| Constant | Value | One-line why |
|---|---|---|
| SUPPORT_TOLERANCE | 0.05 fh | reads as rested |
| SUPPORT_MIN_SPAN | 0.30 s | single frames are noise |
| bimodal spread | 0.15 fh | median of two worlds is neither |
| thin-region edge | 0.88 | bottom sliver can't judge |
| ownership window | ±0.55 s of peak | the thing under the apex |
| required-lift caps | 0.45·obh / 0.85·h | half the obstacle, honestly deliverable |
| widthFactor floor | 0.45 @ ≤0.027 fw | poles aren't vans |
| dodge step | 0.09 fh | visible lane change |
| size step | 3 %/frame | ≈0.5 % of screen height — imperceptible |
| height clamp | 0.12–0.50 fh | on-screen, non-trivial |
| airTime clamp | 0.6–3.4 s | playable, matches drawn arcs |

fh = frame heights, fw = frame widths, obh = obstacle height above ground.
