import numpy as np
import pytest

from video_analyzer.segment import (
    NO_GROUND,
    SurfaceFrame,
    extract_run_line,
    load_surfaces,
    save_surfaces,
)


def _surface(run_line, height=100, width=None, index=0, time=0.0, coverage=0.3):
    run_line = np.asarray(run_line, dtype=np.int32)
    return SurfaceFrame(
        index=index,
        time=time,
        run_line=run_line,
        coverage=coverage,
        width=width or run_line.size,
        height=height,
    )


def test_extract_run_line_finds_the_upper_boundary():
    mask = np.zeros((100, 5), dtype=bool)
    mask[60:, :] = True

    assert np.array_equal(extract_run_line(mask), np.full(5, 60))


def test_extract_run_line_follows_a_sloping_surface():
    mask = np.zeros((100, 4), dtype=bool)
    for column, top in enumerate([70, 65, 60, 55]):
        mask[top:, column] = True

    assert np.array_equal(extract_run_line(mask), np.array([70, 65, 60, 55]))


def test_extract_run_line_marks_columns_without_ground():
    """An object standing on the ground blanks its columns entirely."""
    mask = np.zeros((100, 4), dtype=bool)
    mask[60:, 0] = True
    mask[60:, 3] = True

    result = extract_run_line(mask)

    assert result[0] == 60
    assert result[1] == NO_GROUND
    assert result[2] == NO_GROUND
    assert result[3] == 60


def test_extract_run_line_notches_around_an_obstacle():
    """A parked car interrupts the surface, pushing the boundary upward."""
    mask = np.zeros((100, 6), dtype=bool)
    mask[70:, :] = True
    mask[70:90, 2:4] = False  # obstacle footprint

    result = extract_run_line(mask)

    assert result[0] == 70
    assert result[2] == 90  # notch
    assert result[5] == 70


def test_normalized_run_line_is_resolution_independent():
    surface = _surface([50, 60, 70], height=100)

    normalized = surface.normalized_run_line()

    assert normalized == pytest.approx([0.5, 0.6, 0.7])


def test_normalized_run_line_uses_nan_for_missing_ground():
    surface = _surface([50, NO_GROUND, 70], height=100)

    normalized = surface.normalized_run_line()

    assert np.isnan(normalized[1])
    assert normalized[0] == pytest.approx(0.5)


def test_valid_fraction_counts_columns_with_ground():
    assert _surface([10, NO_GROUND, 30, NO_GROUND]).valid_fraction == pytest.approx(0.5)


def test_ground_mask_round_trips_through_the_run_line():
    mask = np.zeros((40, 8), dtype=bool)
    mask[25:, :] = True
    mask[25:, 3] = False

    surface = _surface(extract_run_line(mask), height=40)
    reconstructed = surface.ground_mask() > 0

    assert np.array_equal(reconstructed, mask)


def test_save_and_load_round_trip(tmp_path):
    surfaces = [
        _surface([10, 20, NO_GROUND], index=0, time=0.0, coverage=0.25),
        _surface([12, 22, 32], index=3, time=0.1, coverage=0.31),
    ]
    path = tmp_path / "surfaces.npz"

    save_surfaces(path, surfaces)
    loaded = load_surfaces(path)

    assert [s.index for s in loaded] == [0, 3]
    assert loaded[0].time == pytest.approx(0.0)
    assert loaded[1].coverage == pytest.approx(0.31, abs=1e-6)
    assert np.array_equal(loaded[0].run_line, surfaces[0].run_line)
    assert loaded[0].height == 100


def test_plausible_ground_accepts_a_normal_surface():
    assert _surface([50, 60], coverage=0.195).is_plausible_ground


def test_implausible_when_a_wall_fills_the_frame():
    """The t~61s failure on IMG_3775: 94.5% coverage is not a ground plane."""
    surface = _surface([5, 5, 5], coverage=0.945)

    assert surface.valid_fraction == 1.0  # every column has 'ground'...
    assert not surface.is_plausible_ground  # ...but coverage gives it away


def test_implausible_when_almost_nothing_is_found():
    assert not _surface([90, 91], coverage=0.005).is_plausible_ground
