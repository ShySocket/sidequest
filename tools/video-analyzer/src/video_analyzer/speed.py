"""Ego-motion speed estimation from sparse optical flow.

A phone held against a side window points roughly perpendicular to travel, which
makes this much easier than a forward-facing dashcam: world motion is nearly
pure lateral translation, with no focus of expansion and no singularity along
the direction of travel. Tracked features simply sweep across the frame at a
rate proportional to speed / depth.

Two populations of pixels have to be rejected:

* **The car's own interior** — window frame, door trim, reflections. These are
  rigidly attached to the camera and never move, so they sit at zero flow. A
  plain median over all features would be dragged toward zero by them and would
  under-report speed badly.
* **Independently moving objects** — mainly passing and oncoming vehicles, which
  move faster or slower than the static world.

We handle the first by taking the median only over features that are actually
moving, and the second by relying on the median being robust to a minority of
outliers. At a genuine stop *no* feature is moving, the moving population is
empty, and speed correctly falls to zero.

Known limitation: depth
-----------------------
Flow scales as ``speed / depth``, so scene depth confounds the estimate. On
IMG_3775.mov, measuring five horizontal bands showed the near-ground band is
decisively the most stable proxy (IQR/median 0.62, versus 1.29-1.70 for bands
looking at the mid-field or the horizon), because the ground beside the car sits
at a roughly fixed distance while distant scenery does not.

It is a reduction, not a cure. Where the car passes close to a building the wall
fills the band and flow spikes: this clip shows a 4.5x spike at t~61s that is
depth, not acceleration. Narrowing the band only brings that to 3.3x, so
geometry alone cannot fix it.

The real fix is to restrict flow to pixels that are actually on the ground
plane, which needs the road/sidewalk segmentation masks from the surface stage.
``estimate_speed`` accepts a ``ground_mask`` callable for exactly that; until
those masks exist, the near-ground band plus robust statistics is the best
available approximation, and the resulting curve should be read as provisional.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field

import cv2
import numpy as np

from .decode import Frame, VideoInfo


@dataclass(frozen=True)
class SpeedConfig:
    roi_top: float = 0.88
    roi_bottom: float = 0.99
    """Vertical band of the frame to track, as a fraction of height.

    Defaults to the near ground beside the car, which measured as by far the
    most stable speed proxy on this footage (see the module docstring). The
    bottom 1% is trimmed because the frame edge picks up rolling-shutter smear.
    """

    max_features: int = 400
    feature_quality: float = 0.01
    min_feature_distance: int = 12

    motion_floor_px: float = 0.35
    """Per-frame displacement below which a feature counts as static.

    Sits just above LK's sub-pixel noise, so camera shake does not register as
    forward motion.
    """

    min_moving_features: int = 12
    """Below this many moving features the estimate is untrustworthy."""

    max_flow_error: float = 20.0
    smoothing_window: int = 9
    """Median-filter width, in samples, applied to the raw curve."""

    max_acceleration_fraction: float = 0.5
    """Largest believable speed change per second, as a fraction of median speed.

    Depth is fundamentally unrecoverable from monocular flow, so a scene that
    suddenly gets closer reads as a scene that suddenly got faster. Physics
    disambiguates: a car cannot change speed several-fold in a fraction of a
    second, so an excursion that steep is depth, not motion.

    0.5 is roughly twice hard acceleration for a car at this median speed, which
    leaves genuine dynamics untouched while clipping the artefacts. The Unity
    side already takes the same position via
    RunnerConfiguration.maximumAcceleration.
    """

    stop_threshold_fraction: float = 0.06
    """Fraction of median speed below which the vehicle counts as stopped."""


@dataclass
class SpeedResult:
    times: np.ndarray
    speeds: np.ndarray
    """Smoothed speed in pixels/second at the analysis resolution."""

    raw_speeds: np.ndarray
    confidence: np.ndarray
    """Fraction of tracked features that agreed, per sample, in 0..1."""

    stop_threshold: float = 0.0
    diagnostics: dict = field(default_factory=dict)


def _roi_mask(shape: tuple[int, int], config: SpeedConfig) -> np.ndarray:
    height, width = shape
    mask = np.zeros((height, width), dtype=np.uint8)
    top = int(height * config.roi_top)
    bottom = int(height * config.roi_bottom)
    mask[top:bottom, :] = 255
    return mask


def _frame_displacement(
    previous_gray: np.ndarray,
    gray: np.ndarray,
    config: SpeedConfig,
    mask: np.ndarray | None = None,
) -> tuple[float, float]:
    """Median horizontal displacement of moving features, and a confidence.

    Returns ``(pixels, confidence)``. Confidence is the share of successfully
    tracked features that were moving and agreed in direction.
    """
    if mask is None:
        mask = _roi_mask(previous_gray.shape, config)

    features = cv2.goodFeaturesToTrack(
        previous_gray,
        maxCorners=config.max_features,
        qualityLevel=config.feature_quality,
        minDistance=config.min_feature_distance,
        mask=mask,
    )
    if features is None or len(features) < config.min_moving_features:
        return 0.0, 0.0

    tracked, status, error = cv2.calcOpticalFlowPyrLK(
        previous_gray,
        gray,
        features,
        None,
        winSize=(21, 21),
        maxLevel=3,
        criteria=(cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT, 30, 0.01),
    )
    if tracked is None:
        return 0.0, 0.0

    ok = (status.ravel() == 1) & (error.ravel() < config.max_flow_error)
    if ok.sum() < config.min_moving_features:
        return 0.0, 0.0

    dx = (tracked[:, 0, 0] - features[:, 0, 0])[ok]

    # Keep only the features that are actually moving: the rest are the car's
    # own interior, which is rigid relative to the camera.
    moving = np.abs(dx) > config.motion_floor_px
    moving_count = int(moving.sum())
    if moving_count < config.min_moving_features:
        # Either a genuine stop or a frame we cannot trust. Both mean "no
        # forward progress"; find_stopped_spans() disambiguates using duration.
        return 0.0, 0.0

    moving_dx = dx[moving]

    # The world sweeps one way; oncoming traffic sweeps the other. The median is
    # robust to that minority, and directional agreement is a useful confidence.
    median_dx = float(np.median(moving_dx))
    if median_dx == 0.0:
        return 0.0, 0.0

    agreeing = int((np.sign(moving_dx) == np.sign(median_dx)).sum())
    confidence = agreeing / moving_count

    return abs(median_dx), confidence


def _median_filter(values: np.ndarray, window: int) -> np.ndarray:
    if window <= 1 or values.size == 0:
        return values.copy()
    if window % 2 == 0:
        window += 1
    half = window // 2
    padded = np.pad(values, half, mode="edge")
    strided = np.lib.stride_tricks.sliding_window_view(padded, window)
    return np.median(strided, axis=1)


def _limit_acceleration(
    times: np.ndarray,
    speeds: np.ndarray,
    max_change_per_second: float,
) -> np.ndarray:
    """Clip physically impossible upward excursions in the speed curve.

    Two passes, forward and backward, each capping how fast the curve may rise;
    the result is their elementwise minimum. Running it in both directions
    matters: a single forward pass would clip a spike's leading edge but leave
    its trailing edge, and would lag the whole curve. Taking the min of both
    envelopes brackets a spike from either side without shifting it in time.

    Only *rises* are limited. Real braking is a genuine sharp fall and the
    stop detector depends on it, so downward moves pass through untouched.
    """
    if max_change_per_second <= 0 or speeds.size < 2:
        return speeds.copy()

    intervals = np.diff(times)

    forward = speeds.copy()
    for index in range(1, speeds.size):
        ceiling = forward[index - 1] + max_change_per_second * intervals[index - 1]
        forward[index] = min(forward[index], ceiling)

    backward = speeds.copy()
    for index in range(speeds.size - 2, -1, -1):
        ceiling = backward[index + 1] + max_change_per_second * intervals[index]
        backward[index] = min(backward[index], ceiling)

    return np.minimum(forward, backward)


def estimate_speed(
    frames: list[Frame] | object,
    info: VideoInfo,
    config: SpeedConfig | None = None,
    *,
    progress: object = None,
    ground_mask: Callable[[Frame], np.ndarray | None] | None = None,
) -> SpeedResult:
    """Estimate a per-frame speed curve from an iterable of frames.

    ``ground_mask`` optionally returns a uint8 mask of ground-plane pixels for a
    frame, replacing the fixed ROI band. Supplying segmentation masks here
    addresses the depth confound described in the module docstring.

    Returning ``None`` from that callable means *no ground was visible*, and the
    sample is treated as unmeasurable and interpolated over rather than guessed
    at. Falling back to the fixed band would be worse than useless: where the
    ground is hidden it is usually hidden by a wall filling the frame, which is
    exactly what the band would then measure. Measured on IMG_3775.mov, falling
    back made the t~61s artefact worse (2.18x -> 2.37x), while interpolating
    removes it.

    Passing no callable at all is different from a callable returning ``None``:
    it means no segmentation is available, and the fixed band is all there is.
    """
    config = config or SpeedConfig()

    times: list[float] = []
    raw: list[float] = []
    confidences: list[float] = []

    previous_gray: np.ndarray | None = None
    previous_time = 0.0

    for frame in frames:
        gray = cv2.cvtColor(frame.image, cv2.COLOR_BGR2GRAY)

        if previous_gray is not None:
            interval = frame.time - previous_time
            if interval > 0:
                if ground_mask is not None:
                    mask = ground_mask(frame)
                    if mask is None:
                        # No ground visible: unmeasurable, not slow or fast.
                        times.append(frame.time)
                        raw.append(np.nan)
                        confidences.append(0.0)
                        previous_gray = gray
                        previous_time = frame.time
                        if progress is not None:
                            progress.update(1)
                        continue
                else:
                    mask = None

                pixels, confidence = _frame_displacement(previous_gray, gray, config, mask)
                times.append(frame.time)
                raw.append(pixels / interval)
                confidences.append(confidence)

        previous_gray = gray
        previous_time = frame.time

        if progress is not None:
            progress.update(1)

    if len(times) < 2:
        raise RuntimeError(
            f"only {len(times)} usable frame pairs; the clip is too short or failed to decode"
        )

    times_array = np.asarray(times, dtype=np.float64)
    raw_array = np.asarray(raw, dtype=np.float64)

    # Bridge unmeasurable samples before filtering. Speed is continuous, so
    # linear interpolation across a short gap beats any measurement taken of the
    # wrong surface.
    unmeasurable = np.isnan(raw_array)
    measurable = ~unmeasurable
    if not measurable.any():
        raise RuntimeError("no frame had a visible ground surface to measure")
    filled = raw_array.copy()
    if unmeasurable.any():
        filled[unmeasurable] = np.interp(
            times_array[unmeasurable], times_array[measurable], raw_array[measurable]
        )

    smoothed = _median_filter(filled, config.smoothing_window)

    positive = smoothed[smoothed > 0]
    median_speed = float(np.median(positive)) if positive.size else 0.0

    # The acceleration cap is expressed relative to the clip's own median, since
    # distances here are in pixels and carry no absolute scale.
    limited = _limit_acceleration(
        times_array, smoothed, median_speed * config.max_acceleration_fraction
    )
    clipped_fraction = float(np.mean(limited < smoothed - 1e-9))

    stop_threshold = median_speed * config.stop_threshold_fraction

    return SpeedResult(
        times=times_array,
        speeds=limited,
        raw_speeds=raw_array,
        confidence=np.asarray(confidences, dtype=np.float64),
        stop_threshold=stop_threshold,
        diagnostics={
            "median_speed_px_per_s": median_speed,
            "mean_confidence": float(np.mean(confidences)) if confidences else 0.0,
            "sample_count": int(times_array.size),
            "acceleration_clipped_fraction": clipped_fraction,
            "unmeasurable_fraction": float(np.mean(unmeasurable)),
        },
    )
