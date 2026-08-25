"""The occlusion rules, exercised with synthetic detections.

Each test states one clause of the contract in occlude.py: depth decides,
overlap gates, dodges and frontSpans suppress, behindSpans forces, and
flicker never ships.
"""

import numpy as np
import pytest

from video_analyzer.detect import Detection, DetectionFrame
from video_analyzer.occlude import (
    MaskAtlas,
    OcclusionConfig,
    derive_occluder_tracks,
    foreground_entries,
    strip_at,
)

SIZE = (960, 540)

# The ball rolls at column 0.4 on a ground line at y 0.6 throughout.
TIMES = np.linspace(0.0, 10.0, 101)
GROUND = np.full(101, 0.6)
COLUMNS = np.full(101, 0.4)


def frames_with_pole(times, x=0.38, base_y=0.7, top_y=0.2, label="pole", score=0.9):
    """A static thin pole near the ball's column, seen at each time."""
    box = (x * 960, top_y * 540, (x + 0.02) * 960, base_y * 540)
    return [
        DetectionFrame(
            index=i,
            time=t,
            detections=[Detection(label=label, score=score, box=box)],
        )
        for i, t in enumerate(times)
    ]


def derive(frames, config=OcclusionConfig()):
    return derive_occluder_tracks(frames, SIZE, TIMES, GROUND, COLUMNS, config)


def test_base_below_ground_line_occludes():
    # Base at 0.7 vs ground 0.6: nearer the camera, so the ball goes behind.
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17)))
    assert len(tracks) == 1
    assert tracks[0].label == "pole"
    assert len(tracks[0].samples) >= 2


def test_base_above_ground_line_stays_in_front():
    # Base at 0.5 vs ground 0.6: farther away; occluding would be wrong.
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17), base_y=0.5))
    assert tracks == []


def test_far_column_is_ignored():
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17), x=0.8))
    assert tracks == []


def test_unlisted_label_is_ignored():
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17), label="car"))
    assert tracks == []


def test_low_score_is_ignored():
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17), score=0.2))
    assert tracks == []


def test_behind_span_forces_through_failed_depth_test():
    config = OcclusionConfig(force_spans=((0.9, 2.1),))
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17), base_y=0.5), config)
    assert len(tracks) == 1


def test_front_span_suppresses():
    config = OcclusionConfig(suppress_spans=((0.9, 2.1),))
    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17)), config)
    assert tracks == []


def test_elevated_surface_suppresses():
    # On a railing the ball fronts everything planted beyond it; the depth
    # test cannot see the railing's footing, so it must not get a vote.
    frames = frames_with_pole(np.arange(1.0, 2.0, 0.17))
    tracks = derive_occluder_tracks(
        frames, SIZE, TIMES, GROUND, COLUMNS, OcclusionConfig(),
        elevated=np.ones(TIMES.size, dtype=bool),
    )
    assert tracks == []


def test_behind_span_forces_through_elevated_surface():
    frames = frames_with_pole(np.arange(1.0, 2.0, 0.17))
    tracks = derive_occluder_tracks(
        frames, SIZE, TIMES, GROUND, COLUMNS,
        OcclusionConfig(force_spans=((0.9, 2.1),)),
        elevated=np.ones(TIMES.size, dtype=bool),
    )
    assert len(tracks) == 1


def test_dodge_becomes_a_suppress_span():
    class FakeTimeline:
        behind_spans = []
        front_spans = []
        occluder_labels = None
        events = [{"type": "dodge", "time": 1.5}]
        hidden = [{"from": 5.0, "to": 7.0}]

    config = OcclusionConfig.from_timeline(FakeTimeline())
    assert any(a <= 1.5 <= b for a, b in config.suppress_spans)
    # A hidden ball needs no occluder either.
    assert any(a <= 6.0 <= b for a, b in config.suppress_spans)


def test_single_sighting_is_flicker_and_dropped():
    tracks = derive(frames_with_pole([1.0]))
    assert tracks == []


def test_center_jump_splits_tracks():
    # Two poles seen alternately would chain into one sweeping strip without
    # the continuity rule; a 0.2 frame-width jump in 0.17s must split.
    frames = frames_with_pole(np.arange(1.0, 2.0, 0.17), x=0.3) + frames_with_pole(
        np.arange(2.17, 3.2, 0.17), x=0.5
    )
    frames.sort(key=lambda f: f.time)
    tracks = derive(frames)
    assert len(tracks) == 2
    assert tracks[0].id == 1 and tracks[1].id == 2


