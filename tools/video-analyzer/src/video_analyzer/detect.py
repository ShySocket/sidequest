"""Zero-shot obstacle detection with Grounding DINO.

Detection here does one job: putting a *name* on things. Where an obstacle sits
and when it reaches the character both come from the run line in ``segment.py``,
which is more reliable for the purpose - it is tied to the surface the character
is actually standing on, and it catches objects no class list anticipated.

Validated on this footage before any fine-tuning was attempted: cars, traffic
signs and poles come back accurately out of the box (14 cars in one parking-lot
frame, tight boxes), which is why no labelling step exists in this pipeline.
Asking the same model for "sidewalk" returns a full-frame-width box, which is
why surfaces are segmented instead.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

MODEL_ID = "IDEA-Research/grounding-dino-base"

PROMPT = "sidewalk. railing. fence. traffic sign. bush. car. pole. tree."

CANONICAL_LABELS = (
    "traffic sign",
    "railing",
    "fence",
    "sidewalk",
    "bush",
    "car",
    "pole",
    "tree",
)
"""Checked in order when untangling a merged phrase; earliest match wins.

Grounding DINO grounds spans of the prompt rather than emitting one label per
box, so it returns phrases like "railing fence" or "sidewalk railing". The order
puts the more specific and more gameplay-relevant classes first.
"""

SURFACE_LABELS = frozenset({"sidewalk", "railing", "fence"})
"""Classes that describe something to run *on*, not something to dodge."""


@dataclass(frozen=True)
class Detection:
    label: str
    score: float
    box: tuple[float, float, float, float]
    """x1, y1, x2, y2 in pixels at the analysis resolution."""

    @property
    def centre_x(self) -> float:
        return 0.5 * (self.box[0] + self.box[2])

    def spans_column(self, x: float) -> bool:
        return self.box[0] <= x <= self.box[2]

    @property
    def is_surface(self) -> bool:
        return self.label in SURFACE_LABELS


@dataclass(frozen=True)
class DetectionFrame:
    index: int
    time: float
    detections: list[Detection] = field(default_factory=list)


def normalize_label(phrase: str) -> str:
    """Reduce a grounded phrase to a single canonical class."""
    lowered = phrase.lower().strip()
    for candidate in CANONICAL_LABELS:
        if candidate in lowered:
            return candidate
    return lowered or "unknown"


class ObstacleDetector:
    """Lazy-loading Grounding DINO wrapper. Loading costs ~80s, so reuse one."""

    def __init__(
        self,
        model_id: str = MODEL_ID,
        prompt: str = PROMPT,
        device: str | None = None,
        box_threshold: float = 0.30,
        text_threshold: float = 0.25,
    ) -> None:
        self.model_id = model_id
        self.prompt = prompt
        self.box_threshold = box_threshold
        self.text_threshold = text_threshold
        self._device = device
        self._model = None
        self._processor = None

    def _ensure_loaded(self) -> None:
        if self._model is not None:
            return

        import torch
        from transformers import AutoModelForZeroShotObjectDetection, AutoProcessor

        if self._device is None:
            self._device = "mps" if torch.backends.mps.is_available() else "cpu"

        self._processor = AutoProcessor.from_pretrained(self.model_id)
        self._model = (
            AutoModelForZeroShotObjectDetection.from_pretrained(self.model_id)
            .to(self._device)
            .eval()
        )

    @property
    def device(self) -> str:
        self._ensure_loaded()
        return self._device  # type: ignore[return-value]

    def detect(self, image: np.ndarray) -> list[Detection]:
        import torch

        self._ensure_loaded()
        height, width = image.shape[:2]
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)

        inputs = self._processor(  # type: ignore[union-attr]
            images=rgb, text=self.prompt, return_tensors="pt"
        ).to(self._device)

        with torch.no_grad():
            outputs = self._model(**inputs)  # type: ignore[misc]

        results = self._processor.post_process_grounded_object_detection(  # type: ignore[union-attr]
            outputs,
            threshold=self.box_threshold,
            text_threshold=self.text_threshold,
            target_sizes=[(height, width)],
        )[0]

        return [
            Detection(
                label=normalize_label(label),
                score=float(score),
                box=tuple(float(v) for v in box.tolist()),  # type: ignore[arg-type]
            )
            for box, score, label in zip(
                results["boxes"], results["scores"], results["text_labels"]
            )
        ]

    def analyze(self, frame) -> DetectionFrame:
        return DetectionFrame(
            index=frame.index, time=frame.time, detections=self.detect(frame.image)
        )


def save_detections(path: Path, frames: list[DetectionFrame], width: int, height: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "width": width,
        "height": height,
        "frames": [
            {
                "index": frame.index,
                "time": round(frame.time, 5),
                "detections": [
                    {
                        "label": d.label,
                        "score": round(d.score, 4),
                        "box": [round(v, 2) for v in d.box],
                    }
                    for d in frame.detections
                ],
            }
            for frame in frames
        ],
    }
    path.write_text(json.dumps(payload))


def load_detections(path: Path) -> tuple[list[DetectionFrame], int, int]:
    payload = json.loads(path.read_text())
    frames = [
        DetectionFrame(
            index=int(entry["index"]),
            time=float(entry["time"]),
            detections=[
                Detection(
                    label=d["label"], score=float(d["score"]), box=tuple(d["box"])
                )
                for d in entry["detections"]
            ],
        )
        for entry in payload["frames"]
    ]
    return frames, int(payload["width"]), int(payload["height"])
