"""Command line entry point."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np
from tqdm import tqdm

from .ambient import sample_ambient
from .audit import LevelAuditor
from .authored import MarkerTrack, Timeline, build_authored_level, write_authored
from .decode import VideoInfo, iter_frames, probe
from .detect import ObstacleDetector, load_detections, save_detections
from .distance import DistanceMap, build_distance_map, find_stopped_spans
from .ledges import LedgeExtractor, save_ledges
from .level import build_level, build_surface_segments, write_level
from .obstacles import ObstacleConfig, extract_obstacles
from .overlay import render as render_overlay
from .segment import GroundSegmenter, SurfaceFrame, load_surfaces, save_surfaces
from .speed import SpeedConfig, SpeedResult, estimate_speed
from .track import TrackConfig, find_arcs, ground_baseline, parked_after, smooth, track_dot
from .transcode import TranscodeSettings, transcode


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


def _command_detect(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    detector = ObstacleDetector()
    total = info.frame_count // args.stride if info.frame_count > 0 else None

    frames = []
    width = height = 0
    with tqdm(total=total, unit="frame", desc="detecting") as progress:
        for frame in iter_frames(
            info, analysis_width=args.analysis_width, stride=args.stride
        ):
            height, width = frame.image.shape[:2]
            frames.append(detector.analyze(frame))
            progress.update(1)

    if not frames:
        print("error: no frames were processed", file=sys.stderr)
        return 1

    counts: dict[str, int] = {}
    for entry in frames:
        for detection in entry.detections:
            counts[detection.label] = counts.get(detection.label, 0) + 1

    print()
    print(f"device               {detector.device}")
    print(f"frames processed     {len(frames)}")
    print(f"detections           {sum(counts.values())}")
    for label, count in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"  {label:16s} {count}")

    output = Path(args.output).expanduser().resolve()
    save_detections(output, frames, width, height)
    print(f"\nwrote {output} ({output.stat().st_size / 1e6:.2f} MB)")
    return 0


def _command_level(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    surfaces_path = Path(args.surfaces).expanduser().resolve()
    if not surfaces_path.exists():
        print(f"error: no such surfaces file: {surfaces_path}", file=sys.stderr)
        return 1
    surfaces = load_surfaces(surfaces_path)
    print(f"loaded {len(surfaces)} surfaces")

    detection_frames = None
    if args.detections:
        detections_path = Path(args.detections).expanduser().resolve()
        if not detections_path.exists():
            print(f"error: no such detections file: {detections_path}", file=sys.stderr)
            return 1
        detection_frames, _, _ = load_detections(detections_path)
        print(f"loaded {len(detection_frames)} detection frames")

    result, distance_map, speed_config = _run_speed(info, args, surfaces)
    stops = find_stopped_spans(
        result.times,
        result.speeds,
        speed_threshold=result.stop_threshold,
        min_duration=args.min_stop_duration,
    )

    obstacle_config = ObstacleConfig(character_column=args.character_column)
    obstacles = extract_obstacles(surfaces, distance_map, detection_frames, obstacle_config)

    level = build_level(
        video,
        info,
        distance_map,
        surfaces,
        obstacles,
        stops,
        obstacle_config,
        diagnostics=result.diagnostics,
        map_samples=args.map_samples,
    )

    named = [o for o in obstacles if o.label != "unknown"]
    label_counts: dict[str, int] = {}
    for event in obstacles:
        label_counts[event.label] = label_counts.get(event.label, 0) + 1

    print()
    print(f"total distance       {distance_map.total_distance:.1f} relative units")
    print(f"surface segments     {len(level['surfaceSegments'])}")
    print(f"surface gaps         {len(level['surfaceGaps'])}")
    print(f"obstacles            {len(obstacles)}  ({len(named)} classified)")
    for label, count in sorted(label_counts.items(), key=lambda kv: -kv[1]):
        print(f"  {label:16s} {count}")
    if obstacles:
        spacing = distance_map.total_distance / len(obstacles)
        print(f"mean spacing         {spacing:.1f} units")

    output = Path(args.output).expanduser().resolve()
    write_level(output, level)
    print(f"\nwrote {output} ({output.stat().st_size / 1e6:.2f} MB)")

    if args.overlay:
        overlay_path = Path(args.overlay).expanduser().resolve()
        total = info.frame_count // args.overlay_stride if info.frame_count > 0 else None
        with tqdm(total=total, unit="frame", desc="overlay") as progress:
            render_overlay(
                info,
                result,
                distance_map,
                overlay_path,
                config=speed_config,
                stride=args.overlay_stride,
                surfaces=surfaces,
                detections=detection_frames,
                obstacles=obstacles,
                obstacle_config=obstacle_config,
                progress=progress,
            )
        print(f"wrote {overlay_path}")

    return 0


def _command_author(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    surfaces_path = Path(args.surfaces).expanduser().resolve()
    if not surfaces_path.exists():
        print(f"error: no such surfaces file: {surfaces_path}", file=sys.stderr)
        return 1
    surfaces = load_surfaces(surfaces_path)

    timeline_path = Path(args.timeline).expanduser().resolve()
    if not timeline_path.exists():
        print(f"error: no such timeline: {timeline_path}", file=sys.stderr)
        return 1
    timeline = Timeline.load(timeline_path)

    detection_data = None
    if args.detections:
        det_path = Path(args.detections).expanduser().resolve()
        if det_path.exists():
            frames, det_w, det_h = load_detections(det_path)
            detection_data = (frames, det_w, det_h)
            print(f"detections: {len(frames)} frames, for jump-window fitting")

    marker = None
    if args.marker:
        marker_path = Path(args.marker).expanduser().resolve()
        if not marker_path.exists():
            print(f"error: no such marker track: {marker_path}", file=sys.stderr)
            return 1
        marker = MarkerTrack.load(marker_path)
        print(f"marker: {len(marker.arcs)} arcs, animated to {marker.animated_until:.1f}s, "
              f"diameter {2 * marker.median_radius:.3f}")

    result, distance_map, _ = _run_speed(info, args, surfaces)
    obstacle_config = ObstacleConfig(character_column=args.character_column)
    segments, _ = build_surface_segments(surfaces, distance_map, obstacle_config)

    output = Path(args.output).expanduser().resolve()

    def bake_occluder_masks(tracks):
        """SAM-segment each occluder sighting; the level carries the result.

        Defined here rather than in authored.py because it owns IO: decoding
        the video again and writing the atlas PNG next to the level.
        """
        if args.no_occluder_masks:
            return None

        from .occlude import bake_mask_atlas

        count = sum(len(t.samples) for t in tracks)
        atlas_path = output.with_name(f"{video.stem}.occluders.png")
        with tqdm(total=count, unit="mask", desc="occluder masks") as progress:
            atlas = bake_mask_atlas(
                info, tracks, analysis_width=args.analysis_width, progress=progress
            )
        cv2.imwrite(str(atlas_path), atlas.image)
        atlas.file_name = atlas_path.name
        print(f"wrote {atlas_path} ({atlas_path.stat().st_size / 1e3:.0f} kB)")
        return atlas

    level = build_authored_level(
        video,
        info,
        distance_map,
        segments,
        timeline,
        character_column=args.character_column,
        travel_direction=result.travel_direction,
        marker=marker,
        ledges=Path(args.ledges).expanduser().resolve() if args.ledges else None,
        surfaces_for_clamp=surfaces,
        detection_data=detection_data,
        speed_curve=(result.times, result.speeds),
        analysis_width=args.analysis_width,
        playback_file=args.playback_file,
        mask_baker=bake_occluder_masks,
    )

    # Light the ball from the footage: sample the pixels around where the
    # character will be, per frame, and ship them with the level.
    path_d = np.array([p["d"] for p in level["path"]])
    path_t = np.array([distance_map.time_at(float(d)) for d in path_d])
    path_x = np.array([p["x"] for p in level["path"]])
    path_y = np.array([p["y"] for p in level["path"]])
    total = info.frame_count // 3 if info.frame_count > 0 else None
    with tqdm(total=total, unit="frame", desc="ambient light") as progress:
        colors = sample_ambient(info, path_t, path_x, path_y, progress=progress)
    level["ambient"] = [
        {
            "d": round(float(d), 3),
            "r": round(float(c[0]), 3),
            "g": round(float(c[1]), 3),
            "b": round(float(c[2]), 3),
        }
        for d, c in zip(path_d, colors)
    ]
    luma = colors @ np.array([0.299, 0.587, 0.114])
    print(f"ambient luma         median {np.median(luma):.2f}  "
          f"range {luma.min():.2f}-{luma.max():.2f}")

    counts: dict[str, int] = {}
    for event in level["events"]:
        key = f"{event['type']} ({event['source']})"
        counts[key] = counts.get(key, 0) + 1

    heading = "right-to-left" if level["travelDirection"] < 0 else "left-to-right"
    print()
    print(f"travel direction     {heading} "
          f"({result.diagnostics['direction_agreement']:.0%} of frames agree)")
    print(f"path samples         {len(level['path'])}")
    print(f"events               {len(level['events'])}")
    for kind, count in sorted(counts.items()):
        print(f"  {kind:10s} {count}")
    print(f"hidden spans         {len(level['hidden'])}")
    for span in level["hidden"]:
        print(f"  {span['startTime']:.1f}s -> {span['endTime']:.1f}s")

    occluders: dict[int, int] = {}
    for entry in level["foreground"]:
        occluders[entry["id"]] = occluders.get(entry["id"], 0) + 1
    masked = sum(1 for e in level["foreground"] if e.get("mask"))
    print(
        f"occluders            {len(occluders)} tracks, "
        f"{len(level['foreground'])} samples ({masked} with silhouettes)"
    )

    write_authored(output, level)
    print(f"\nwrote {output} ({output.stat().st_size / 1e6:.2f} MB)")
    return 0


def _command_transcode(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    output = Path(args.output).expanduser().resolve()
    print(f"transcoding to {args.height}p H.264 (no audio)...")
    transcode(video, output, TranscodeSettings(height=args.height, crf=args.crf))

    before = video.stat().st_size / 1e6
    after = output.stat().st_size / 1e6
    print(f"\n{before:.0f} MB -> {after:.0f} MB  ({after / before:.0%})")
    print(f"wrote {output}")
    return 0


def _command_track(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    total = info.frame_count // args.stride if info.frame_count > 0 else None
    with tqdm(total=total, unit="frame", desc="tracking marker") as progress:
        samples = track_dot(
            info, analysis_width=args.analysis_width, stride=args.stride, progress=progress
        )

    if not samples:
        print("error: marker never found", file=sys.stderr)
        return 1

    times = np.array([s.time for s in samples])
    xs = np.array([s.x for s in samples])
    ys = np.array([s.y for s in samples])
    radii = np.array([s.radius for s in samples])

    animated_until = parked_after(times, xs, ys)
    baseline = ground_baseline(times, ys)
    arcs = find_arcs(times, ys, baseline)
    arcs = [a for a in arcs if a.peak_time <= animated_until]

    print()
    print(f"samples              {len(samples)} / {total} ({len(samples) / max(total, 1):.0%})")
    print(f"marker radius        {np.median(radii):.4f} of frame height "
          f"(diameter {2 * np.median(radii):.3f})")
    print(f"animated until       {animated_until:.1f}s of {info.duration:.1f}s")
    print(f"jump arcs            {len(arcs)}")
    for arc in arcs:
        print(f"  peak {arc.peak_time:6.2f}s  height {arc.height:.3f}  span {arc.duration:.2f}s")

    payload = {
        "source": {"file": video.name, "fps": info.fps, "duration": info.duration},
        "animatedUntil": round(animated_until, 3),
        "medianRadius": round(float(np.median(radii)), 5),
        "samples": [
            {"t": round(float(t), 4), "x": round(float(x), 5), "y": round(float(y), 5)}
            for t, x, y in zip(times, xs, ys)
        ],
        "arcs": [
            {
                "start": round(a.start_time, 3),
                "peak": round(a.peak_time, 3),
                "end": round(a.end_time, 3),
                "height": round(a.height, 4),
            }
            for a in arcs
        ],
    }

    output = Path(args.output).expanduser().resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=1))
    print(f"\nwrote {output}")
    return 0


def _command_ledges(args: argparse.Namespace) -> int:
    video = _resolve_video(args.video)
    if video is None:
        return 1

    info = probe(video)
    _describe(info, video)

    detections_path = Path(args.detections).expanduser().resolve()
    if not detections_path.exists():
        print(f"error: no such detections file: {detections_path}", file=sys.stderr)
        return 1
    detection_frames, _, _ = load_detections(detections_path)
    by_time = {round(f.time, 3): f for f in detection_frames}
    print(f"loaded {len(detection_frames)} detection frames")

    extractor = LedgeExtractor()
    frames = []
    with tqdm(total=len(detection_frames), unit="frame", desc="ledges") as progress:
        for frame in iter_frames(info, analysis_width=args.analysis_width, stride=args.stride):
            match = by_time.get(round(frame.time, 3))
            if match is None:
                continue
            frames.append(extractor.analyze(frame, match.detections))
            progress.update(1)

    if not frames:
        print("error: no frames processed", file=sys.stderr)
        return 1

    print()
    print(f"device               {extractor.device}")
    print(f"frames               {len(frames)}")
    for surface in sorted({n for f in frames for n in f.lines}):
        present = sum(1 for f in frames if surface in f.lines)
        print(f"  {surface:8s} present in {present} frames ({present / len(frames):.0%})")

    output = Path(args.output).expanduser().resolve()
    save_ledges(output, frames)
    print(f"\nwrote {output} ({output.stat().st_size / 1e6:.2f} MB)")
    return 0


def _command_occluders(args: argparse.Namespace) -> int:
    """One composite still per occluder track, plus the occlusion verifier.

    Each still draws a stand-in ball at the level's own path position and then
    re-draws the strip exactly as the game does - video pixels through the
    silhouette. If a still looks wrong, so will the game.

    ``--verify`` additionally steps the whole level at video rate through the
    game's mirrored strip logic and fails (exit 2) if any frame erases ball
    pixels with no detected object in front - the "hole bitten out of the
    ball" regression this exists to keep out.
    """
    video = _resolve_video(args.video)
    if video is None:
        return 1

    level = json.loads(Path(args.level).expanduser().resolve().read_text())
    atlas = None
    if level.get("foregroundMaskFile"):
        atlas_path = Path(args.level).expanduser().resolve().parent / level["foregroundMaskFile"]
        atlas = cv2.imread(str(atlas_path), cv2.IMREAD_GRAYSCALE)

    if args.verify:
        from .occlude import verify_occlusion

        detections_path = Path(args.detections).expanduser().resolve()
        if not detections_path.exists():
            print(f"error: --verify needs detections: {detections_path}", file=sys.stderr)
            return 1
        frames, det_w, det_h = load_detections(detections_path)

        from .occlude import OCCLUDER_LABELS

        labels = tuple(level.get("occluderLabels") or OCCLUDER_LABELS)
        violations, stats = verify_occlusion(
            level, atlas, frames, (det_w, det_h), labels=labels
        )
        print(
            f"occlusion verify: {stats['frames_checked']} frames, "
            f"{stats['frames_occluding']} with the ball behind something, "
            f"worst phantom {stats['worst_phantom']:.1%} of ball area"
        )
        for violation in violations[:10]:
            print(f"  {violation}")
        if len(violations) > 10:
            print(f"  ... and {len(violations) - 10} more")
        if violations:
            return 2
        if args.verify_only:
            return 0

    entries = level.get("foreground", [])
    if not entries:
        print("level has no occluders; nothing to render")
        return 0

    map_t = np.array([p["t"] for p in level["timeToDistance"]])
    map_d = np.array([p["d"] for p in level["timeToDistance"]])
    path_d = np.array([p["d"] for p in level["path"]])
    path_y = np.array([p["y"] for p in level["path"]])
    path_x = np.array([p["x"] for p in level["path"]])
    diameter = float(level.get("markerDiameter", 0.173)) or 0.173

    # The sample of each track where the strip is closest to the ball: the
    # moment occlusion is actually visible.
    per_track: dict[int, tuple[float, float, dict]] = {}
    for entry in entries:
        column = float(np.interp(entry["d"], path_d, path_x))
        gap = abs(0.5 * (entry["x1"] + entry["x2"]) - column)
        time = float(np.interp(entry["d"], map_d, map_t))
        if entry["id"] not in per_track or gap < per_track[entry["id"]][0]:
            per_track[entry["id"]] = (gap, time, entry)

    wanted = {round(t, 2): (tid, e) for tid, (_, t, e) in per_track.items()}
    times = np.array(sorted(wanted))
    out_dir = Path(args.output).expanduser().resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    info = probe(video)

    for frame in iter_frames(info, analysis_width=args.analysis_width, stride=2):
        if not times.size:
            break
        near = float(times[int(np.argmin(np.abs(times - frame.time)))])
        if abs(near - frame.time) > 0.04:
            continue
        tid, entry = wanted.pop(near)
        times = np.array(sorted(wanted))

        img = frame.image.copy()
        height, width = img.shape[:2]
        d = float(np.interp(near, map_t, map_d))
        bx = int(float(np.interp(d, path_d, path_x)) * width)
        by = int(float(np.interp(d, path_d, path_y)) * height)
        radius = int(diameter * height / 2)
        cv2.circle(img, (bx, by - radius), radius, (40, 90, 230), -1)

        x1, y1 = int(entry["x1"] * width), int(entry["y1"] * height)
        x2, y2 = int(entry["x2"] * width), int(entry["y2"] * height)
        if atlas is not None and entry.get("mask"):
            ah, aw = atlas.shape
            cell = atlas[
                int(round((1 - entry["v2"]) * ah)) : int(round((1 - entry["v1"]) * ah)),
                int(round(entry["u1"] * aw)) : int(round(entry["u2"] * aw)),
            ]
            mask = cv2.resize(cell, (max(x2 - x1, 1), max(y2 - y1, 1)))
            alpha = np.clip((mask.astype(np.float32) / 255.0 - 0.35) / 0.3, 0, 1)[..., None]
            img[y1:y2, x1:x2] = (
                frame.image[y1:y2, x1:x2] * alpha + img[y1:y2, x1:x2] * (1 - alpha)
            ).astype(np.uint8)
        else:
            img[y1:y2, x1:x2] = frame.image[y1:y2, x1:x2]

        still = out_dir / f"occluder_{tid:02d}_{near:.1f}s.png"
        cv2.imwrite(str(still), img)
        print(f"wrote {still}")

    return 0


def _command_audit(args: argparse.Namespace) -> int:
    auditor = LevelAuditor(
        Path(args.level).expanduser().resolve(),
        Path(args.surfaces).expanduser().resolve(),
        Path(args.ledges).expanduser().resolve(),
        Path(args.detections).expanduser().resolve(),
        video_path=Path(args.video).expanduser().resolve() if args.video else None,
    )
    result = auditor.run(
        Path(args.timeline).expanduser().resolve() if args.timeline else None
    )

    print(f"frames audited       {result.frames}")
    if result.frames:
        print(f"supported            {result.supported_frames / result.frames:.1%}")
    print(f"violations           {len(result.violations)}")
    for violation in result.violations:
        print(f"  {violation}")

    return 0 if result.clean else 2


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

    detect = subparsers.add_parser(
        "detect", help="name obstacles with zero-shot Grounding DINO"
    )
    detect.add_argument("video", help="path to the source video")
    detect.add_argument("-o", "--output", required=True, help="write detections JSON here")
    detect.add_argument("--analysis-width", type=int, default=960)
    detect.add_argument(
        "--stride",
        type=int,
        default=5,
        help="detection only supplies labels; the run line supplies timing, "
        "so it can be sampled far more sparsely than the surface pass",
    )
    detect.set_defaults(func=_command_detect)

    level = subparsers.add_parser("level", help="assemble the level file")
    level.add_argument("video", help="path to the source video")
    level.add_argument("-o", "--output", required=True, help="write level JSON here")
    level.add_argument("--surfaces", required=True, help="cached surfaces .npz")
    level.add_argument("--detections", help="cached detections .json, for obstacle names")
    level.add_argument("--analysis-width", type=int, default=960)
    level.add_argument("--stride", type=int, default=1)
    level.add_argument("--map-samples", type=int, default=2000)
    level.add_argument("--min-stop-duration", type=float, default=0.5)
    level.add_argument("--character-column", type=float, default=0.35)
    level.add_argument("--overlay", help="render a debug overlay video here")
    level.add_argument("--overlay-stride", type=int, default=1)
    level.set_defaults(func=_command_level)

    author = subparsers.add_parser(
        "author", help="build a playable level from a hand-authored timeline"
    )
    author.add_argument("video", help="path to the source video")
    author.add_argument("-o", "--output", required=True, help="write level JSON here")
    author.add_argument("--surfaces", required=True, help="cached surfaces .npz")
    author.add_argument("--timeline", default="timeline.json", help="authored timeline")
    author.add_argument("--analysis-width", type=int, default=960)
    author.add_argument("--stride", type=int, default=1)
    author.add_argument("--min-stop-duration", type=float, default=0.5)
    author.add_argument("--character-column", type=float, default=0.35)
    author.add_argument(
        "--ledges", help="ledges .npz from 'analyze ledges'; snaps the path to real surfaces"
    )
    author.add_argument(
        "--detections",
        help="detections .json; sizes each jump cue's window so any accepted tap clears",
    )
    author.add_argument(
        "--marker", help="tracked marker JSON from 'analyze track'; overrides the timeline"
    )
    author.add_argument(
        "--playback-file",
        default="IMG_3775.play.mp4",
        help="file name the game should play, as it appears in StreamingAssets",
    )
    author.add_argument(
        "--no-occluder-masks",
        action="store_true",
        help="skip the SAM silhouette pass; occluders fall back to rectangles",
    )
    author.set_defaults(func=_command_author)

    transcode_parser = subparsers.add_parser(
        "transcode", help="make a playback-friendly copy (720p H.264, no audio)"
    )
    transcode_parser.add_argument("video", help="path to the source video")
    transcode_parser.add_argument("-o", "--output", required=True, help="write the mp4 here")
    transcode_parser.add_argument("--height", type=int, default=720)
    transcode_parser.add_argument("--crf", type=int, default=23)
    transcode_parser.set_defaults(func=_command_transcode)

    track_parser = subparsers.add_parser(
        "track", help="track a coloured marker drawn over the footage"
    )
    track_parser.add_argument("video", help="the overlay video containing the marker")
    track_parser.add_argument("-o", "--output", required=True, help="write the track JSON here")
    track_parser.add_argument("--analysis-width", type=int, default=640)
    track_parser.add_argument("--stride", type=int, default=1)
    track_parser.set_defaults(func=_command_track)

    ledges_parser = subparsers.add_parser(
        "ledges", help="extract top edges of runnable things (railings, hedges)"
    )
    ledges_parser.add_argument("video", help="path to the source video")
    ledges_parser.add_argument("-o", "--output", required=True, help="write ledges .npz here")
    ledges_parser.add_argument("--detections", required=True, help="cached detections .json")
    ledges_parser.add_argument("--analysis-width", type=int, default=960)
    ledges_parser.add_argument("--stride", type=int, default=5)
    ledges_parser.set_defaults(func=_command_ledges)

    occluders_parser = subparsers.add_parser(
        "occluders",
        help="composite stills of the ball behind each occluder - the eyeball check",
    )
    occluders_parser.add_argument("video", help="path to the source video")
    occluders_parser.add_argument("--level", default="out/IMG_3775.authored.json")
    occluders_parser.add_argument("-o", "--output", default="out/occluders")
    occluders_parser.add_argument("--analysis-width", type=int, default=960)
    occluders_parser.add_argument(
        "--verify",
        action="store_true",
        help="step the level at video rate through the game's mirrored strip "
        "logic; exit 2 if any frame erases ball pixels with nothing there",
    )
    occluders_parser.add_argument(
        "--verify-only",
        action="store_true",
        help="with --verify: skip rendering the stills",
    )
    occluders_parser.add_argument(
        "--detections",
        default="out/IMG_3775.detections.json",
        help="cached detections .json; the ground truth --verify checks against",
    )
    occluders_parser.set_defaults(func=_command_occluders)

    audit_parser = subparsers.add_parser(
        "audit", help="frame-by-frame check of the level against footage evidence"
    )
    audit_parser.add_argument("--level", default="out/IMG_3775.authored.json")
    audit_parser.add_argument("--surfaces", default="out/IMG_3775.surfaces.npz")
    audit_parser.add_argument("--ledges", default="out/IMG_3775.ledges.npz")
    audit_parser.add_argument("--detections", default="out/IMG_3775.detections.json")
    audit_parser.add_argument("--timeline", default="timeline.json")
    audit_parser.add_argument(
        "--video",
        default=None,
        help="source clip; when given, box-test overlap hits are confirmed "
        "against SAM silhouettes from the frames themselves",
    )
    audit_parser.set_defaults(func=_command_audit)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
