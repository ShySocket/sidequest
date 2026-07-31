import numpy as np
import pytest

from video_analyzer.detect import Detection, DetectionFrame, normalize_label
from video_analyzer.distance import build_distance_map
from video_analyzer.obstacles import (
    ObstacleConfig,
    blocked_fraction,
    column_profile,
    extract_obstacles,
    free_ground_baseline,
)
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


def _with_obstacle_at_character(index, y=60, depth=15):
    """Obstacle at the character column pushes the run line DOWN (larger y)."""
    line = np.full(WIDTH, y)
    centre = int(WIDTH * CONFIG.character_column)
    half = max(1, int(WIDTH * CONFIG.column_halfwidth))
    line[centre - half : centre + half + 1] = y + depth
    return _surface(index, line)


def _distance_map(count):
    times = np.arange(count) * 0.1
    return build_distance_map(times, np.full(count, 10.0))


# --- baseline ---------------------------------------------------------------


def test_baseline_tracks_free_ground():
    surfaces = [_flat(i, y=60) for i in range(80)]

    baseline = free_ground_baseline(surfaces, CONFIG.baseline_window)

    assert np.allclose(baseline, 0.6)


def test_baseline_ignores_obstacles_occupying_a_minority_of_columns():
    surfaces = [_with_obstacle_at_character(i, y=60, depth=25) for i in range(80)]

    baseline = free_ground_baseline(surfaces, CONFIG.baseline_window)

    # The obstacle covers ~6% of columns, so the median still finds free ground.
    assert np.allclose(baseline, 0.6)


def test_baseline_interpolates_frames_with_no_ground():
    surfaces = [_flat(i, y=60) for i in range(40)]
    surfaces[20] = _surface(20, np.full(WIDTH, NO_GROUND))

    baseline = free_ground_baseline(surfaces, window=1)

    assert baseline[20] == pytest.approx(0.6)


# --- blockage ---------------------------------------------------------------


def test_clear_ground_is_not_blocked():
    fraction, depth = blocked_fraction(_flat(0, y=60), baseline=0.6, config=CONFIG)

    assert fraction == 0.0
    assert depth == pytest.approx(0.0)


def test_obstacle_at_character_column_is_blocked():
    surface = _with_obstacle_at_character(0, y=60, depth=15)

    fraction, depth = blocked_fraction(surface, baseline=0.6, config=CONFIG)

    assert fraction > CONFIG.blocked_column_fraction
    assert depth == pytest.approx(0.15, abs=0.01)


def test_missing_ground_counts_as_fully_blocked():
    line = np.full(WIDTH, 60)
    centre = int(WIDTH * CONFIG.character_column)
    line[centre - 6 : centre + 7] = NO_GROUND

    fraction, _ = blocked_fraction(_surface(0, line), baseline=0.6, config=CONFIG)

    assert fraction > CONFIG.blocked_column_fraction


def test_obstacle_away_from_the_character_column_is_ignored():
    """Something at the far edge of frame is not in the character's path."""
    line = np.full(WIDTH, 60)
    line[WIDTH - 20 :] = 85

    fraction, _ = blocked_fraction(_surface(0, line), baseline=0.6, config=CONFIG)

    assert fraction == 0.0


def test_shallow_curb_wobble_does_not_register():
    line = np.full(WIDTH, 60)
    centre = int(WIDTH * CONFIG.character_column)
    line[centre - 6 : centre + 7] = 62  # 2% of height, under notch_depth

    fraction, _ = blocked_fraction(_surface(0, line), baseline=0.6, config=CONFIG)

    assert fraction == 0.0


# --- events -----------------------------------------------------------------


def test_extracts_a_single_obstacle_event():
    surfaces = [_flat(i) for i in range(60)]
    for i in range(28, 33):
        surfaces[i] = _with_obstacle_at_character(i)

    events = extract_obstacles(surfaces, _distance_map(60), None, CONFIG)

    assert len(events) == 1
    assert events[0].label == "unknown"
    assert events[0].blockage > 0.5


