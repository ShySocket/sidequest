"""Command line entry point."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from tqdm import tqdm

from .decode import iter_frames, probe
from .distance import build_distance_map, find_stopped_spans
from .overlay import render as render_overlay
from .speed import SpeedConfig, estimate_speed


def _command_speed(args: argparse.Namespace) -> int:
    video = Path(args.video).expanduser().resolve()
    if not video.exists():
        print(f"error: no such video: {video}", file=sys.stderr)
        return 1

    info = probe(video)
    print(
        f"{video.name}: {info.width}x{info.height} @ {info.fps:.3f} fps, "
        f"{info.frame_count} frames, {info.duration:.2f}s, rotation {info.rotation:g}deg"
    )

    total = info.frame_count // args.stride if info.frame_count > 0 else None
    config = SpeedConfig()

    with tqdm(total=total, unit="frame", desc="optical flow") as progress:
        frames = iter_frames(info, analysis_width=args.analysis_width, stride=args.stride)
        result = estimate_speed(frames, info, config, progress=progress)

    distance_map = build_distance_map(result.times, result.speeds)
    stops = find_stopped_spans(
        result.times,
        result.speeds,
        speed_threshold=result.stop_threshold,
        min_duration=args.min_stop_duration,
    )

    print()
    print(f"samples              {result.diagnostics['sample_count']}")
    print(f"median speed         {result.diagnostics['median_speed_px_per_s']:.1f} px/s")
    print(f"mean confidence      {result.diagnostics['mean_confidence']:.3f}")
    print(f"stop threshold       {result.stop_threshold:.1f} px/s")
    print(f"total distance       {distance_map.total_distance:.1f} relative units")
    print(f"stopped spans        {len(stops)}")
    for span in stops:
        print(f"  {span.start_time:7.2f}s -> {span.end_time:7.2f}s  ({span.duration:.2f}s)")

    if args.output:
        output = Path(args.output).expanduser().resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        times, distances = distance_map.resample(args.map_samples)
        payload = {
            "source": {
                "file": video.name,
                "fps": info.fps,
                "width": info.width,
                "height": info.height,
                "duration": info.duration,
            },
            "distanceUnits": "relative",
            "analysis": {
                "analysisWidth": args.analysis_width,
                "stride": args.stride,
                **result.diagnostics,
            },
            "timeToDistance": [[round(t, 5), round(d, 5)] for t, d in zip(times, distances)],
            "stoppedSpans": [
                {"start": round(s.start_time, 3), "end": round(s.end_time, 3)} for s in stops
            ],
            "speedCurve": [
                [round(t, 5), round(v, 3)] for t, v in zip(result.times, result.speeds)
            ],
        }
        output.write_text(json.dumps(payload, indent=2))
        print(f"\nwrote {output}")

    if args.overlay:
        overlay_path = Path(args.overlay).expanduser().resolve()
        total = info.frame_count // args.overlay_stride if info.frame_count > 0 else None
        with tqdm(total=total, unit="frame", desc="overlay") as progress:
            render_overlay(
                info,
                result,
                distance_map,
                overlay_path,
                config=config,
                stride=args.overlay_stride,
                progress=progress,
            )
        print(f"wrote {overlay_path}")

    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="analyze",
        description="Turn car-window video into a Sidequest level file.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    speed = subparsers.add_parser(
        "speed", help="estimate the ego-motion speed curve and time<->distance map"
    )
    speed.add_argument("video", help="path to the source video")
    speed.add_argument("-o", "--output", help="write the speed/distance JSON here")
    speed.add_argument("--analysis-width", type=int, default=960)
    speed.add_argument("--stride", type=int, default=1, help="analyse every Nth frame")
    speed.add_argument("--map-samples", type=int, default=2000)
    speed.add_argument("--min-stop-duration", type=float, default=0.5)
    speed.add_argument("--overlay", help="render a debug overlay video here")
    speed.add_argument("--overlay-stride", type=int, default=1)
    speed.set_defaults(func=_command_speed)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
