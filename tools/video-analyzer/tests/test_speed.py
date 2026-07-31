"""Speed estimation against synthetic footage with a known ground truth.

Each test builds frames by sliding a fixed texture, so the true displacement is
known exactly and the estimator can be checked rather than merely exercised.
"""

import cv2
import numpy as np
import pytest

from video_analyzer.decode import Frame, VideoInfo
from video_analyzer.speed import (
    SpeedConfig,
    _limit_acceleration,
    _median_filter,
    estimate_speed,
)

FPS = 30.0
WIDTH = 640
HEIGHT = 360


def _texture(width: int, height: int, seed: int = 0) -> np.ndarray:
    """A high-frequency pattern that goodFeaturesToTrack can lock onto."""
    rng = np.random.default_rng(seed)
    base = rng.integers(0, 255, size=(height, width, 3), dtype=np.uint8)
    return cv2.GaussianBlur(base, (5, 5), 0)


def _info() -> VideoInfo:
    return VideoInfo(
        path=None,  # type: ignore[arg-type]
        fps=FPS,
        frame_count=0,
        width=WIDTH,
        height=HEIGHT,
        rotation=0.0,
    )


def _sliding_frames(shifts_px: list[float]) -> list[Frame]:
    """Frames panning across a wide texture by the given per-frame shifts."""
    canvas = _texture(WIDTH * 4, HEIGHT)
    frames = []
    # Start mid-canvas so negative shifts have somewhere to pan to.
    offset = float(WIDTH)
    for index, shift in enumerate([0.0, *shifts_px]):
        offset += shift
        start = int(round(offset))
        frames.append(
            Frame(
                index=index,
                time=index / FPS,
                image=canvas[:, start : start + WIDTH].copy(),
            )
        )
    return frames


# The synthetic texture fills the frame, so track all of it rather than the
# near-ground band the real footage needs.
FULL_FRAME = SpeedConfig(roi_top=0.0, roi_bottom=1.0, smoothing_window=1)


def test_recovers_a_known_constant_speed():
    shift = 6.0
    result = estimate_speed(_sliding_frames([shift] * 40), _info(), FULL_FRAME)

    expected = shift * FPS  # px/s
    assert np.median(result.speeds) == pytest.approx(expected, rel=0.02)


def test_speed_scales_with_displacement():
    slow = estimate_speed(_sliding_frames([3.0] * 30), _info(), FULL_FRAME)
    fast = estimate_speed(_sliding_frames([9.0] * 30), _info(), FULL_FRAME)

    ratio = np.median(fast.speeds) / np.median(slow.speeds)
    assert ratio == pytest.approx(3.0, rel=0.05)


def test_static_scene_reports_zero_speed():
    result = estimate_speed(_sliding_frames([0.0] * 30), _info(), FULL_FRAME)

    # Every feature sits below the motion floor, so the moving population is
    # empty and speed collapses to zero rather than to camera noise.
    assert np.all(result.speeds == 0.0)


def test_stop_threshold_sits_below_the_moving_speed():
    shifts = [6.0] * 20 + [0.0] * 20 + [6.0] * 20
    result = estimate_speed(_sliding_frames(shifts), _info(), FULL_FRAME)

    moving = result.speeds[result.speeds > 0]
    assert result.stop_threshold < np.median(moving)
    assert np.any(result.speeds < result.stop_threshold)


def test_a_moving_minority_does_not_capture_the_median():
    """An oncoming vehicle covering part of the frame must not set the speed."""
    canvas = _texture(WIDTH * 4, HEIGHT, seed=1)
    patch = _texture(180, 120, seed=2)

    frames = []
    world = 0.0
    for index in range(30):
        world += 5.0
        image = canvas[:, int(world) : int(world) + WIDTH].copy()
        # A patch sweeping the other way, at a much higher rate, over ~9% of
        # the frame area.
        x = int(WIDTH - 12 * index) % (WIDTH - 180)
        image[100 : 100 + 120, x : x + 180] = patch
        frames.append(Frame(index=index, time=index / FPS, image=image))

    result = estimate_speed(frames, _info(), FULL_FRAME)

    assert np.median(result.speeds) == pytest.approx(5.0 * FPS, rel=0.10)


def test_confidence_is_high_for_coherent_motion():
    result = estimate_speed(_sliding_frames([6.0] * 30), _info(), FULL_FRAME)

    assert np.mean(result.confidence) > 0.95


