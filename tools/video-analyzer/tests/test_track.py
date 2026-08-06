"""Marker tracking maths, against synthetic paths with a known answer."""

import numpy as np
import pytest

from video_analyzer.track import find_arcs, ground_baseline, parked_after, smooth


def _times(count=600, rate=30.0):
    return np.arange(count) / rate


def _with_jumps(times, ground=0.8, jumps=((5.0, 0.3, 0.8),)):
    """Ground line with parabolic dips upward (screen y decreases upward)."""
    y = np.full_like(times, ground)
    for centre, height, width in jumps:
        near = np.abs(times - centre) <= width * 0.5
        phase = (times[near] - centre) / (width * 0.5)
        y[near] -= height * (1.0 - phase * phase)
    return y


# --- baseline ---------------------------------------------------------------


def test_baseline_recovers_the_ground_under_a_jump():
    times = _times()
    y = _with_jumps(times, ground=0.8, jumps=((5.0, 0.3, 0.8),))

    baseline = ground_baseline(times, y)

    assert np.allclose(baseline, 0.8, atol=0.02)


def test_baseline_follows_a_ground_line_that_drifts():
    times = _times()
    y = 0.5 + 0.2 * times / times[-1]

    baseline = ground_baseline(times, y)

    assert baseline[0] == pytest.approx(y[0], abs=0.05)
    assert baseline[-1] == pytest.approx(y[-1], abs=0.05)


# --- arcs -------------------------------------------------------------------


def test_finds_a_single_jump():
    times = _times()
    y = _with_jumps(times, jumps=((5.0, 0.3, 1.0),))

    arcs = find_arcs(times, y, ground_baseline(times, y))

    assert len(arcs) == 1
    assert arcs[0].peak_time == pytest.approx(5.0, abs=0.1)
    assert arcs[0].height == pytest.approx(0.3, abs=0.05)


def test_finds_several_separated_jumps():
    times = _times(1200)
    y = _with_jumps(times, jumps=((5.0, 0.3, 1.0), (15.0, 0.5, 1.0), (30.0, 0.2, 1.0)))

    arcs = find_arcs(times, y, ground_baseline(times, y))

    assert [round(a.peak_time) for a in arcs] == [5, 15, 30]
    assert arcs[1].height > arcs[0].height > arcs[2].height


def test_flat_ground_yields_no_jumps():
    times = _times()
    assert find_arcs(times, np.full_like(times, 0.8), np.full_like(times, 0.8)) == []


def test_a_shallow_wobble_is_not_a_jump():
    times = _times()
    y = _with_jumps(times, jumps=((5.0, 0.02, 1.0),))

    assert find_arcs(times, y, ground_baseline(times, y)) == []


def test_a_mid_arc_dropout_does_not_split_one_jump():
    """Keyframed motion often dips briefly mid-arc."""
    times = _times()
    y = _with_jumps(times, jumps=((5.0, 0.3, 1.2),))
    dip = np.abs(times - 5.0) < 0.05
    y[dip] = 0.8  # momentarily back on the ground

    arcs = find_arcs(times, y, ground_baseline(times, y))

    assert len(arcs) == 1


# --- smoothing --------------------------------------------------------------


def test_smoothing_removes_the_corners_an_editor_leaves():
    times = _times()
    y = np.abs(((times % 4.0) - 2.0)) / 2.0  # sharp triangle wave

    smoothed = smooth(times, y, window=0.6)

    # Second difference measures corners; smoothing should cut it sharply.
    assert np.max(np.abs(np.diff(smoothed, 2))) < np.max(np.abs(np.diff(y, 2))) * 0.5


def test_smoothing_preserves_the_overall_level():
    times = _times()
    y = np.full_like(times, 0.7)

    assert np.allclose(smooth(times, y, window=0.6), 0.7, atol=1e-6)


def test_smoothing_is_a_no_op_for_a_zero_window():
    times = _times(10)
    y = np.linspace(0, 1, 10)
    assert np.array_equal(smooth(times, y, window=0.0), y)


# --- parked detection -------------------------------------------------------


def test_detects_when_the_marker_stopped_being_animated():
    times = _times(900)
    x = np.where(times < 20.0, 0.9 - 0.03 * times, 0.9 - 0.03 * 20.0)
    y = np.where(times < 20.0, 0.8 - 0.005 * times, 0.8 - 0.005 * 20.0)

    # Detected slightly early by construction: the last sample differing from
    # the final value by more than the tolerance is one tolerance/speed before
    # the marker actually stopped.
    assert parked_after(times, x, y) == pytest.approx(20.0, abs=0.5)


def test_a_marker_animated_to_the_end_reports_the_end():
    times = _times(600)
    x = 0.9 - 0.03 * times
    y = np.full_like(times, 0.8)

    assert parked_after(times, x, y) == pytest.approx(times[-1], abs=0.05)
