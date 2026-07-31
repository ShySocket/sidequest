"""Command line entry point."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np
from tqdm import tqdm

from .decode import VideoInfo, iter_frames, probe
from .distance import DistanceMap, build_distance_map, find_stopped_spans
from .overlay import render as render_overlay
from .segment import GroundSegmenter, SurfaceFrame, load_surfaces, save_surfaces
from .speed import SpeedConfig, SpeedResult, estimate_speed


def _resolve_video(raw: str) -> Path | None:
    video = Path(raw).expanduser().resolve()
    if not video.exists():
        print(f"error: no such video: {video}", file=sys.stderr)
        return None
    return video


def _describe(info: VideoInfo, video: Path) -> None:
    print(
        f"{video.name}: {info.width}x{info.height} @ {info.fps:.3f} fps, "
        f"{info.frame_count} frames, {info.duration:.2f}s, rotation {info.rotation:g}deg"
    )


def _make_ground_mask_lookup(
    surfaces: list[SurfaceFrame],
    analysis_width: int,
):
    """Nearest-in-time ground mask for a frame, for masking optical flow.

    Surfaces are usually sampled at a stride, so an exact index match is not
    guaranteed; the surface changes slowly enough that nearest-in-time is fine.
    """
    times = np.array([s.time for s in surfaces])

    def lookup(frame):
        position = int(np.argmin(np.abs(times - frame.time)))
        surface = surfaces[position]
        # An implausible mask (a wall, or almost nothing) is worse than no mask;
        # fall back to the fixed ROI band rather than tracking the wrong plane.
        if not surface.is_plausible_ground or surface.valid_fraction < 0.2:
            return None
        mask = surface.ground_mask()
        if mask.shape[1] != analysis_width:
            mask = cv2.resize(
                mask,
                (analysis_width, int(round(mask.shape[0] * analysis_width / mask.shape[1]))),
                interpolation=cv2.INTER_NEAREST,
            )
        return mask

    return lookup


def _run_speed(
    info: VideoInfo,
    args: argparse.Namespace,
    surfaces: list[SurfaceFrame] | None,
) -> tuple[SpeedResult, DistanceMap, SpeedConfig]:
    config = SpeedConfig()
    total = info.frame_count // args.stride if info.frame_count > 0 else None
    ground_mask = (
        _make_ground_mask_lookup(surfaces, args.analysis_width) if surfaces else None
    )

    label = "optical flow (ground-masked)" if surfaces else "optical flow"
    with tqdm(total=total, unit="frame", desc=label) as progress:
        frames = iter_frames(info, analysis_width=args.analysis_width, stride=args.stride)
        result = estimate_speed(
            frames, info, config, progress=progress, ground_mask=ground_mask
        )

    return result, build_distance_map(result.times, result.speeds), config


def _report_speed(result: SpeedResult, distance_map: DistanceMap, stops) -> None:
    print()
    print(f"samples              {result.diagnostics['sample_count']}")
    print(f"median speed         {result.diagnostics['median_speed_px_per_s']:.1f} px/s")
    print(f"mean confidence      {result.diagnostics['mean_confidence']:.3f}")
    print(f"stop threshold       {result.stop_threshold:.1f} px/s")
    print(f"total distance       {distance_map.total_distance:.1f} relative units")
    print(f"stopped spans        {len(stops)}")
    for span in stops:
        print(f"  {span.start_time:7.2f}s -> {span.end_time:7.2f}s  ({span.duration:.2f}s)")


def _command_speed(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    surfaces = None
    if args.surfaces:
        surfaces_path = Path(args.surfaces).expanduser().resolve()
        if not surfaces_path.exists():
            print(f"error: no such surfaces file: {surfaces_path}", file=sys.stderr)
            return 1
        surfaces = load_surfaces(surfaces_path)
        print(f"masking flow with {len(surfaces)} cached ground surfaces")

    result, distance_map, config = _run_speed(info, args, surfaces)
    stops = find_stopped_spans(
        result.times,
        result.speeds,
        speed_threshold=result.stop_threshold,
        min_duration=args.min_stop_duration,
    )
    _report_speed(result, distance_map, stops)

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
                "groundMasked": surfaces is not None,
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
                surfaces=surfaces,
                progress=progress,
            )
        print(f"wrote {overlay_path}")

    return 0


def _command_surface(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    segmenter = GroundSegmenter()
    total = info.frame_count // args.stride if info.frame_count > 0 else None

    surfaces: list[SurfaceFrame] = []
    with tqdm(total=total, unit="frame", desc="segmenting") as progress:
        for frame in iter_frames(
            info, analysis_width=args.analysis_width, stride=args.stride
        ):
            surfaces.append(segmenter.analyze(frame))
            progress.update(1)

    if not surfaces:
        print("error: no frames were segmented", file=sys.stderr)
        return 1

    coverage = np.array([s.coverage for s in surfaces])
    valid = np.array([s.valid_fraction for s in surfaces])

    print()
    print(f"device               {segmenter.device}")
    print(f"frames segmented     {len(surfaces)}")
    print(f"median coverage      {np.median(coverage):.1%}")
    print(f"median valid columns {np.median(valid):.1%}")
    print(f"frames below 20% valid columns: {int((valid < 0.2).sum())}")

    output = Path(args.output).expanduser().resolve()
    save_surfaces(output, surfaces)
    print(f"\nwrote {output} ({output.stat().st_size / 1e6:.1f} MB)")
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
    speed.add_argument(
        "--surfaces",
        help="cached surfaces .npz; masks flow to the ground plane, which removes "
        "the depth confound described in speed.py",
    )
    speed.add_argument("--overlay", help="render a debug overlay video here")
    speed.add_argument("--overlay-stride", type=int, default=1)
    speed.set_defaults(func=_command_speed)

    surface = subparsers.add_parser(
        "surface", help="segment the ground surface and extract the run line"
    )
    surface.add_argument("video", help="path to the source video")
    surface.add_argument("-o", "--output", required=True, help="write surfaces .npz here")
    surface.add_argument("--analysis-width", type=int, default=960)
    surface.add_argument("--stride", type=int, default=2)
    surface.set_defaults(func=_command_surface)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