def test_entries_are_distance_keyed_and_sorted():
    class UnitDistanceMap:
        def distance_at(self, time):
            return 100.0 * time

    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17)))
    entries = foreground_entries(tracks, UnitDistanceMap())
    assert entries == sorted(entries, key=lambda e: e["d"])
    assert all(e["id"] == 1 for e in entries)
    assert all("mask" not in e for e in entries)
    assert entries[0]["x1"] < entries[0]["x2"]
    assert entries[0]["d"] == pytest.approx(100.0, abs=1.0)


TRACK = [
    {"d": 100.0, "id": 1, "x1": 0.30, "y1": 0.1, "x2": 0.34, "y2": 0.8},
    {"d": 110.0, "id": 1, "x1": 0.32, "y1": 0.1, "x2": 0.36, "y2": 0.8},
    {"d": 120.0, "id": 1, "x1": 0.34, "y1": 0.1, "x2": 0.38, "y2": 0.8},
]


def test_strip_interpolates_inside_a_track():
    box, _ = strip_at(105.0, TRACK, 10000.0)
    assert box[0] == pytest.approx(0.31)


def test_strip_dies_immediately_past_the_track_edge():
    # Sample spacing is 10; the last sighting may linger half that. At +5.1
    # past the end there must be NO strip - a stale silhouette parked where
    # the pole used to be is exactly the "hole bitten out of the ball" bug.
    assert strip_at(125.0, TRACK, 10000.0) is not None
    assert strip_at(125.2, TRACK, 10000.0) is None
    assert strip_at(94.9, TRACK, 10000.0) is None
    assert strip_at(95.1, TRACK, 10000.0) is not None


def test_strip_never_bridges_two_objects():
    entries = TRACK + [
        {"d": 400.0, "id": 2, "x1": 0.7, "y1": 0.2, "x2": 0.74, "y2": 0.9},
        {"d": 410.0, "id": 2, "x1": 0.72, "y1": 0.2, "x2": 0.76, "y2": 0.9},
    ]
    assert strip_at(260.0, entries, 10000.0) is None


def test_atlas_is_black_outside_silhouettes(monkeypatch):
    # Everything that is not positively an object silhouette must be black:
    # black occludes nothing, and bilinear sampling bleeds cell borders into
    # the strip, so a white background bites visible notches out of the ball.
    import video_analyzer.decode as decode
    from video_analyzer.occlude import CELL_GUTTER, OccluderTrack, bake_mask_atlas

    class Frame:
        time = 1.0
        image = np.zeros((540, 960, 3), dtype=np.uint8)

    monkeypatch.setattr(decode, "iter_frames", lambda *a, **k: iter([Frame()]))

    class StubMasker:
        def mask(self, image, box):
            out = np.zeros(image.shape[:2], dtype=bool)
            x1, y1, x2, y2 = (int(v) for v in box)
            # a thin pole in the middle of the box
            mid = (x1 + x2) // 2
            out[y1:y2, mid - 2 : mid + 2] = True
            return out

    class Info:
        fps = 30.0

    track = OccluderTrack(id=1, label="pole")
    track.samples.append(
        __import__("video_analyzer.occlude", fromlist=["OccluderSample"]).OccluderSample(
            time=1.0, x1=0.3, y1=0.1, x2=0.34, y2=0.8, label="pole"
        )
    )
    atlas = bake_mask_atlas(Info(), [track], masker=StubMasker())

    (u1, v1, u2, v2) = atlas.uvs[(1, 0)]
    ah, aw = atlas.image.shape
    content = np.zeros(atlas.image.shape, dtype=bool)
    content[
        int(round((1 - v2) * ah)) : int(round((1 - v1) * ah)),
        int(round(u1 * aw)) : int(round(u2 * aw)),
    ] = True
    assert (atlas.image[~content] == 0).all(), "non-silhouette texels must be black"
    assert atlas.image[content].max() == 255, "the silhouette itself was written"
    assert CELL_GUTTER > 0


def test_entries_carry_mask_uvs_when_atlas_present():
    class UnitDistanceMap:
        def distance_at(self, time):
            return 100.0 * time

    tracks = derive(frames_with_pole(np.arange(1.0, 2.0, 0.17)))
    uvs = {
        (1, i): (0.0, 0.5, 0.0625, 1.0) for i in range(len(tracks[0].samples))
    }
    atlas = MaskAtlas(
        image=np.zeros((128, 1024), dtype=np.uint8),
        uvs=uvs,
        coverages={},
        file_name="clip.occluders.png",
    )
    entries = foreground_entries(tracks, UnitDistanceMap(), atlas)
    assert all(e["mask"] == 1 for e in entries)
    assert all(e["v2"] > e["v1"] and e["u2"] > e["u1"] for e in entries)
