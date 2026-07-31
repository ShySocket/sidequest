# Transport Motion Estimator

## Scope

The estimator is transport-agnostic at runtime: it contains no route, station,
car, bus, or train selection. Its primary tuning target is motorized ground
transport, especially cars, buses, subways, and trains.

GPS is satellite positioning and does not inherently require an internet
connection. A phone may still take longer to obtain a fix without assisted GPS,
and tunnels or urban buildings can block or distort the signal. Once the app
has an absolute speed reference, all fallback calculations run locally from the
phone's linear-acceleration and attitude sensors.

## Fusion Behavior

| Sensor situation | Estimator behavior |
|---|---|
| Healthy, accurate GPS | GPS remains the absolute-speed authority; acceleration supplies immediate changes between fixes |
| Weak GPS | Accuracy-weighted correction with single-fix outlier rejection |
| GPS stale or missing | Integrates longitudinal acceleration and otherwise holds constant velocity |
| GPS returns | Blends back to the corrected speed instead of jumping |
| GPS and motion sensor both stop | Slowly decays the held estimate and lowers confidence |

The accelerometer is transformed from phone coordinates into a stable
reference frame using device attitude. The forward direction is learned from
GPS speed changes. Reliable GPS course changes rotate that learned direction
through road turns. After a confirmed stop, a sustained departure can establish
a different direction, which covers turns at intersections and buses leaving a
stop on a curved route.

## Braking Behavior

Two independent profiles avoid tuning the entire system to one vehicle:

- A short hard-brake path covers cars and abrupt bus stops. It reports hard
  braking quickly but confirms a stop only if integrated deceleration explains
  nearly all of the entry speed and is followed by quiet motion.
- A long service-brake path covers buses, subways, and trains. It tolerates
  under-reported phone acceleration but still requires sustained deceleration,
  a large speed reduction, a small residual estimate, and a quiet end phase.

A partial emergency brake and a long slowdown to a rolling speed are explicitly
tested not to produce a false stop. Phone bumps and alternating handling motion
are also tested not to release the stationary lock.

## Automated Coverage

Unity 6000.3.20f1 results:

| Suite | Result |
|---|---:|
| EditMode | 51 passed, 0 failed |
| PlayMode | 5 passed, 0 failed |

The EditMode suite includes:

- a car hard stop during GPS loss;
- a partial car emergency brake that must remain moving;
- a long bus slowdown that must remain moving;
- a bus stop and restart after GPS has become completely missing;
- a 90-degree road turn followed by accelerometer-only speed change;
- alternating phone motion while stationary;
- the full Downtown Berkeley to Montgomery BART replay;
- weak GPS, multipath spikes, phone rotation, tunnel cruising, and GPS return.

The BART regression remains at 1.54 m/s mean absolute error, 3.11 m/s
95th-percentile error, 0.50 seconds maximum stop-confirmation latency, and zero
false station stops in its deterministic replay.

## Physical Limits

This improves generality; it cannot make phone-only inertial navigation
universally exact.

- An accelerometer measures change in velocity, not absolute constant speed.
  If the app starts inside a tunnel after the vehicle is already cruising,
  absolute speed is unknowable until GPS returns or a known start is observed.
- Integration accumulates sensor bias over long outages. Confidence therefore
  falls with GPS age even while the estimate continues smoothly.
- Elevators, aircraft, boats in heavy seas, walking, and cycling have motion
  signatures not yet represented by dedicated simulations. They are not
  intentionally special-cased, but should not be described as validated.
- GPS course is unreliable at very low speeds, so direction updates are ignored
  there.

Real passenger recordings remain necessary before release. Collect car and bus
traces with the phone held, pocketed, and mounted; include urban canyons,
tunnels, stop-and-go traffic, turns, gentle stops, and emergency braking
performed only in a safe controlled test. Never interact with the app while
driving.
