#!/usr/bin/env bash
# End-to-end: a new car-window clip (+ optional marker overlay) -> authored,
# audited level in Unity's StreamingAssets.
#
#   ./new-clip.sh /path/to/CLIP.mov [/path/to/OVERLAY.mp4]
#
# Every stage caches under out/<name>.*, so re-running skips finished work.
# Expect ~20-25 min of model passes for a ~90s clip on first run.
# See resources/PIPELINE_PLAYBOOK.md for what each stage does and how to tune.
set -euo pipefail

clip="${1:?usage: new-clip.sh CLIP.mov [OVERLAY.mp4]}"
overlay="${2:-}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

name="$(basename "${clip%.*}")"
out="out/$name"

run() { echo; echo "==> $*"; "$@"; }

[[ -f "$out.play.mp4" ]] || run uv run analyze transcode "$clip" -o "$out.play.mp4"
[[ -f "$out.surfaces.npz" ]] || run uv run analyze surface "$clip" -o "$out.surfaces.npz" --stride 2
[[ -f "$out.detections.json" ]] || run uv run analyze detect "$clip" -o "$out.detections.json" --stride 5
[[ -f "$out.ledges.npz" ]] || run uv run analyze ledges "$clip" -o "$out.ledges.npz" \
    --detections "$out.detections.json" --stride 5

marker_args=()
if [[ -n "$overlay" ]]; then
  [[ -f "$out.track.json" ]] || run uv run analyze track "$overlay" -o "$out.track.json" --stride 2
  marker_args=(--marker "$out.track.json")
fi

run uv run analyze author "$clip" -o "$out.authored.json" \
    --surfaces "$out.surfaces.npz" \
    --timeline timeline.json \
    --ledges "$out.ledges.npz" \
    --detections "$out.detections.json" \
    --playback-file "$name.play.mp4" \
    "${marker_args[@]}"

# The gate: non-zero exit stops before anything ships to Unity.
run uv run analyze audit --level "$out.authored.json" \
    --surfaces "$out.surfaces.npz" --ledges "$out.ledges.npz" \
    --detections "$out.detections.json"

# Occlusion gate + eyeball check: --verify steps the whole level through the
# game's own strip logic and fails if any frame erases ball pixels with no
# detected object there; the stills are the human acceptance test on top.
run uv run analyze occluders "$clip" --level "$out.authored.json" \
    --detections "$out.detections.json" --verify \
    -o "out/$name.occluders"

run ./copy-to-unity.sh "$out.play.mp4" "$out.authored.json"

echo
echo "Done. In Unity: Tools > Sidequest > Build Video Runner (once), then Play."
echo "If the level name changed, update levelFileName on the LevelDirector."
