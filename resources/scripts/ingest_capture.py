#!/usr/bin/env python3
"""Validate and summarize a SidequestCapture session folder.

A session folder (copied off the phone via Finder / Files app / AirDrop)
contains:
    video.mov       the clip
    sensors.jsonl   GPS + device-motion samples, host-clock stamped
    session.json    metadata incl. clock.videoStartHostTime

This script (stdlib only):
  1. validates the trio and the clock mapping,
  2. prints a summary (duration, fixes, distance, speeds, gaps),
  3. writes speed.csv       (t_video_s, speed_mps, hacc_m)  -- video-time keyed
     and    track.geojson   (the GPS path, for a quick map sanity check)
     into the session folder.

speed.csv is the real-world replacement for the optical-flow speed estimate:
feed it to the analyzer's distance stage to build the time<->distance map.

Usage:
    python3 ingest_capture.py /path/to/Sessions/20260810-143201
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


def haversine_m(lat1, lon1, lat2, lon2):
    r = 6371000.0
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp = p2 - p1
    dl = math.radians(lon2 - lon1)
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def fail(msg):
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("session", type=Path, help="session folder from the phone")
    args = ap.parse_args()
    d = args.session

    video = d / "video.mov"
    sensors = d / "sensors.jsonl"
    meta_path = d / "session.json"
    for p in (video, sensors, meta_path):
        if not p.exists():
            fail(f"missing {p.name} in {d}")

    meta = json.loads(meta_path.read_text())
    t0 = meta.get("clock", {}).get("videoStartHostTime")
    t_stop = meta.get("clock", {}).get("stopHostTime")
    if not t0:
        fail("session.json has no clock.videoStartHostTime")
    if meta.get("recordingError"):
        print(f"WARNING: recording ended with error: {meta['recordingError']}")

    gps, motion = [], []
    with sensors.open() as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                print(f"WARNING: bad JSONL at line {i} (truncated write?)")
                continue
            if rec.get("type") == "gps":
                gps.append(rec)
            elif rec.get("type") == "motion":
                motion.append(rec)

    duration = (t_stop - t0) if t_stop else None

    # --- speed.csv: video-time-keyed GPS speed ---
    rows = []
    for g in gps:
        tv = g["t"] - t0
        if tv < 0 or g["speed"] < 0:
            continue  # pre-roll fix or invalid speed
        rows.append((tv, g["speed"], g.get("hacc", -1)))
    speed_csv = d / "speed.csv"
    with speed_csv.open("w") as f:
        f.write("t_video_s,speed_mps,hacc_m\n")
        for tv, v, hacc in rows:
            f.write(f"{tv:.3f},{v:.3f},{hacc:.1f}\n")

    # --- track.geojson: the path, for a one-glance map check ---
    coords = [[g["lon"], g["lat"]] for g in gps if g["t"] >= t0]
    (d / "track.geojson").write_text(json.dumps({
        "type": "Feature",
        "properties": {"name": d.name},
        "geometry": {"type": "LineString", "coordinates": coords},
    }))

    # --- summary ---
    dist = sum(
        haversine_m(a["lat"], a["lon"], b["lat"], b["lon"])
        for a, b in zip(gps, gps[1:])
    )
    speeds = [v for _, v, _ in rows]
    gaps = [
        (a["t"] - t0, b["t"] - a["t"])
        for a, b in zip(gps, gps[1:])
        if b["t"] - a["t"] > 2.0
    ]

    print(f"session   {d.name}")
    if duration is not None:
        print(f"duration  {duration:.1f} s")
    print(f"video     {video.stat().st_size / 1e6:.1f} MB")
    print(f"gps       {len(gps)} fixes ({len(rows)} usable speed rows)")
    if motion:
        span = motion[-1]["t"] - motion[0]["t"]
        rate = (len(motion) - 1) / span if span > 0 else 0
        print(f"motion    {len(motion)} samples @ {rate:.0f} Hz")
    else:
        print("motion    NONE — check motion permissions")
    print(f"distance  {dist:.0f} m")
    if speeds:
        print(f"speed     avg {sum(speeds)/len(speeds)*3.6:.0f} km/h, "
              f"max {max(speeds)*3.6:.0f} km/h")
    for at, gap in gaps:
        print(f"WARNING: {gap:.1f}s GPS gap at t_video={at:.1f}s")
    if not rows:
        print("WARNING: no usable GPS speed rows — was location denied?")

    print(f"\nwrote {speed_csv}")
    print(f"wrote {d / 'track.geojson'}")
    print("\nnext: resources/PIPELINE_PLAYBOOK.md — run the analyzer on "
          "video.mov, with speed.csv as the measured speed source.")


if __name__ == "__main__":
    main()
