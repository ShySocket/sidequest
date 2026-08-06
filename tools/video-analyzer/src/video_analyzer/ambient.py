"""Ambient light sampled from the footage along the character's path.

A ball lit by one constant sun looks pasted on the moment the footage
disagrees - the clip drives through tree shade, past sunlit walls and into a
warm low-sun parking lot, and a character whose shading never responds to any
of it reads as a sticker. The honest source for that lighting is the footage
itself: sample the pixels around where the ball will be, per frame, and let the
game tint its material from the result.

Colours are normalized so the clip's median luminance maps to 1.0. The game
multiplies its tuned material colours by the sample, so "ordinary daylight in
this clip" leaves the material exactly as tuned and shade or glare move it
relative to that.
"""

from __future__ import annotations

import numpy as np

from .decode import VideoInfo, iter_frames

PATCH_HALF = 6
"""Half-size of the sampled square, in pixels at the analysis width."""

BALL_LIFT = 0.05
"""Sample this far above the ground line, in frame heights - the ball's body,
not the pavement it hides."""

CLAMP = (0.35, 1.8)
"""Bounds on the normalized colour, so a black underpass or blown-out sky
cannot drive the material to nothing or to bloom."""


def normalize_ambient(colors: np.ndarray, clamp: tuple[float, float] = CLAMP) -> np.ndarray:
    """Scale colours so the clip's median luminance is 1.0, then clamp.

    Median rather than mean: a few seconds of deep shade or glare should not
    shift what counts as "ordinary" for the whole clip.
    """
    colors = np.asarray(colors, dtype=np.float64)
    luma = colors @ np.array([0.299, 0.587, 0.114])
    valid = np.isfinite(luma) & (luma > 0)
    if not valid.any():
        return np.ones_like(colors)

    scale = float(np.median(luma[valid]))
    if scale <= 0:
        return np.ones_like(colors)

    return np.clip(colors / scale, clamp[0], clamp[1])


def fill_gaps(times: np.ndarray, colors: np.ndarray) -> np.ndarray:
    """Interpolate samples that were never measured (NaN rows)."""
    result = colors.astype(np.float64).copy()
    for channel in range(result.shape[1]):
        column = result[:, channel]
        known = np.isfinite(column)
        if not known.any():
            result[:, channel] = 1.0
            continue
        result[~known, channel] = np.interp(
            times[~known], times[known], column[known]
        )
    return result


def sample_ambient(
    info: VideoInfo,
    times: np.ndarray,
    xs: np.ndarray,
    ys: np.ndarray,
    *,
    analysis_width: int = 320,
    stride: int = 3,
    progress: object = None,
) -> np.ndarray:
    """Mean RGB (0..1, normalized to the clip median) near each path sample.

    ``times``/``xs``/``ys`` describe where the character will be; the returned
    array is parallel to them.
    """
    times = np.asarray(times, dtype=np.float64)
    raw = np.full((times.size, 3), np.nan)

    order = np.argsort(times)
    sorted_times = times[order]
    cursor = 0

    for frame in iter_frames(info, analysis_width=analysis_width, stride=stride):
        if progress is not None:
            progress.update(1)

        # Advance to path samples this frame is nearest to; half a stride each
        # side keeps every sample assigned exactly once.
        window = stride / info.fps * 0.75
        while cursor < sorted_times.size and sorted_times[cursor] < frame.time - window:
            cursor += 1

        index = cursor
        height, width = frame.image.shape[:2]
        while index < sorted_times.size and sorted_times[index] <= frame.time + window:
            position = order[index]
            cx = int(np.clip(xs[position] * width, PATCH_HALF, width - PATCH_HALF - 1))
            cy = int(
                np.clip(
                    (ys[position] - BALL_LIFT) * height,
                    PATCH_HALF,
                    height - PATCH_HALF - 1,
                )
            )
            patch = frame.image[
                cy - PATCH_HALF : cy + PATCH_HALF + 1,
                cx - PATCH_HALF : cx + PATCH_HALF + 1,
            ]
            # BGR -> RGB.
            raw[position] = patch.reshape(-1, 3).mean(axis=0)[::-1] / 255.0
            index += 1

    return normalize_ambient(fill_gaps(times, raw))