def test_clear_footage_yields_no_events():
    surfaces = [_flat(i) for i in range(60)]

    assert extract_obstacles(surfaces, _distance_map(60), None, CONFIG) == []


def test_blinking_detection_merges_into_one_obstacle():
    """A single object often drops out for a frame or two mid-pass."""
    surfaces = [_flat(i) for i in range(60)]
    for i in [28, 29, 32, 33]:  # gap at 30-31
        surfaces[i] = _with_obstacle_at_character(i)

    events = extract_obstacles(surfaces, _distance_map(60), None, CONFIG)

    assert len(events) == 1


def test_well_separated_obstacles_stay_separate():
    surfaces = [_flat(i) for i in range(200)]
    for i in list(range(20, 24)) + list(range(160, 164)):
        surfaces[i] = _with_obstacle_at_character(i)

    events = extract_obstacles(surfaces, _distance_map(200), None, CONFIG)

    assert len(events) == 2
    assert events[0].distance < events[1].distance
    assert [e.id for e in events] == [0, 1]


def test_events_are_keyed_on_distance_not_time():
    """Doubling speed halves the timestamps but leaves distances unchanged."""
    surfaces = [_flat(i) for i in range(60)]
    for i in range(28, 33):
        surfaces[i] = _with_obstacle_at_character(i)

    times = np.arange(60) * 0.1
    slow = build_distance_map(times, np.full(60, 10.0))
    fast = build_distance_map(times * 0.5, np.full(60, 20.0))

    surfaces_fast = [
        SurfaceFrame(s.index, s.time * 0.5, s.run_line, s.coverage, s.width, s.height)
        for s in surfaces
    ]

    a = extract_obstacles(surfaces, slow, None, CONFIG)
    b = extract_obstacles(surfaces_fast, fast, None, CONFIG)

    assert a[0].distance == pytest.approx(b[0].distance, rel=1e-6)


def test_obstacle_is_named_from_an_overlapping_detection():
    surfaces = [_flat(i) for i in range(60)]
    for i in range(28, 33):
        surfaces[i] = _with_obstacle_at_character(i)

    column = WIDTH * CONFIG.character_column
    detections = [
        DetectionFrame(
            index=30,
            time=3.0,
            detections=[
                Detection("traffic sign", 0.81, (column - 10, 20.0, column + 10, 70.0))
            ],
        )
    ]

    events = extract_obstacles(surfaces, _distance_map(60), detections, CONFIG)

    assert events[0].label == "traffic sign"
    assert events[0].confidence == pytest.approx(0.81)
    assert events[0].height > 0


def test_surface_classes_never_name_an_obstacle():
    """A sidewalk spanning the column is what we run on, not a thing to dodge."""
    surfaces = [_flat(i) for i in range(60)]
    for i in range(28, 33):
        surfaces[i] = _with_obstacle_at_character(i)

    column = WIDTH * CONFIG.character_column
    detections = [
        DetectionFrame(
            index=30,
            time=3.0,
            detections=[Detection("sidewalk", 0.95, (0.0, 50.0, float(WIDTH), 100.0))],
        )
    ]

    events = extract_obstacles(surfaces, _distance_map(60), detections, CONFIG)

    assert events[0].label == "unknown"


def test_detection_not_spanning_the_column_is_not_used():
    surfaces = [_flat(i) for i in range(60)]
    for i in range(28, 33):
        surfaces[i] = _with_obstacle_at_character(i)

    detections = [
        DetectionFrame(
            index=30, time=3.0, detections=[Detection("car", 0.9, (0.0, 20.0, 5.0, 70.0))]
        )
    ]

    events = extract_obstacles(surfaces, _distance_map(60), detections, CONFIG)

    assert events[0].label == "unknown"


def test_no_surfaces_yields_no_events():
    assert extract_obstacles([], _distance_map(10), None, CONFIG) == []


# --- label normalization ----------------------------------------------------


