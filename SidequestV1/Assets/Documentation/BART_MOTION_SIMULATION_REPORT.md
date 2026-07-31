# BART Motion Simulation and Offline Sensor Fusion

## Outcome

The movement estimator was replayed through a deterministic Downtown Berkeley
to Montgomery Street rail profile at 50 Hz. The replay covers:

- Downtown Berkeley, Ashby, MacArthur, 19th St Oakland, 12th St Oakland,
  West Oakland, Embarcadero, and Montgomery St
- acceleration, cruise, service braking, station settling, and dwell
- 10% under-reported accelerometer magnitude
- vibration/noise and continuous phone attitude changes
- accurate, weak/intermittent, and completely missing GPS
- a full GPS blackout between West Oakland and Embarcadero

Final result:

| Metric | Result |
|---|---:|
| Mean absolute physical-speed error | 1.54 m/s |
| 95th percentile physical-speed error | 3.11 m/s |
| Maximum confirmed-stop latency | 0.50 s |
| False station stops | 0 |
| Edit Mode regression suite | 51 passed, 0 failed |
| Play Mode regression suite | 5 passed, 0 failed |

The simulation source is `Assets/Tests/EditMode/BartRideSimulationTests.cs`.
Cars, buses, road turns, short hard stops, partial brakes, and GPS-outage
restart behavior are covered separately in
`Assets/Tests/EditMode/GroundTransportSimulationTests.cs`.

## Why the Train and Character Previously Diverged

### Braking was mistaken for being stopped

The previous estimator converted roughly 0.18 seconds of hard braking directly
to zero speed. A train remains in motion throughout a long braking ramp. BART
reports that even an 80 mph braking test takes about 20 to 25 seconds to reach
zero. The early zero made the character stop while the train was still moving.

The estimator now integrates the braking ramp. It confirms a stop only when:

- the integrated speed reaches the stationary band;
- fresh GPS reports a stationary fix; or
- a sustained rail-like braking profile ends in quiet motion and the remaining
  speed is consistent with accelerometer under-reporting.

### Raw GPS speed was too noisy

Speed calculated from two adjacent positions amplifies normal location error.
This is especially visible near tunnel mouths and among downtown buildings.

`GpsSpeedWindow` now measures displacement over a 2.5-to-6-second window and
uses the median of the available baselines. The fusion layer also:

- weights a fix by horizontal accuracy;
- rejects implausible single-fix speed jumps;
- requires consistent evidence before leaving a station lock; and
- blends GPS back in after an outage.

### Missing GPS incorrectly implied slowing down

Constant velocity produces approximately zero acceleration. In a tunnel, zero
acceleration means "continue at the current velocity," not "decay toward zero."
The old 0.98-per-second fallback removed about 45% of the speed in 30 seconds.

The new estimator holds velocity while the motion sensor is active. Retention
decay is used only when both GPS and the motion sensor have stopped updating,
and the default retention is now 0.999 per second.

### Phone rotation invalidated the learned axis

The accelerometer reports in phone/device coordinates. A phone rotating in a
hand or pocket used to rotate the apparent train-forward direction as well.

Every sample is now transformed by the attitude sensor into a stable reference
frame. Candidate forward axes are learned from correlation with GPS speed
changes. GPS course changes rotate the learned direction during road turns,
and a confirmed stop allows the next departure to establish a new direction.

### GPS and acceleration measure different things

GPS position differences provide a delayed average speed. The accelerometer
provides immediate change in velocity but not absolute speed. The complementary
estimator therefore predicts continuously from acceleration and uses GPS as an
accuracy-weighted absolute correction.

## Runtime and Offline Behavior

The runtime does not call a web API, load a map, or require an internet
connection. `Input.location`, the linear acceleration sensor, and the attitude
sensor feed local calculations.

| Situation | Behavior |
|---|---|
| Good GPS | Accuracy-weighted GPS correction plus acceleration prediction |
| Weak GPS | Low-weight correction and multipath/outlier protection |
| Tunnel/no GPS | Accelerometer dead reckoning and constant-velocity hold |
| Normal braking | Integrates deceleration; character keeps moving |
| Braking magnitude too low | End-of-braking quiet phase can close a small residual speed |
| Confirmed stop | Immediate zero and short stop hold |
| GPS returns | Smooth correction over the configured return interval |
| Motion sensor also disappears | Slow configurable retention decay |

## Fundamental Limit

No phone-only algorithm can determine absolute constant velocity from an
accelerometer. If the app starts after the train is already cruising
underground, with no prior GPS fix and no observed departure, absolute speed is
unobservable. The safe result is low confidence rather than an invented speed.

For best results, start tracking at the station or while GPS is available.
An optional route/profile prior could estimate an underground cold start, but
that would be a guess and should not silently override sensor confidence.

## Character Motion Semantics

`MovementEstimator` now targets the train's physical speed and stop timing.
`VehicleSpeedMapper` intentionally converts metres per second into game units
with a softened curve, and `VehicleSpeedFilter` adds a short visual smoothing
interval. Therefore:

- acceleration, braking, cruise changes, and stops should occur with the train;
- literal world-space metres travelled by the character are not intended to
  equal the train's metres travelled.

If literal 1:1 distance is desired, use a linear metres-to-world-units scale and
remove the softened gameplay curve. That is a game-design decision, separate
from sensor accuracy.

## Physical-Device Validation

Simulation protects the algorithm from known failure modes but does not replace
an actual passenger recording. A safe field pass should:

1. Start on the Downtown Berkeley platform before departure.
2. Keep the debug telemetry visible or record it without interacting while the
   train moves.
3. Note door-close/departure and door-open/arrival times at every station.
4. Repeat once with the phone held and once in a pocket or bag.
5. Test airplane mode separately; GPS availability may change, but the
   estimator itself has no network dependency.
6. Compare estimated stop latency and speed error with the replay thresholds.

Testing must be performed by a passenger. Do not operate or tune the app while
driving.

## Reference Data

- BART GTFS permalink:
  https://www.bart.gov/dev/schedules/google_transit.zip
- BART system facts:
  https://www.bart.gov/about/history/facts
- BART Fleet of the Future braking-test description:
  https://www.bart.gov/news/articles/2025/news20250728
