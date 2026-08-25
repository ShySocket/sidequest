# SidequestCapture

A one-button iOS capture app: records video out the vehicle window while
logging GPS (position + speed) and 100 Hz device motion, all stamped with a
single monotonic clock so the sensor log aligns exactly with the video.
Each session becomes a map candidate for the video-runner pipeline —
GPS speed replaces the optical-flow speed *estimate* the analyzer otherwise
has to make.

## Run it on your phone

1. `open SidequestCapture/SidequestCapture.xcodeproj`
2. Select the SidequestCapture target → Signing & Capabilities → pick your
   team (bundle id `com.sidequest.capture`, automatic signing).
3. Plug in your iPhone, select it as the destination, Run.
4. First launch asks for camera + location. Grant both.

Camera and GPS are real-hardware features — the simulator will build but
can't capture.

## Using it

- Mount the phone against the window, wait for the GPS dot to turn green.
- Tap record. HUD shows live speed, GPS accuracy, and elapsed time.
- Tap again to stop. The session finalizes to
  `Documents/Sessions/<yyyyMMdd-HHmmss>/`:
  - `video.mov` — 1080p30, back wide camera, stabilization off (raw
    geometry for the CV stages)
  - `sensors.jsonl` — one JSON object per line: `gps` fixes and `motion`
    samples, each with `t` = `CACurrentMediaTime()`
  - `session.json` — device, start time, and `clock.videoStartHostTime`;
    video time of any sample is `t - videoStartHostTime`

## Getting sessions onto the Mac

The app has file sharing enabled, so sessions are visible in:
- **Finder** → your iPhone → Files tab → SidequestCapture (drag folders out)
- **Files app** on the phone → On My iPhone → SidequestCapture → Sessions
  (long-press a session folder → Share → AirDrop to the Mac)

Then validate + convert:

```bash
python3 resources/scripts/ingest_capture.py ~/Downloads/20260810-143201
```

which checks the session, prints a summary, and writes `speed.csv`
(video-time-keyed GPS speed) and `track.geojson` (drop onto geojson.io to
eyeball the route).

## Design notes

- **One clock.** Everything is stamped with the mach host clock
  (`CACurrentMediaTime`). The video's t=0 host time is captured in the
  `didStartRecordingTo` delegate (first frame on disk) — alignment error is
  at most a frame or two, negligible for speed-over-distance work.
- **30 fps locked, stabilization off** — consistent input for
  `decode.py`/`speed.py`, and no stabilization warp under the ledge/marker
  stages.
- **GPS warm before recording** — location updates start at app launch so
  the first recorded fix isn't a cold start; pre-roll fixes before video
  t=0 are logged too (negative video time, filtered by ingest).
- **Crash-tolerant** — `AVCaptureMovieFileOutput` writes movie fragments,
  and the sensor log flushes every ~16 KB, so a dead battery mid-drive
  loses seconds, not the session.

## The user-sourced capture idea

Running this capture inside the shipped game while players ride is
technically feasible (the CV game already uses the live rear camera), but
it must be **explicit opt-in**: a consent screen stating video + precise
location are recorded and uploaded, a visible recording indicator, and App
Store privacy labels that declare it. iOS enforces the visible camera/
location indicators regardless, and silent collection is an App Store
rejection and a privacy-law problem. When that version happens, this app's
capture core (CaptureController + SensorRecorder) is the piece to reuse
behind the consent gate.
