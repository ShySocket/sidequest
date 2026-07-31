"""Ground-surface segmentation with SAM 2.

What the game needs is a *run line*: for every column of the frame, the y at
which the character's feet would sit. Detection boxes cannot supply this. A
bounding box around "sidewalk" is essentially the whole lower half of the frame,
which says nothing about where the ground actually is - verified on this footage,
where Grounding DINO returns exactly that.

SAM 2 does supply it. Prompting with a few points along the very bottom of the
frame - which, whatever the car is driving past, is almost always the ground
plane - yields a clean mask of the drivable/walkable surface. Its upper boundary
per column is the run line.

The boundary also comes with a useful side effect: objects standing *on* the
ground interrupt the mask, so the run line notches upward around parked cars,
bollards, poles and chains. Those notches are an obstacle signal derived from
geometry rather than from class labels, and they line up with the surface the
character is actually running on.

Only the run line is cached, never the masks. Full masks for this clip would be
~1.4 GB; run lines are a few MB, and the ground region can be reconstructed from
one when needed (everything below the boundary is ground).
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

MODEL_ID = "facebook/sam2.1-hiera-small"

NO_GROUND = -1
"""Run-line sentinel for a column where no ground surface was found."""

PROMPT_ROW = 0.95
"""Vertical position of the point prompts, as a fraction of frame height."""

PROMPT_COLUMNS = (0.25, 0.5, 0.75)

MIN_GROUND_COVERAGE = 0.02
MAX_GROUND_COVERAGE = 0.60
"""Plausible share of the frame occupied by ground.

SAM 2 segments whatever the prompt points land on; it has no semantic notion of
"ground". When the car hugs a building, the points land on the wall and the
whole wall comes back as the surface - on this clip that shows up at t~61s as
94.5% coverage against a median of 19.5%.

A camera pointed sideways out of a window sees the ground occupying the lower
part of the frame and nothing like all of it, so coverage far outside this band
means the mask is not a ground plane and must not be trusted.
"""


@dataclass(frozen=True)
class SurfaceFrame:
    """The ground surface for a single frame."""

    index: int
    time: float
    run_line: np.ndarray
    """Upper boundary of the ground per column, in pixels; NO_GROUND if absent."""

    coverage: float
    """Fraction of the frame classified as ground."""

    width: int
    height: int

    @property
    def valid_fraction(self) -> float:
        """Share of columns that found any ground at all."""
        return float(np.mean(self.run_line != NO_GROUND))

    @property
    def is_plausible_ground(self) -> bool:
        """Whether this mask can be a ground plane at all.

        Note this is not the same test as ``valid_fraction``: a wall segmented
        edge to edge has ground in *every* column, so only coverage catches it.
        """
        return MIN_GROUND_COVERAGE <= self.coverage <= MAX_GROUND_COVERAGE

    def normalized_run_line(self) -> np.ndarray:
        """Run line as y in 0..1, with NaN where there is no ground.

        Normalizing here keeps the level file resolution-independent, so the
        analysis resolution can change without invalidating a level.
        """
        line = self.run_line.astype(np.float64)
        line[self.run_line == NO_GROUND] = np.nan
        return line / self.height

    def ground_mask(self) -> np.ndarray:
        """Reconstruct the ground region: everything below the run line."""
        rows = np.arange(self.height)[:, None]
        boundary = np.where(self.run_line == NO_GROUND, self.height + 1, self.run_line)
        return (rows >= boundary[None, :]).astype(np.uint8) * 255


def extract_run_line(mask: np.ndarray) -> np.ndarray:
    """Topmost ground pixel per column.

    Vectorized over columns: ``argmax`` on a boolean column gives the first True,
    and a column with no ground is caught by testing ``any`` separately.
    """
    boolean = mask.astype(bool)
    has_ground = boolean.any(axis=0)
    top = boolean.argmax(axis=0).astype(np.int32)
    return np.where(has_ground, top, NO_GROUND)


class GroundSegmenter:
    """Lazy-loading SAM 2 wrapper. Loading costs ~10s, so reuse one instance."""

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

    def segment(self, image: np.ndarray) -> np.ndarray:
        """Return a boolean ground mask for a BGR frame."""
        import torch

        self._ensure_loaded()
        height, width = image.shape[:2]

        points = [[[[int(width * fx), int(height * PROMPT_ROW)] for fx in PROMPT_COLUMNS]]]
        labels = [[[1] * len(PROMPT_COLUMNS)]]

        # cvtColor rather than a ::-1 slice: the slice yields a negative-stride
        # view, which torch.from_numpy rejects.
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)

        inputs = self._processor(  # type: ignore[union-attr]
            images=rgb,
            input_points=points,
            input_labels=labels,
            return_tensors="pt",
        ).to(self._device)

        with torch.no_grad():
            outputs = self._model(**inputs, multimask_output=False)  # type: ignore[misc]

        masks = self._processor.post_process_masks(  # type: ignore[union-attr]
            outputs.pred_masks.cpu(), inputs["original_sizes"]
        )[0]
        return masks[0, 0].numpy() > 0

    def analyze(self, frame) -> SurfaceFrame:
        mask = self.segment(frame.image)
        height, width = mask.shape
        return SurfaceFrame(
            index=frame.index,
            time=frame.time,
            run_line=extract_run_line(mask),
            coverage=float(mask.mean()),
            width=width,
            height=height,
        )


def save_surfaces(path: Path, surfaces: list[SurfaceFrame]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        path,
        indices=np.array([s.index for s in surfaces], dtype=np.int32),
        times=np.array([s.time for s in surfaces], dtype=np.float64),
        run_lines=np.stack([s.run_line for s in surfaces]).astype(np.int16),
        coverage=np.array([s.coverage for s in surfaces], dtype=np.float32),
        size=np.array([surfaces[0].width, surfaces[0].height], dtype=np.int32),
    )


def load_surfaces(path: Path) -> list[SurfaceFrame]:
    data = np.load(path)
    width, height = (int(v) for v in data["size"])
    return [
        SurfaceFrame(
            index=int(index),
            time=float(time),
            run_line=run_line.astype(np.int32),
            coverage=float(coverage),
            width=width,
            height=height,
        )
        for index, time, run_line, coverage in zip(
            data["indices"], data["times"], data["run_lines"], data["coverage"]
        )
    ]
