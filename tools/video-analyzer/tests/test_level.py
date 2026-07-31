from pathlib import Path

import numpy as np
import pytest

from video_analyzer.decode import VideoInfo
from video_analyzer.distance import build_distance_map
from video_analyzer.level import (
    LEVEL_VERSION,
    bridge_short_runs,
    build_level,
    build_surface_segments,
    write_level,
)
from video_analyzer.obstacles import ObstacleConfig, extract_obstacles
from video_analyzer.segment import NO_GROUND, SurfaceFrame

WIDTH = 200
HEIGHT = 100
CONFIG = ObstacleConfig()


def _surface(index, run_line, coverage=0.2):
    return SurfaceFrame(
        index=index,
        time=index * 0.1,
        run_line=np.asarray(run_line, dtype=np.int32),
        coverage=coverage,
        width=WIDTH,
        height=HEIGHT,
    )


def _flat(index, y=60, coverage=0.2):
    return _surface(index, np.full(WIDTH, y), coverage)


def _distance_map(count):
    return build_distance_map(np.arange(count) * 0.1, np.full(count, 10.0))


def _info():
    return VideoInfo(
        path=Path("clip.mov"), fps=30.0, frame_count=600, width=1920, height=1080, rotation=0.0
    )


# --- bridging ---------------------------------------------------------------


def test_bridge_fills_a_short_run():
    flags = np.array([True, True, False, True, True])

    assert np.array_equal(bridge_short_runs(flags, 3), np.ones(5, dtype=bool))


def test_bridge_leaves_a_long_run_alone():
    flags = np.array([True, False, False, False, False, True])

    assert np.array_equal(bridge_short_runs(flags, 3), flags)


def test_bridge_handles_runs_at_the_edges():
    flags = np.array([False, True, True, True, False])

    assert np.array_equal(bridge_short_runs(flags, 3), np.ones(5, dtype=bool))


def test_bridge_is_a_no_op_for_min_run_of_one():
    flags = np.array([True, False, True])
    assert np.array_equal(bridge_short_runs(flags, 1), flags)


# --- segments ---------------------------------------------------------------


def test_continuous_ground_is_one_segment_with_no_gaps():
    surfaces = [_flat(i) for i in range(40)]

    segments, gaps = build_surface_segments(surfaces, _distance_map(40), CONFIG)

    assert len(segments) == 1
    assert gaps == []
    assert segments[0].path[0][1] == pytest.approx(0.6)


def test_a_sustained_wall_becomes_a_gap():
    surfaces = [_flat(i) for i in range(40)]
    for i in range(15, 25):
        surfaces[i] = _flat(i, coverage=0.95)  # wall: implausible as ground

    segments, gaps = build_surface_segments(surfaces, _distance_map(40), CONFIG)

    assert len(segments) == 2
    assert len(gaps) == 1


def test_a_momentary_dropout_is_bridged_not_emitted():
    surfaces = [_flat(i) for i in range(40)]
    surfaces[20] = _surface(20, np.full(WIDTH, NO_GROUND))

    segments, gaps = build_surface_segments(surfaces, _distance_map(40), CONFIG)

    assert len(segments) == 1
    assert gaps == []


def test_a_bridged_dropout_gets_an_interpolated_path_point():
    surfaces = [_flat(i, y=60) for i in range(40)]
    surfaces[20] = _surface(20, np.full(WIDTH, NO_GROUND))

    segments, _ = build_surface_segments(surfaces, _distance_map(40), CONFIG)

    ys = [y for _, y in segments[0].path]
    assert all(np.isfinite(ys))
    assert ys[20] == pytest.approx(0.6, abs=0.01)


def test_paths_are_normalized_so_resolution_can_change():
    """The same scene at double resolution must produce the same path."""
    small = [_flat(i, y=60) for i in range(30)]
    large = [
        SurfaceFrame(i, i * 0.1, np.full(WIDTH * 2, 120, dtype=np.int32), 0.2, WIDTH * 2, 200)
        for i in range(30)
    ]

    a, _ = build_surface_segments(small, _distance_map(30), CONFIG)
    b, _ = build_surface_segments(large, _distance_map(30), CONFIG)

    assert a[0].path[0][1] == pytest.approx(b[0].path[0][1])


# --- level file -------------------------------------------------------------


