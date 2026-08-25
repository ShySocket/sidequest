#!/usr/bin/env bash
# Put the video and the authored level where Unity can load them at runtime.
#
# Both live in StreamingAssets, which Unity copies into the build verbatim and
# does not try to import. Neither is committed: the video is 91 MB, and the level
# is regenerated from timeline.json.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
target="$repo/SidequestV1/Assets/StreamingAssets"

video="${1:-$here/out/IMG_3775.play.mp4}"
level="${2:-$here/out/IMG_3775.authored.json}"

for file in "$video" "$level"; do
  if [[ ! -f "$file" ]]; then
    echo "error: missing $file" >&2
    echo "hint: run 'uv run analyze transcode ...' and 'uv run analyze author ...' first" >&2
    exit 1
  fi
done

mkdir -p "$target"
cp "$video" "$target/"
cp "$level" "$target/"

# The occluder silhouette atlas, when the author pass baked one. Its name is
# recorded inside the level file, so it is read from there rather than guessed.
atlas="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('foregroundMaskFile',''))" "$level")"
if [[ -n "$atlas" ]]; then
  if [[ -f "$(dirname "$level")/$atlas" ]]; then
    cp "$(dirname "$level")/$atlas" "$target/"
  else
    echo "error: level names occluder atlas '$atlas' but it is not next to the level" >&2
    exit 1
  fi
fi

echo "copied into $target:"
ls -lh "$target" | tail -n +2
