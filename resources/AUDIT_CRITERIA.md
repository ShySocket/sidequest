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
- Candidate boxes: non-surface classes, overlapping the ball's x-extent,
  whose base reaches down to the ball's line (a near van's box runs past the
  frame bottom and still counts). A base more than **0.03 above the line**
  stands beyond the ball's lane — the same depth constant the overlap check
  and `occlude.py` use — and owes the arc nothing: the ball passes in front
  of it (a 0.18 tolerance here demanded the 44.7 s chain hop vault a parked
  car two ranks behind the ball). This exempts only *farther* objects; for
  nearer ones occlusion is the exemption, not depth alone — a box a shipped
  occluder strip covers is exempt because the ball passes *behind* it by
  design (the near lamppost sweeping the 40.71 s arc), while the 9.45 s van
  ships no strip and must be cleared for real.
- The flight is ONE parabola in screen space — take-off point to landing
  point, peaking `height` above the take-off line, **constant** acceleration
  `g = (√(2h)+√(2(h+d)))²/T²` (for a flat landing this is the old `8h/T²`).
  Lift is that parabola measured above the **live** ground line: a drop the
  arc carries (rail→road at 7.15 s) adds to the clearance the vault genuinely
  shows; a mid-arc rise subtracts. The ownership peak is where the parabola's
  velocity crosses zero (earlier than mid-arc when the landing sits lower).
  The ball's size holds the take-off spot's depth through the rise and eases
  into the landing spot's across the descent — the game and the audit share
  this in `arc_params`/`arc_state_at`.
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

## Check 2b — Overlap ("jumping over means never touching")

Clearance asks "is the vault high enough to read"; this asks the harder
question a frame-by-frame review asks: **does the ball's disk ever intersect
the obstacle it clears — at takeoff, in flight, or on landing.** One grazed
frame reads as a collision, and the audit walks every frame of every arc.

- Scope: only the obstacle class the event's **label names** (`"sign"` →
  traffic sign, `"car"/"van"/"suv"` → vehicles, `"people"` → person…). A lamp
  pole standing *behind* the hedge shares a box base with the signs planted
  *on* it; geometry cannot tell them apart, but the choreography names its
  target. An arc that names no obstacle ("off the hedge", the level-change
  hops) clears nothing and checks nothing — bystanders with hidden bases
  would otherwise accuse it.
- Ball disk at 0.85× drawn radius (shading rolls off at the rim; mathematical
  tangency doesn't read as touch), elliptical in frame units.
- Exempt: ball **in front** (its resting line clearly below the box base —
  "lands right in front of the car" is intent), and ball **behind a shipped
  occluder** (the strip redraw is the design).
- **A box hit is a candidate, not a verdict.** Detection rectangles overstate
  the object — a hatchback's sloped tail leaves its box's top corner empty,
  and most box hits are exactly such corner grazes. Each candidate is
  confirmed against the object's **SAM silhouette from the frame itself**
  (`analyze audit --video …`); only pixel hits count. On the first run this
  killed 5 of 7 candidates and kept the 2 real ones.

Fixing a real overlap, in order of preference: **take off earlier** (the van's
hood reaches the column before the annotated moment — beating it costs
nothing), **land later** (the parked car crosses the old landing spot —
outlast it), raise the arc, or — when the obstacle is genuinely *nearer* than
the ball's lane (base below its line) — let the **occlusion depth rule** put
the ball behind it instead of pretending they never cross. Never fix it by
shrinking the ball or nudging the path off its measured line.

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

## Check 5 — Occlusion ("behind means behind")

Occluders are *derived*, not authored (`occlude.py`): an object whose detection
box base sits **clearly below the ball's resting line** (≥ `DEPTH_MARGIN =
0.03` fh) is nearer the camera — same ground plane, lower is closer — and the
ball passes behind it. The audit re-checks every shipped occluder sample from
the level file outward:

- **Depth**: box base ≥ ball's line + margin (− 0.02 slack). The reference is
  the ball's own path line, *not* the per-frame segmented line — a parked car
  notches the segmented line toward the camera, and that must not un-earn a
  pole the ball plainly fronts. Support (Check 1) already ties the path to
  footage evidence; this closes the loop without re-measuring through noise.
- **Proximity**: strip centre within `REACH + 0.1` of the ball's column — a
  strip far from the ball occludes nothing and means the tracker drifted.
- **Coherence**: ≥ 2 sightings per track (one is flicker) and centre steps
  ≤ `MAX_CENTER_STEP` between samples (a bigger jump is two objects chained,
  which the game would render as one strip sweeping the gap).
- **Choreography**: no samples during dodges (a dodge steps *in front* of the
  sign — occluding behind it would undo the move) or hidden spans, unless a
  timeline `behindSpans` override forces them.
- **Masks**: uv rects sane, and the atlas the level names ships next to it.

The deeper game-side contract — *behind a real object, or invisible* — is
checked by `analyze occluders --verify`, which mirrors the game's strip
lookup 1:1 (interpolation within a track; past a track's end the last
sighting may linger only **half its own sample spacing**, and it travels
along the track's own velocity rather than freezing — a frozen box left the
silhouette biting the ball where a fast near pole no longer was; a
ball-overlap gate) and rasterizes which ball pixels the silhouette actually
erases, demanding each lie inside a current detection box —
position-interpolated between the bracketing detection frames, because
nearest-frame truth let a mask a tenth of a second stale pass as "on the
object". It also requires every atlas texel outside a silhouette cell to be
black: black occludes nothing, and bilinear sampling bleeds cell borders into
the strip, so anything else bites a clean-edged notch out of the ball with
nothing visibly in front.

Track chaining requires shape continuity as well as centre continuity
(`MAX_SHAPE_RATIO`): a thin background pole and a full-height near pole
sweeping through the same x chained on centre alone, and the game morphed
one into the other.

Derivation-side rules the audit leans on: elevated surfaces (`rail`, `hedge`)
suppress occlusion entirely — furniture the designer put the ball on lines the
near edge of the shot, and no depth test against an invisible footing can
prove otherwise; `frontSpans` suppresses a misread box; `occluderLabels`
widens the class list per clip.

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
| height clamp | 0.12–0.50 fh (derived arcs) / 0.05–0.80 (authored) | authored cues are audited, not re-clamped: clearing frame-tall people takes 0.55 |
| overlap radius | 0.85× drawn | rim shading; tangency doesn't read as touch |
| in-front margin | ball line > base + 0.03 fh | nearer the camera, no contact possible |
| airTime clamp | 0.6–3.4 s | playable, matches drawn arcs |
| DEPTH_MARGIN | 0.03 fh | "clearly below": a wrongly swallowed ball reads worse than a wrongly fronted one |
| REACH | 0.25 fw | strip is invisible anyway; wide reach keeps fast near poles trackable |
| MAX_CENTER_STEP | 0.18 fw | fastest genuine mover measured was 0.16 between sightings |
| MIN_TRACK_SECONDS | 0.15 s | two sightings at detection stride 5 |
| MAX_SHAPE_RATIO | 1.6 | box height barely changes in a sixth of a second; a jump past this is a different object |

fh = frame heights, fw = frame widths, obh = obstacle height above ground.
