"""Ambient normalization maths, on synthetic colours with a known answer."""

import numpy as np
import pytest

from video_analyzer.ambient import fill_gaps, normalize_ambient


def test_median_luminance_maps_to_one():
    colors = np.full((100, 3), 0.4)
    colors[:10] = 0.1   # a stretch of shade
    colors[-10:] = 0.9  # a stretch of glare

    normalized = normalize_ambient(colors)
    luma = normalized @ np.array([0.299, 0.587, 0.114])

    assert np.median(luma) == pytest.approx(1.0, abs=1e-6)


def test_shade_and_glare_stay_bounded():
    colors = np.full((50, 3), 0.5)
    colors[0] = 0.001
    colors[1] = 50.0

    normalized = normalize_ambient(colors)

    assert normalized.min() >= 0.35
    assert normalized.max() <= 1.8


def test_a_warm_cast_survives_normalization():
    """Normalization scales luminance, not hue - the sunset stays warm."""
    colors = np.tile(np.array([0.6, 0.5, 0.35]), (40, 1))

    normalized = normalize_ambient(colors)

    assert normalized[0, 0] > normalized[0, 2]


def test_gaps_are_interpolated_between_neighbours():
    times = np.arange(5, dtype=float)
    colors = np.array(
        [[0.2] * 3, [np.nan] * 3, [np.nan] * 3, [np.nan] * 3, [1.0] * 3]
    )

    filled = fill_gaps(times, colors)

    assert filled[2, 0] == pytest.approx(0.6)
    assert np.all(np.isfinite(filled))


def test_all_missing_defaults_to_neutral():
    filled = fill_gaps(np.arange(3, dtype=float), np.full((3, 3), np.nan))
    assert np.allclose(filled, 1.0)
