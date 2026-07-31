"""Speed estimation against synthetic footage with a known ground truth.

Each test builds frames by sliding a fixed texture, so the true displacement is
known exactly and the estimator can be checked rather than merely exercised.
"""

import cv2
import numpy as np
import pytest

from video_analyzer.decode import Frame, VideoInfo
from video_analyzer.speed import SpeedConfig, _median_filter, estimate_speed

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
    offset = 0.0
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