def _level(surfaces, obstacles=None):
    distance_map = _distance_map(len(surfaces))
    return build_level(
        Path("clip.mov"),
        _info(),
        distance_map,
        surfaces,
        obstacles if obstacles is not None else [],
        [],
        CONFIG,
    )


def test_level_has_the_expected_shape():
    level = _level([_flat(i) for i in range(40)])

    assert level["version"] == LEVEL_VERSION
    assert level["distanceUnits"] == "relative"
    assert level["source"]["file"] == "clip.mov"
    assert level["characterColumn"] == CONFIG.character_column
    for key in ("timeToDistance", "surfaceSegments", "surfaceGaps", "obstacles"):
        assert key in level


def test_level_never_contains_tap_timestamps():
    """Tap windows are Unity's to derive from live jump physics."""
    surfaces = [_flat(i) for i in range(40)]
    for i in range(18, 22):
        line = np.full(WIDTH, 60)
        centre = int(WIDTH * CONFIG.character_column)
        line[centre - 6 : centre + 7] = 80
        surfaces[i] = _surface(i, line)

    level = _level(surfaces, extract_obstacles(surfaces, _distance_map(40), None, CONFIG))

    serialized = str(level)
    assert "tap" not in serialized.lower()
    assert level["obstacles"]
    for obstacle in level["obstacles"]:
        assert "distance" in obstacle
        assert "time" not in obstacle


def test_time_to_distance_is_monotonic_and_invertible():
    level = _level([_flat(i) for i in range(60)])

    pairs = np.array(level["timeToDistance"])
    assert np.all(np.diff(pairs[:, 0]) > 0)
    assert np.all(np.diff(pairs[:, 1]) > 0)


def test_paths_are_thinned_but_keep_their_endpoints():
    surfaces = [_flat(i) for i in range(600)]

    level = build_level(
        Path("clip.mov"),
        _info(),
        _distance_map(600),
        surfaces,
        [],
        [],
        CONFIG,
        path_samples=50,
    )

    path = level["surfaceSegments"][0]["path"]
    assert len(path) <= 60
    assert path[0][0] == pytest.approx(level["surfaceSegments"][0]["startDistance"], abs=0.01)
    assert path[-1][0] == pytest.approx(level["surfaceSegments"][0]["endDistance"], abs=0.01)


def test_level_round_trips_through_json(tmp_path):
    import json

    level = _level([_flat(i) for i in range(40)])
    path = tmp_path / "level.json"

    write_level(path, level)

    assert json.loads(path.read_text()) == level


def test_emitted_path_is_denoised():
    """A ragged mask edge must not become a jittering character."""
    surfaces = []
    for i in range(40):
        line = np.full(WIDTH, 60)
        line[::5] = 85  # isolated spikes, as on textured asphalt
        surfaces.append(_surface(i, line))

    segments, _ = build_surface_segments(surfaces, _distance_map(40), CONFIG)

    ys = np.array([y for _, y in segments[0].path])
    assert np.allclose(ys, 0.6, atol=0.02)


def test_path_does_not_teleport_across_an_obstacle():
    """While an obstacle blocks the column the run line dives to the frame
    bottom; the emitted path must describe the ground, not that dive."""
    surfaces = [_flat(i, y=60) for i in range(60)]
    for i in range(28, 33):
        line = np.full(WIDTH, 60)
        centre = int(WIDTH * CONFIG.character_column)
        line[centre - 12 : centre + 13] = NO_GROUND  # fully blocked
        surfaces[i] = _surface(i, line)

    segments, _ = build_surface_segments(surfaces, _distance_map(60), CONFIG)

    ys = np.array([y for s in segments for _, y in s.path])
    assert np.max(np.abs(np.diff(ys))) < 0.05
    assert np.allclose(ys, 0.6, atol=0.02)


def test_obstacle_is_still_reported_even_though_the_path_smooths_over_it():
    surfaces = [_flat(i, y=60) for i in range(60)]
    for i in range(28, 33):
        line = np.full(WIDTH, 60)
        centre = int(WIDTH * CONFIG.character_column)
        line[centre - 12 : centre + 13] = NO_GROUND
        surfaces[i] = _surface(i, line)

    events = extract_obstacles(surfaces, _distance_map(60), None, CONFIG)

    assert len(events) == 1