@pytest.mark.parametrize(
    "phrase,expected",
    [
        ("car", "car"),
        ("railing fence", "railing"),
        ("sidewalk railing", "railing"),
        ("traffic sign", "traffic sign"),
        ("TRAFFIC SIGN", "traffic sign"),
        ("", "unknown"),
        ("something else", "something else"),
    ],
)
def test_normalize_label(phrase, expected):
    assert normalize_label(phrase) == expected


def test_merge_gap_is_counted_in_frames_not_distance():
    """The same blink must merge identically however long the clip is."""
    def build(total):
        surfaces = [_flat(i) for i in range(total)]
        for i in [28, 29, 32, 33]:
            surfaces[i] = _with_obstacle_at_character(i)
        return extract_obstacles(surfaces, _distance_map(total), None, CONFIG)

    assert len(build(60)) == len(build(600)) == 1


def test_a_dropout_longer_than_the_merge_gap_stays_separate():
    surfaces = [_flat(i) for i in range(60)]
    for i in [28, 29, 40, 41]:
        surfaces[i] = _with_obstacle_at_character(i)

    assert len(extract_obstacles(surfaces, _distance_map(60), None, CONFIG)) == 2


def test_single_column_noise_does_not_create_an_obstacle():
    """Ragged mask edges on textured asphalt were manufacturing obstacles.

    Real noise is sparse — measured at ~0.4% of columns on this footage — so
    isolated spikes must vanish under the median while a solid run does not.
    """
    line = np.full(WIDTH, 60)
    line[::5] = 90  # isolated spikes, none adjacent

    fraction, _ = blocked_fraction(_surface(0, line), baseline=0.6, config=CONFIG)

    assert fraction == 0.0


def test_a_wide_obstacle_survives_column_smoothing():
    surface = _with_obstacle_at_character(0, y=60, depth=20)

    fraction, _ = blocked_fraction(surface, baseline=0.6, config=CONFIG)

    assert fraction > CONFIG.blocked_column_fraction


def test_a_frame_that_barely_sees_ground_reports_no_obstacle():
    """'Ground not visible' must not be reported as 'way is blocked'."""
    line = np.full(WIDTH, NO_GROUND)
    line[: int(WIDTH * 0.4)] = 60  # only 40% of columns have ground
    surfaces = [_flat(i) for i in range(40)]
    for i in range(18, 24):
        surfaces[i] = _surface(i, line)

    events = extract_obstacles(surfaces, _distance_map(40), None, CONFIG)

    assert events == []


def test_an_implausible_wall_frame_reports_no_obstacle():
    surfaces = [_flat(i) for i in range(40)]
    for i in range(18, 24):
        surfaces[i] = _flat(i, y=5, coverage=0.95)

    assert extract_obstacles(surfaces, _distance_map(40), None, CONFIG) == []


def test_a_wall_frame_above_free_ground_is_not_trusted():
    """At t~70.1s a wall slipped the coverage band and put the run line at the
    top of the frame, which would have teleported the character."""
    surfaces = [_flat(i, y=60) for i in range(40)]
    surfaces[20] = _surface(20, np.full(WIDTH, 1), coverage=0.588)  # run line at top

    profile = column_profile(surfaces, CONFIG)

    assert not profile.trustworthy[20]
    assert profile.trustworthy[19] and profile.trustworthy[21]


def test_free_ground_excludes_both_obstacles_and_junk():
    surfaces = [_flat(i, y=60) for i in range(40)]
    surfaces[10] = _with_obstacle_at_character(10)
    surfaces[20] = _surface(20, np.full(WIDTH, 1), coverage=0.588)

    profile = column_profile(surfaces, CONFIG)

    assert not profile.free_ground[10]
    assert not profile.free_ground[20]
    assert profile.free_ground[30]


def test_baseline_still_smooths_on_a_clip_shorter_than_its_window():
    """Without this the baseline follows a junk frame on short clips."""
    surfaces = [_flat(i, y=60) for i in range(11)]
    surfaces[5] = _surface(5, np.full(WIDTH, 1))

    baseline = free_ground_baseline(surfaces, window=61)

    assert baseline[5] == pytest.approx(0.6, abs=0.01)
