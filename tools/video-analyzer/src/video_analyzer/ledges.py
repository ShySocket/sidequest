"""Top edges of the things the character can run along.

The ground segmentation answers "where is the road", which is not the same
question as "where can the character stand". Measured against the tracked
marker, its foot lies inside the segmented ground only 38% of the time, and
where it does not it is almost always *above* it - because the designer runs it
along railings, hedges and kerbs that sit on top of the road plane.

So each runnable thing needs its own line. A detection box says where the object
is; SAM 2 prompted with that box says which pixels are it; the topmost pixel per
column is the surface to stand on.

Detections of one class are unioned before the edge is taken. A hedge comes back
as five separate boxes covering parts of one continuous thing, and reading an
edge from each in turn would produce five disconnected ledges where there is
really one.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

from .decode import VideoInfo, iter_frames
from .detect import DetectionFrame
from .segment import NO_GROUND

MODEL_ID = "facebook/sam2.1-hiera-small"

RUNNABLE_CLASSES = {
    "railing": "rail",
    "fence": "rail",
    "bush": "hedge",
}
"""Detection label -> the surface it becomes. Railing and fence are one thing."""

MIN_SCORE = 0.28
MAX_BOXES_PER_CLASS = 6


@dataclass
class LedgeFrame:
    time: float
    lines: dict[str, np.ndarray] = field(default_factory=dict)
    """Surface name -> top edge per column, NO_GROUND where absent."""

    width: int = 0
    height: int = 0

    def normalized(self, surface: str) -> np.ndarray | None:
        line = self.lines.get(surface)
        if line is None:
            return None
        out = line.astype(np.float64)
        out[line == NO_GROUND] = np.nan
        return out / self.height


def top_edge(mask: np.ndarray) -> np.ndarray:
    """Topmost set pixel per column, NO_GROUND where the column is empty."""
    boolean = mask.astype(bool)
    has = boolean.any(axis=0)
    return np.where(has, boolean.argmax(axis=0), NO_GROUND).astype(np.int32)


def clean_line(line: np.ndarray, max_step: float, smooth_window: int = 15) -> np.ndarray:
    """Drop outliers and smooth, so the ledge is something a ball can roll on.

    Segmentation edges catch foliage wisps and fence posts, which would read as
    the surface jumping by a tenth of the frame between neighbouring columns.
    """
    result = line.astype(np.float64)
    valid = line != NO_GROUND
    if valid.sum() < 8:
        return line

    median = np.median(result[valid])
    outlier = valid & (np.abs(result - median) > max_step)
    result[outlier] = np.nan
    result[~valid] = np.nan

    known = ~np.isnan(result)
    if known.sum() < 8:
        return line

    indices = np.arange(result.size)
    result[~known] = np.interp(indices[~known], indices[known], result[known])

    window = max(3, smooth_window | 1)
    padded = np.pad(result, window // 2, mode="edge")
    smoothed = np.convolve(padded, np.ones(window) / window, mode="valid")

    # Columns that were never seen stay unknown; only the interior is filled.
    first, last = indices[known][0], indices[known][-1]
    out = np.full(result.size, float(NO_GROUND))
    out[first : last + 1] = smoothed[first : last + 1]
    return out.astype(np.int32)


class LedgeExtractor:
    """Lazy-loading SAM 2, prompted with detection boxes."""

    def __init__(self, model_id: str = MODEL_ID, device: str | None = None) -> None:
        self.model_id = model_id
        self._device = device
        self._model = None
        self._processor = None

    def _ensure_loaded(self) -> None:
        if self._model is not None:
            return

        import torch
        from transformers import Sam2Model, Sam2Processor

        if self._device is None:
            self._device = "mps" if torch.backends.mps.is_available() else "cpu"

        self._processor = Sam2Processor.from_pretrained(self.model_id)
        self._model = Sam2Model.from_pretrained(self.model_id).to(self._device).eval()

    @property
    def device(self) -> str:
        self._ensure_loaded()
        return self._device  # type: ignore[return-value]

    def _union_mask(self, image: np.ndarray, boxes: list[list[float]]) -> np.ndarray | None:
        import torch

        self._ensure_loaded()
        if not boxes:
            return None

        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        inputs = self._processor(  # type: ignore[union-attr]
            images=rgb, input_boxes=[boxes], return_tensors="pt"
        ).to(self._device)

        with torch.no_grad():
            outputs = self._model(**inputs, multimask_output=False)  # type: ignore[misc]

        masks = self._processor.post_process_masks(  # type: ignore[union-attr]
            outputs.pred_masks.cpu(), inputs["original_sizes"]
        )[0]

        union = None
        for index in range(masks.shape[0]):
            single = masks[index, 0].numpy() > 0
            union = single if union is None else (union | single)
        return union

    def analyze(self, frame, detections: list) -> LedgeFrame:
        height, width = frame.image.shape[:2]
        result = LedgeFrame(time=frame.time, width=width, height=height)

        grouped: dict[str, list[list[float]]] = {}
        for detection in detections:
            surface = RUNNABLE_CLASSES.get(detection.label)
            if surface is None or detection.score < MIN_SCORE:
                continue
            grouped.setdefault(surface, []).append(list(detection.box))

        for surface, boxes in grouped.items():
            boxes.sort(key=lambda b: -(b[2] - b[0]))
            mask = self._union_mask(frame.image, boxes[:MAX_BOXES_PER_CLASS])
            if mask is None:
                continue
            result.lines[surface] = clean_line(top_edge(mask), max_step=height * 0.22)

        return result


def save_ledges(path: Path, frames: list[LedgeFrame]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    surfaces = sorted({name for frame in frames for name in frame.lines})
    payload: dict[str, np.ndarray] = {
        "times": np.array([f.time for f in frames], dtype=np.float64),
        "size": np.array([frames[0].width, frames[0].height], dtype=np.int32),
        "surfaces": np.array(surfaces),
    }
    for surface in surfaces:
        payload[f"line_{surface}"] = np.stack(
            [
                f.lines.get(surface, np.full(f.width, NO_GROUND, dtype=np.int32))
                for f in frames
            ]
        ).astype(np.int16)
    np.savez_compressed(path, **payload)


def load_ledges(path: Path) -> tuple[np.ndarray, dict[str, np.ndarray], int, int]:
    """Return (times, surface -> [frames x width] lines, width, height)."""
    data = np.load(path, allow_pickle=False)
    width, height = (int(v) for v in data["size"])
    surfaces = [str(s) for s in data["surfaces"]]
    lines = {s: data[f"line_{s}"].astype(np.int32) for s in surfaces}
    return data["times"], lines, width, height
