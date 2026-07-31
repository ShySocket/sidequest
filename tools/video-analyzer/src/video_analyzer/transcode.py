"""Produce a playback-friendly copy of the source clip.

Analysis and playback want different things from the same footage. Analysis
wants every original pixel and runs once, offline, where decode cost does not
matter. Playback has to decode a frame every 16 ms on a phone while the game
also renders, and 1080p HEVC is an expensive way to do that.

So the clip is transcoded once: half the pixels, H.264 instead of HEVC, no audio
track the game never plays. H.264 also happens to be the codec Android decodes
reliably, which HEVC is not.

The analysis pipeline keeps using the original, so nothing measured here is
degraded by the transcode.
"""

from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class TranscodeSettings:
    height: int = 720
    crf: int = 23
    preset: str = "medium"


def ffmpeg_executable() -> str:
    """The static ffmpeg that ships with imageio-ffmpeg.

    Avoids depending on a system install; this machine has no Homebrew.
    """
    import imageio_ffmpeg

    return imageio_ffmpeg.get_ffmpeg_exe()


def transcode(
    source: Path,
    destination: Path,
    settings: TranscodeSettings | None = None,
) -> Path:
    settings = settings or TranscodeSettings()
    destination.parent.mkdir(parents=True, exist_ok=True)

    command = [
        ffmpeg_executable(),
        "-y",
        "-i", str(source),
        # -2 keeps the width even and preserves aspect, which H.264 requires.
        "-vf", f"scale=-2:{settings.height}",
        "-c:v", "libx264",
        "-preset", settings.preset,
        "-crf", str(settings.crf),
        # yuv420p is the pixel format every hardware decoder handles.
        "-pix_fmt", "yuv420p",
        # Seeking lands on keyframes, so a tighter interval makes scrubbing and
        # the director's resync land closer to where they were asked to.
        "-g", "30",
        "-an",
        "-movflags", "+faststart",
        str(destination),
    ]

    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        tail = "\n".join(result.stderr.strip().splitlines()[-12:])
        raise RuntimeError(f"ffmpeg failed ({result.returncode}):\n{tail}")

    return destination
