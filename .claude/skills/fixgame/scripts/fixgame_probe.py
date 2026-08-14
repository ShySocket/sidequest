"""Measure and render the video-runner level around a moment in the clip.

The one tool a timeline edit needs before any number is chosen. Three
subcommands, all keyed on video seconds (the unit timeline.json is written in):

  probe   - print the ball's ground line, column, surface, active events,
            detection boxes near the column, and shipped occluder strips for a
            span of times. This is how takeoffs and landings are MEASURED:
            an obstacle's column-crossing interval is read off the box x-range
            sweeping past the ball's column, never guessed from feel.
  render  - composite the scheduled ball over real video frames exactly as the
            game places it (same path, arc, and perspective-size math), and
            write a labelled contact sheet. This is the picture analysis that
            accepts or rejects an edit.
  grade   - grade every arc against gravity (T = k*sqrt(h), k calibrated from
            the 17.0s SUV jump the design accepted). Anything > 1.35x its
            gravity time will read as floating at the apex.

Run from the repo root with the analyzer's environment:

  uv run --project tools/video-analyzer python .claude/skills/fixgame/scripts/fixgame_probe.py \
      probe --from 26.0 --to 29.0 --step 0.15
  ... render --from 26.0 --to 29.0 --step 0.2 -o /tmp/check.jpg
  ... grade
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

# .../<repo>/.claude/skills/fixgame/scripts/fixgame_probe.py -> <repo>
REPO = Path(__file__).resolve().parents[4]
LEVEL = REPO / "tools/video-analyzer/out/IMG_3775.authored.json"
DETECTIONS = REPO / "tools/video-analyzer/out/IMG_3775.detections.json"
VIDEO = REPO / "IMG_3775.mov"

# Screen-space gravity constant, seconds per sqrt(frame-height), calibrated
# from the accepted SUV jump (h 0.44, T 1.62). Approximate - screen pixels per
# metre change with depth - so quick hops under it read as bounce energy and
# are fine; only arcs LONGER than it read as float.
GRAVITY_K = 1.62 / (0.44 ** 0.5)

SKIP_CLASSES = {"sidewalk", "railing", "fence", "tree", "bush"}


class Level:
    def __init__(self, level_path: Path = LEVEL):
        self.data = json.loads(level_path.read_text())
        self.mt = np.array([p["t"] for p in self.data["timeToDistance"]])
        self.md = np.array([p["d"] for p in self.data["timeToDistance"]])
        self.pd = np.array([p["d"] for p in self.data["path"]])
        self.py = np.array([p["y"] for p in self.data["path"]])
        self.px = np.array([p["x"] for p in self.data["path"]])
        self.ps = [p["s"] for p in self.data["path"]]
        heights = np.sort(self.py)
        self.far = float(heights[int(len(heights) * 0.05)])
        self.near = float(heights[int(len(heights) * 0.95)])
        self.diameter = float(self.data.get("markerDiameter") or 0.173)
        self.arcs = [
            (e["time"], e["height"], e["airTime"], e["label"])
            for e in self.data["events"]
            if e["type"] in ("jump", "hop") and e.get("airTime")
        ]

    def distance(self, t: float) -> float:
        return float(np.interp(t, self.mt, self.md))

    def ground(self, t: float) -> float:
        return float(np.interp(self.distance(t), self.pd, self.py))

    def column(self, t: float) -> float:
        return float(np.interp(self.distance(t), self.pd, self.px))

    def surface(self, t: float) -> str:
        i = int(np.searchsorted(self.pd, self.distance(t), side="right")) - 1
        return self.ps[int(np.clip(i, 0, len(self.ps) - 1))]

    def lift(self, t: float) -> tuple[float, str]:
        for tk, h, air, label in self.arcs:
            if tk <= t <= tk + air:
                phase = (t - tk) / air
                return h * 4 * phase * (1 - phase), label
        return 0.0, ""

    def radius(self, t: float) -> float:
        span = self.near - self.far
        depth = 0.0 if span <= 0 else np.clip((self.ground(t) - self.far) / span, 0, 1)
        return self.diameter * float(np.interp(depth, [0, 1], [0.82, 1.2])) * 0.5

    def strips(self, t: float) -> list[dict]:
        d = self.distance(t)
        tol = self.data["totalDistance"] * 0.006
        return [f for f in self.data.get("foreground", []) if abs(f["d"] - d) < tol]


def load_detections():
    det = json.loads(DETECTIONS.read_text())
    times = np.array([f["time"] for f in det["frames"]])
    return det, times


def cmd_probe(args):
    lvl = Level(Path(args.level))
    det, det_times = load_detections()
    w, h = det["width"], det["height"]
    for t in np.arange(args.start, args.end + 1e-9, args.step):
        col, g = lvl.column(t), lvl.ground(t)
        lift, label = lvl.lift(t)
        line = (
            f"t={t:6.2f}  ground={g:.3f} col={col:.3f} surface={lvl.surface(t)}"
            f" lift={lift:.2f}{' <' + label + '>' if label else ''}"
        )
        i = int(np.argmin(np.abs(det_times - t)))
        boxes = []
        if abs(det_times[i] - t) <= 0.25:
            for d in det["frames"][i]["detections"]:
                if d["label"] in SKIP_CLASSES:
                    continue
                x1, y1, x2, y2 = d["box"]
                x1, x2, y1, y2 = x1 / w, x2 / w, y1 / h, y2 / h
                if x2 < col - args.reach or x1 > col + args.reach:
                    continue
                boxes.append(f"{d['label']}[x {x1:.2f}..{x2:.2f} y {y1:.2f}..{y2:.2f}]")
        strips = lvl.strips(t)
        print(line)
        if boxes:
            print(f"          boxes: {' | '.join(boxes)}")
        if strips:
            spans = {f"id{s['id']}" for s in strips}
            print(f"          occluder strips: {sorted(spans)}")


def cmd_render(args):
    import cv2

    lvl = Level(Path(args.level))
    cap = cv2.VideoCapture(str(VIDEO))
    fps = cap.get(cv2.CAP_PROP_FPS)
    tiles = []
    for t in np.arange(args.start, args.end + 1e-9, args.step):
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(round(t * fps)))
        ok, im = cap.read()
        if not ok:
            continue
        H, W = im.shape[:2]
        g, c = lvl.ground(t), lvl.column(t)
        lift, _ = lvl.lift(t)
        r = lvl.radius(t)
        cx, cy, rp = int(c * W), int((g - lift - r) * H), int(r * H)
        cv2.circle(im, (cx, cy), rp, (20, 20, 20), -1)
        cv2.circle(im, (cx, cy), rp, (0, 220, 255), 3)
        cv2.line(im, (cx - 40, int(g * H)), (cx + 40, int(g * H)), (255, 120, 0), 3)
        im = cv2.resize(im, (500, int(500 * H / W)))
        label = f"{t:.2f}s lift={lift:.2f}"
        cv2.putText(im, label, (6, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 0), 4, cv2.LINE_AA)
        cv2.putText(im, label, (6, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 1, cv2.LINE_AA)
        tiles.append(im)
    cap.release()
    if not tiles:
        raise SystemExit("no frames rendered - check the time span")
    while len(tiles) % args.columns:
        tiles.append(np.zeros_like(tiles[-1]))
    rows = [np.hstack(tiles[i : i + args.columns]) for i in range(0, len(tiles), args.columns)]
    cv2.imwrite(args.output, np.vstack(rows), [cv2.IMWRITE_JPEG_QUALITY, 86])
    print(f"wrote {args.output} ({len(tiles)} frames)")


def cmd_grade(args):
    lvl = Level(Path(args.level))
    print(f"gravity constant k={GRAVITY_K:.2f} s per sqrt(frame-height)")
    worst = None
    for tk, h, air, label in lvl.arcs:
        t_g = GRAVITY_K * (h ** 0.5)
        ratio = air / t_g
        flag = "  <-- FLOATY, will hang at the apex" if ratio > 1.35 else (
            "  (snappy)" if ratio < 0.7 else ""
        )
        print(f"{tk:6.2f} {label:26s} h={h:.2f} T={air:.2f} gravity-T={t_g:.2f}{flag}")
        if worst is None or ratio > worst[0]:
            worst = (ratio, tk, label)
    if worst and worst[0] > 1.35:
        raise SystemExit(2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    for name, fn in (("probe", cmd_probe), ("render", cmd_render)):
        p = sub.add_parser(name)
        p.add_argument("--from", dest="start", type=float, required=True)
        p.add_argument("--to", dest="end", type=float, required=True)
        p.add_argument("--step", type=float, default=0.2)
        p.add_argument("--level", default=str(LEVEL))
        if name == "probe":
            p.add_argument("--reach", type=float, default=0.35)
        else:
            p.add_argument("-o", "--output", required=True)
            p.add_argument("--columns", type=int, default=3)
        p.set_defaults(func=fn)

    g = sub.add_parser("grade")
    g.add_argument("--level", default=str(LEVEL))
    g.set_defaults(func=cmd_grade)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
