import numpy as np
import pytest

from video_analyzer.distance import (
    DistanceMap,
    build_distance_map,
    find_stopped_spans,
)


def test_constant_speed_integrates_linearly():
    times = np.linspace(0.0, 10.0, 101)
    speeds = np.full_like(times, 4.0)

    result = build_distance_map(times, speeds)

    assert result.total_distance == pytest.approx(40.0)
    assert result.distance_at(2.5) == pytest.approx(10.0)


def test_round_trip_time_to_distance_and_back():
    # A varying but always-positive speed, so the map stays invertible.
    times = np.linspace(0.0, 20.0, 401)
    speeds = 5.0 + 4.0 * np.sin(times / 3.0)

    result = build_distance_map(times, speeds)

    probes = np.linspace(0.0, 20.0, 97)
    recovered = result.time_at(result.distance_at(probes))
    # Sub-frame accuracy at 30fps is ~0.033s; require an order better.
    assert np.max(np.abs(recovered - probes)) < 0.003


def test_stopped_span_stays_invertible():
    # 3s moving, 4s stopped, 3s moving.
    times = np.linspace(0.0, 10.0, 1001)
    speeds = np.where((times >= 3.0) & (times <= 7.0), 0.0, 6.0)

    result = build_distance_map(times, speeds)

    # Strict monotonicity is what makes time_at() single-valued; the epsilon
    # floor is what buys it across the stop.
    assert np.all(np.diff(result.distances) > 0)
    assert result.time_at(result.distance_at(8.0)) == pytest.approx(8.0, abs=0.01)


def test_distance_map_rejects_non_monotonic_distance():
    with pytest.raises(ValueError, match="distances must be strictly increasing"):
        DistanceMap(times=np.array([0.0, 1.0, 2.0]), distances=np.array([0.0, 5.0, 5.0]))


def test_distance_map_rejects_mismatched_lengths():
    with pytest.raises(ValueError, match="same length"):
        DistanceMap(times=np.array([0.0, 1.0, 2.0]), distances=np.array([0.0, 1.0]))


def test_queries_clamp_outside_the_clip():
    times = np.linspace(0.0, 5.0, 51)
    result = build_distance_map(times, np.full_like(times, 2.0))

    assert result.distance_at(-3.0) == pytest.approx(0.0)
    assert result.distance_at(99.0) == pytest.approx(result.total_distance)
    assert result.time_at(-1.0) == pytest.approx(0.0)
    assert result.time_at(1e6) == pytest.approx(5.0)


def test_resample_preserves_endpoints_and_shape():
    times = np.linspace(0.0, 12.0, 601)
    speeds = 3.0 + np.cos(times)
    result = build_distance_map(times, speeds)

    sampled_times, sampled_distances = result.resample(200)

    assert sampled_times[0] == pytest.approx(0.0)
    assert sampled_times[-1] == pytest.approx(12.0)
    assert sampled_distances[-1] == pytest.approx(result.total_distance)
    assert np.all(np.diff(sampled_distances) > 0)


def test_find_stopped_spans_detects_the_stop():
    times = np.linspace(0.0, 10.0, 1001)
    speeds = np.where((times >= 4.0) & (times <= 6.0), 0.0, 5.0)

    spans = find_stopped_spans(times, speeds, speed_threshold=0.5)

    assert len(spans) == 1
    assert spans[0].start_time == pytest.approx(4.0, abs=0.02)
    assert spans[0].end_time == pytest.approx(6.0, abs=0.02)


def test_find_stopped_spans_ignores_brief_dropouts():
    # A single-frame flow dropout, as happens when a passing van fills the view.
    times = np.linspace(0.0, 10.0, 1001)
    speeds = np.full_like(times, 5.0)
    speeds[500] = 0.0

    assert find_stopped_spans(times, speeds, speed_threshold=0.5, min_duration=0.5) == []


def test_build_distance_map_rejects_unsorted_times():
    with pytest.raises(ValueError, match="times must be strictly increasing"):
        build_distance_map(np.array([0.0, 2.0, 1.0]), np.array([1.0, 1.0, 1.0]))