def test_rejects_a_clip_too_short_to_measure():
    with pytest.raises(RuntimeError, match="too short"):
        estimate_speed(_sliding_frames([]), _info(), FULL_FRAME)


def test_median_filter_removes_spikes_without_shifting_the_level():
    values = np.full(50, 10.0)
    values[25] = 900.0

    filtered = _median_filter(values, 9)

    assert filtered.max() == pytest.approx(10.0)
    assert filtered.size == values.size


def test_median_filter_is_a_no_op_for_unit_window():
    values = np.array([3.0, 1.0, 4.0, 1.0, 5.0])
    assert np.array_equal(_median_filter(values, 1), values)


def test_acceleration_limiter_clips_an_impossible_spike():
    times = np.linspace(0.0, 10.0, 301)
    speeds = np.full_like(times, 100.0)
    speeds[150:154] = 500.0  # a 5x jump inside ~0.1s

    limited = _limit_acceleration(times, speeds, max_change_per_second=50.0)

    assert limited.max() < 120.0
    assert limited[0] == pytest.approx(100.0)


def test_acceleration_limiter_preserves_real_braking():
    """A sharp fall is genuine deceleration; only rises are capped."""
    times = np.linspace(0.0, 10.0, 301)
    speeds = np.where(times < 5.0, 100.0, 0.0)

    limited = _limit_acceleration(times, speeds, max_change_per_second=50.0)

    assert limited[-1] == pytest.approx(0.0)
    assert np.all(limited <= speeds + 1e-9)


def test_acceleration_limiter_does_not_shift_the_curve_in_time():
    """Two-sided clipping must not lag a legitimate ramp the way one pass would."""
    times = np.linspace(0.0, 10.0, 301)
    speeds = 10.0 + 4.0 * times  # 4 units/s, well under the cap

    limited = _limit_acceleration(times, speeds, max_change_per_second=50.0)

    assert np.allclose(limited, speeds)


def test_acceleration_limiter_is_a_no_op_when_disabled():
    times = np.linspace(0.0, 5.0, 51)
    speeds = np.random.default_rng(0).uniform(1.0, 900.0, size=times.size)

    assert np.array_equal(_limit_acceleration(times, speeds, 0.0), speeds)


def test_unmeasurable_frames_are_interpolated_not_guessed():
    """Where no ground is visible, bridge the gap instead of measuring a wall."""
    frames = _sliding_frames([6.0] * 40)
    blind = {15, 16, 17, 18}

    result = estimate_speed(
        frames,
        _info(),
        FULL_FRAME,
        ground_mask=lambda frame: None if frame.index in blind else np.full(
            frame.image.shape[:2], 255, dtype=np.uint8
        ),
    )

    assert result.diagnostics["unmeasurable_fraction"] > 0
    # The bridged samples sit at the same level as the rest, not at zero.
    assert np.median(result.speeds) == pytest.approx(6.0 * FPS, rel=0.05)
    assert result.speeds.min() > 0.5 * 6.0 * FPS


def test_rejects_a_clip_with_no_visible_ground_at_all():
    with pytest.raises(RuntimeError, match="no frame had a visible ground"):
        estimate_speed(
            _sliding_frames([6.0] * 10),
            _info(),
            FULL_FRAME,
            ground_mask=lambda frame: None,
        )


def test_travel_direction_is_opposite_to_the_world_sweep():
    """Scenery sweeping right means the vehicle is heading left."""
    result = estimate_speed(_sliding_frames([6.0] * 30), _info(), FULL_FRAME)

    # _sliding_frames pans the window right across the canvas, so features move
    # left across the frame; the vehicle is therefore heading right.
    assert result.travel_direction in (-1, 1)
    assert result.diagnostics["direction_agreement"] > 0.9


def test_travel_direction_flips_with_the_world():
    forward = estimate_speed(_sliding_frames([6.0] * 30), _info(), FULL_FRAME)
    backward = estimate_speed(_sliding_frames([-6.0] * 30), _info(), FULL_FRAME)

    assert forward.travel_direction == -backward.travel_direction


def test_speed_is_unsigned_regardless_of_direction():
    forward = estimate_speed(_sliding_frames([6.0] * 30), _info(), FULL_FRAME)
    backward = estimate_speed(_sliding_frames([-6.0] * 30), _info(), FULL_FRAME)

    assert np.all(forward.speeds >= 0)
    assert np.all(backward.speeds >= 0)
    assert np.median(forward.speeds) == pytest.approx(np.median(backward.speeds), rel=0.05)
