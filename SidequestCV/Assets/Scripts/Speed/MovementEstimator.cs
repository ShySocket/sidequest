using System;
using UnityEngine;

/// <summary>
/// Fuses scalar GPS speed with device linear acceleration.
///
/// GPS supplies the absolute-speed reference when it is available. The motion
/// sensor fills short GPS gaps and observes both abrupt road-vehicle braking
/// and longer rail braking. All computation is local; no network service is
/// required.
/// </summary>
public sealed class MovementEstimator
{
    private const float AxisLearningMinimumGpsDelta = 0.3f;
    private const float AxisLearningMinimumIntegratedAcceleration = 0.08f;
    private const float AxisLearningRate = 0.25f;
    private const float MinimumAxisCandidateSimilarity = 0.5f;
    private const float AccelerationSmoothingSeconds = 0.12f;
    private const float MinimumDeltaTime = 0.0001f;
    private const float MaximumMotionDeltaTime = 0.1f;
    private const float MinimumStartAcceleration = 0.55f;
    private const float StartMotionEvidenceSeconds = 0.18f;
    private const float GpsConfirmedStartEvidenceSeconds = 0.12f;
    private const float StartDirectionSimilarity = 0.72f;
    private const float DirectionChangeUnlockQuietSeconds = 0.3f;
    private const float MaximumStartCourseChangeDegrees = 45f;
    private const int MovingGpsSamplesRequired = 3;

    // Stop recognition has two evidence paths. Cars can shed most of their
    // speed in a short hard brake, while buses and trains generally produce a
    // longer, gentler profile. Neither path equates one acceleration spike
    // with a stop.
    private const float MinimumServiceBrakingThresholdMps2 = -0.35f;
    private const float MinimumLongBrakingSeconds = 2.5f;
    private const float MinimumLongBrakingSpeedDropMps = 1.5f;
    private const float MaximumLongStopResidualSpeedMps = 4f;
    private const float MinimumLongStopSpeedFraction = 0.5f;
    private const float MinimumShortBrakingSpeedDropMps = 1f;
    private const float MaximumShortStopResidualSpeedMps = 2f;
    private const float MaximumShortStopFractionResidualSpeedMps = 4f;
    private const float MinimumShortStopSpeedFraction = 0.82f;
    private const float QuietAfterBrakingSeconds = 0.35f;
    private const float BrakingResetSeconds = 1.5f;

    // The windowed GPS speed straddles a stop, so it keeps reporting residual
    // motion for several seconds after braking ends. During that window only
    // accelerometer evidence may restart the character.
    private const float GpsRestartSuppressionSeconds = 5f;

    private const float MinimumCourseUpdateSpeedMps = 2.5f;
    private const float MaximumCourseUpdateDegrees = 100f;
    private const float MaximumCourseAccuracyFraction = 0.6f;
    private const float MotionSensorStaleSeconds = 1f;
    private const float StationaryBiasLearningSeconds = 8f;
    private const float MaximumBiasLearningAccelerationMps2 = 0.35f;
    private const float MinimumGpsAccuracyReliability = 0.08f;
    private const float GpsOutlierBaseAllowanceMps = 3f;
    private const float GpsOutlierAccelerationAllowanceMps2 = 3.5f;

    private MovementEstimatorSettings settings;

    private bool hasGpsFix;
    private float lastGoodGpsSpeedMps;
    private float lastGpsAccuracyMeters;
    private double lastGoodGpsTimestamp = -1d;
    private double lastMotionTimestamp = -1d;
    private double lastUpdateTimestamp = -1d;
    private Vector3 smoothedReferenceAcceleration;
    private Vector3 stationaryAccelerationBias;
    private Vector3 integratedReferenceAcceleration;
    private Vector3 inferredForwardAxis;
    private Vector3 startMotionAxis;
    private float accelerometerDeltaSpeedMps;
    private float startMotionEvidenceDuration;
    private float stationaryQuietDuration;
    private int consecutiveMovingGpsSamples;
    private float candidateMovingCourseDegrees = float.NaN;
    private float lastReliableMovingCourseDegrees = float.NaN;
    private bool isStationary;
    private double stopHoldUntil = -1d;
    private double gpsRestartSuppressedUntil = -1d;

    private bool returnBlendActive;
    private double returnBlendStartedAt;
    private float returnBlendStartSpeed;
    private float returnBlendTargetSpeed;

    private bool isBraking;
    private float brakingDuration;
    private float brakingObservedSpeedDropMps;
    private float brakingEntrySpeedMps;
    private float brakingQuietDuration;
    private float peakBrakingDecelerationMps2;
    private bool isHardBraking;

    public MovementEstimator(MovementEstimatorSettings settings)
    {
        this.settings = settings;
        Reset();
    }

    public float EstimatedVehicleSpeedMps { get; private set; }
    public float LastGoodGpsSpeedMps => lastGoodGpsSpeedMps;
    public float AccelerometerDerivedSpeedMps =>
        Mathf.Clamp(
            lastGoodGpsSpeedMps + accelerometerDeltaSpeedMps,
            0f,
            settings.MaximumSpeedMetersPerSecond);
    public float ForwardAccelerationMps2 { get; private set; }
    public float Confidence { get; private set; }
    public GpsHealth GpsHealth { get; private set; }
    public bool IsDeadReckoning =>
        hasGpsFix && GpsHealth != global::GpsHealth.Healthy;
    public bool SuddenStopDetected { get; private set; }
    public bool HasGpsFix => hasGpsFix;
    public bool HasSpeedEstimate => hasGpsFix || !isStationary;
    public bool IsStationary => isStationary;
    public bool IsBraking => isBraking;
    public bool IsHardBraking => isHardBraking;
    public Vector3 InferredForwardAxis => inferredForwardAxis;
    public Quaternion LastDeviceAttitude { get; private set; } =
        Quaternion.identity;

    public void UpdateSettings(MovementEstimatorSettings updatedSettings)
    {
        settings = updatedSettings;
    }

    public void AddGpsSample(
        float speedMetersPerSecond,
        float horizontalAccuracyMeters,
        double localTimestamp,
        float courseDegrees = float.NaN)
    {
        if (!IsFinite(localTimestamp) || localTimestamp <= lastGoodGpsTimestamp)
        {
            return;
        }

        float measuredSpeed = Mathf.Clamp(
            speedMetersPerSecond,
            0f,
            settings.MaximumSpeedMetersPerSecond);
        float acceptedSpeed = measuredSpeed;
        bool firstGpsFix = !hasGpsFix;
        bool returningFromLoss = hasGpsFix
            && localTimestamp - lastGoodGpsTimestamp
                > settings.GpsStaleThresholdSeconds;
        bool motionConfirmedDeparture =
            lastGoodGpsSpeedMps
                <= settings.StationarySpeedThresholdMetersPerSecond
            && (!isStationary
                || startMotionEvidenceDuration
                    >= GpsConfirmedStartEvidenceSeconds);

        if (!firstGpsFix
            && isStationary
            && localTimestamp - lastGoodGpsTimestamp
                > settings.GpsStaleThresholdSeconds * 2f)
        {
            consecutiveMovingGpsSamples = 0;
            candidateMovingCourseDegrees = float.NaN;
        }

        if (firstGpsFix)
        {
            isStationary =
                measuredSpeed <= settings.StationarySpeedThresholdMetersPerSecond;
            consecutiveMovingGpsSamples = isStationary
                ? 0
                : MovingGpsSamplesRequired;
        }
        else if (measuredSpeed
            <= settings.StationarySpeedThresholdMetersPerSecond)
        {
            acceptedSpeed = 0f;
            bool wasMoving = !isStationary
                || EstimatedVehicleSpeedMps
                    > settings.StationarySpeedThresholdMetersPerSecond;
            ConfirmStationary(localTimestamp, wasMoving);
        }
        else if (isStationary)
        {
            bool motionConfirmsStart =
                startMotionEvidenceDuration
                    >= GpsConfirmedStartEvidenceSeconds;
            if (!motionConfirmsStart
                && localTimestamp < gpsRestartSuppressedUntil)
            {
                // Residual window speed from before the stop is not evidence
                // of a departure; a real one shows up on the accelerometer.
                consecutiveMovingGpsSamples = 0;
                candidateMovingCourseDegrees = float.NaN;
                acceptedSpeed = 0f;
            }
            else
            {
                bool courseIsConsistent = float.IsNaN(courseDegrees)
                    || float.IsNaN(candidateMovingCourseDegrees)
                    || Mathf.Abs(Mathf.DeltaAngle(
                        candidateMovingCourseDegrees,
                        courseDegrees)) <= MaximumStartCourseChangeDegrees;
                consecutiveMovingGpsSamples = courseIsConsistent
                    ? consecutiveMovingGpsSamples + 1
                    : 1;
                candidateMovingCourseDegrees = courseDegrees;
                if (motionConfirmsStart
                    || consecutiveMovingGpsSamples >= MovingGpsSamplesRequired)
                {
                    isStationary = false;
                }
                else
                {
                    // An isolated displacement is commonly GPS jitter at a
                    // station.
                    acceptedSpeed = 0f;
                }
            }
        }
        else
        {
            consecutiveMovingGpsSamples = MovingGpsSamplesRequired;
            candidateMovingCourseDegrees = courseDegrees;
        }

        if (!firstGpsFix
            && acceptedSpeed > 0f
            && !motionConfirmedDeparture
            && !IsPlausibleGpsSpeed(
                acceptedSpeed,
                horizontalAccuracyMeters,
                localTimestamp,
                returningFromLoss))
        {
            // Preserve the freshness timestamp but do not allow a single tunnel
            // multipath jump to move the character.
            acceptedSpeed = EstimatedVehicleSpeedMps;
        }

        UpdateForwardAxisFromCourse(
            acceptedSpeed,
            horizontalAccuracyMeters,
            courseDegrees);
        LearnForwardAxis(acceptedSpeed);
        lastGoodGpsSpeedMps = acceptedSpeed;
        lastGpsAccuracyMeters = Mathf.Max(0f, horizontalAccuracyMeters);
        lastGoodGpsTimestamp = localTimestamp;
        hasGpsFix = true;
        integratedReferenceAcceleration = Vector3.zero;

        if (firstGpsFix)
        {
            EstimatedVehicleSpeedMps = acceptedSpeed;
            returnBlendActive = false;
        }
        else if (acceptedSpeed <= 0f)
        {
            EstimatedVehicleSpeedMps = 0f;
            returnBlendActive = false;
        }
        else
        {
            float gpsWeight = EffectiveGpsWeight(horizontalAccuracyMeters);
            float correctedSpeed = Mathf.Lerp(
                EstimatedVehicleSpeedMps,
                acceptedSpeed,
                gpsWeight);
            if (returningFromLoss && settings.GpsReturnBlendSeconds > 0f)
            {
                returnBlendActive = true;
                returnBlendStartedAt = localTimestamp;
                returnBlendStartSpeed = EstimatedVehicleSpeedMps;
                returnBlendTargetSpeed = correctedSpeed;
            }
            else
            {
                returnBlendActive = false;
                EstimatedVehicleSpeedMps = correctedSpeed;
            }
        }

        accelerometerDeltaSpeedMps =
            EstimatedVehicleSpeedMps - lastGoodGpsSpeedMps;
    }

    public void AddMotionSample(DeviceMotionReading reading)
    {
        double timestamp = reading.LocalTimestamp;
        if (!IsFinite(timestamp) || timestamp <= lastMotionTimestamp)
        {
            return;
        }

        LastDeviceAttitude = reading.Attitude;
        Vector3 referenceAcceleration = ToReferenceFrame(reading);
        if (lastMotionTimestamp < 0d)
        {
            lastMotionTimestamp = timestamp;
            smoothedReferenceAcceleration = referenceAcceleration;
            return;
        }

        float deltaTime = Mathf.Clamp(
            (float)(timestamp - lastMotionTimestamp),
            MinimumDeltaTime,
            MaximumMotionDeltaTime);
        lastMotionTimestamp = timestamp;

        referenceAcceleration = Vector3.ClampMagnitude(
            referenceAcceleration,
            settings.MaximumAccelerationMetersPerSecondSquared);
        LearnStationaryBias(referenceAcceleration, deltaTime);
        Vector3 unbiasedAcceleration =
            referenceAcceleration - stationaryAccelerationBias;
        float smoothingAlpha = 1f - (float)Math.Exp(
            -deltaTime / AccelerationSmoothingSeconds);
        smoothedReferenceAcceleration = Vector3.Lerp(
            smoothedReferenceAcceleration,
            unbiasedAcceleration,
            smoothingAlpha);
        integratedReferenceAcceleration +=
            smoothedReferenceAcceleration * deltaTime;
        UpdateStationaryQuietEvidence(unbiasedAcceleration, deltaTime);
        if (!isStationary || timestamp >= stopHoldUntil)
        {
            UpdateStartMotionEvidence(unbiasedAcceleration, deltaTime);
        }
        else
        {
            ResetStartMotionEvidence();
        }

        if (inferredForwardAxis.sqrMagnitude < 0.5f
            && startMotionEvidenceDuration
                >= StartMotionEvidenceSeconds * 0.5f)
        {
            inferredForwardAxis = startMotionAxis;
        }

        bool releasedStationaryThisSample = false;
        if (isStationary
            && timestamp >= stopHoldUntil
            && startMotionEvidenceDuration >= StartMotionEvidenceSeconds)
        {
            isStationary = false;
            releasedStationaryThisSample = true;
            inferredForwardAxis = startMotionAxis.normalized;
            ResetBrakingEvidence();
        }

        float projectedAcceleration =
            inferredForwardAxis.sqrMagnitude >= 0.5f
            ? Vector3.Dot(
                smoothedReferenceAcceleration,
                inferredForwardAxis)
            : 0f;
        ForwardAccelerationMps2 = ApplyDeadZone(projectedAcceleration);

        if (!isStationary || releasedStationaryThisSample)
        {
            EstimatedVehicleSpeedMps = Mathf.Clamp(
                EstimatedVehicleSpeedMps
                    + ForwardAccelerationMps2 * deltaTime,
                0f,
                settings.MaximumSpeedMetersPerSecond);
        }

        UpdateBrakingEvidence(ForwardAccelerationMps2, deltaTime, timestamp);
        accelerometerDeltaSpeedMps =
            EstimatedVehicleSpeedMps - lastGoodGpsSpeedMps;
    }

    public float Update(double localTimestamp)
    {
        float updateDeltaTime = lastUpdateTimestamp < 0d
            ? 0f
            : Mathf.Max(0f, (float)(localTimestamp - lastUpdateTimestamp));
        lastUpdateTimestamp = localTimestamp;

        UpdateGpsHealth(localTimestamp);
        SuddenStopDetected = localTimestamp < stopHoldUntil;

        if (!hasGpsFix)
        {
            // Acceleration cannot reveal absolute velocity if the app starts
            // during constant-speed travel. It can still detect a departure
            // from a known stationary start, but confidence remains low.
            Confidence = isStationary ? 0f : 0.15f;
            return EstimatedVehicleSpeedMps;
        }

        if (isStationary)
        {
            EstimatedVehicleSpeedMps = 0f;
            UpdateConfidence(localTimestamp);
            return 0f;
        }

        if (returnBlendActive)
        {
            float progress = settings.GpsReturnBlendSeconds <= 0f
                ? 1f
                : Mathf.Clamp01(
                    (float)(localTimestamp - returnBlendStartedAt)
                    / settings.GpsReturnBlendSeconds);
            EstimatedVehicleSpeedMps = Mathf.Lerp(
                returnBlendStartSpeed,
                returnBlendTargetSpeed,
                progress);
            if (progress >= 1f)
            {
                returnBlendActive = false;
            }
        }

        bool motionSensorMissing = lastMotionTimestamp < 0d
            || localTimestamp - lastMotionTimestamp > MotionSensorStaleSeconds;
        if (GpsHealth == global::GpsHealth.Missing
            && motionSensorMissing
            && updateDeltaTime > 0f)
        {
            EstimatedVehicleSpeedMps *= Mathf.Pow(
                settings.MissingSpeedRetentionPerSecond,
                updateDeltaTime);
        }

        EstimatedVehicleSpeedMps = Mathf.Clamp(
            EstimatedVehicleSpeedMps,
            0f,
            settings.MaximumSpeedMetersPerSecond);
        accelerometerDeltaSpeedMps =
            EstimatedVehicleSpeedMps - lastGoodGpsSpeedMps;
        UpdateConfidence(localTimestamp);
        return EstimatedVehicleSpeedMps;
    }

    public void Reset()
    {
        hasGpsFix = false;
        lastGoodGpsSpeedMps = 0f;
        lastGpsAccuracyMeters = 0f;
        lastGoodGpsTimestamp = -1d;
        lastMotionTimestamp = -1d;
        lastUpdateTimestamp = -1d;
        smoothedReferenceAcceleration = Vector3.zero;
        stationaryAccelerationBias = Vector3.zero;
        integratedReferenceAcceleration = Vector3.zero;
        inferredForwardAxis = Vector3.zero;
        startMotionAxis = Vector3.zero;
        accelerometerDeltaSpeedMps = 0f;
        startMotionEvidenceDuration = 0f;
        stationaryQuietDuration = DirectionChangeUnlockQuietSeconds;
        consecutiveMovingGpsSamples = 0;
        candidateMovingCourseDegrees = float.NaN;
        lastReliableMovingCourseDegrees = float.NaN;
        isStationary = true;
        stopHoldUntil = -1d;
        gpsRestartSuppressedUntil = -1d;
        returnBlendActive = false;
        EstimatedVehicleSpeedMps = 0f;
        ForwardAccelerationMps2 = 0f;
        Confidence = 0f;
        GpsHealth = global::GpsHealth.Missing;
        SuddenStopDetected = false;
        LastDeviceAttitude = Quaternion.identity;
        ResetBrakingEvidence();
    }

    private Vector3 ToReferenceFrame(DeviceMotionReading reading)
    {
        Quaternion attitude = reading.Attitude;
        float normSquared = attitude.x * attitude.x
            + attitude.y * attitude.y
            + attitude.z * attitude.z
            + attitude.w * attitude.w;
        if (normSquared < 0.5f
            || float.IsNaN(normSquared)
            || float.IsInfinity(normSquared))
        {
            return reading.UserAccelerationMetersPerSecondSquared;
        }

        return attitude.normalized
            * reading.UserAccelerationMetersPerSecondSquared;
    }

    private void LearnStationaryBias(Vector3 acceleration, float deltaTime)
    {
        if (!isStationary
            || acceleration.magnitude > MaximumBiasLearningAccelerationMps2)
        {
            return;
        }

        float alpha = 1f - (float)Math.Exp(
            -deltaTime / StationaryBiasLearningSeconds);
        stationaryAccelerationBias = Vector3.Lerp(
            stationaryAccelerationBias,
            acceleration,
            alpha);
    }

    private void LearnForwardAxis(float newGpsSpeed)
    {
        if (lastGoodGpsTimestamp < 0d)
        {
            return;
        }

        float gpsSpeedDelta = newGpsSpeed - lastGoodGpsSpeedMps;
        if (Mathf.Abs(gpsSpeedDelta) < AxisLearningMinimumGpsDelta
            || integratedReferenceAcceleration.magnitude
                < AxisLearningMinimumIntegratedAcceleration)
        {
            return;
        }

        Vector3 candidate = integratedReferenceAcceleration.normalized
            * Mathf.Sign(gpsSpeedDelta);
        if (inferredForwardAxis.sqrMagnitude >= 0.5f
            && Vector3.Dot(inferredForwardAxis, candidate)
                < MinimumAxisCandidateSimilarity)
        {
            // A weak GPS delta during quiet cruise can correlate with phone
            // handling or vibration. Course changes and departures from a
            // confirmed stop update the axis through stronger evidence paths.
            return;
        }

        inferredForwardAxis = inferredForwardAxis.sqrMagnitude < 0.5f
            ? candidate
            : Vector3.Slerp(
                inferredForwardAxis,
                candidate,
                AxisLearningRate).normalized;
    }

    private void UpdateBrakingEvidence(
        float forwardAcceleration,
        float deltaTime,
        double timestamp)
    {
        if (isStationary)
        {
            ResetBrakingEvidence();
            return;
        }

        float brakingThreshold = Mathf.Min(
            MinimumServiceBrakingThresholdMps2,
            settings.HardBrakingThresholdMetersPerSecondSquared * 0.25f);
        if (forwardAcceleration <= brakingThreshold)
        {
            if (brakingDuration <= 0f)
            {
                brakingEntrySpeedMps = EstimatedVehicleSpeedMps;
            }

            brakingDuration += deltaTime;
            brakingObservedSpeedDropMps +=
                -forwardAcceleration * deltaTime;
            peakBrakingDecelerationMps2 = Mathf.Max(
                peakBrakingDecelerationMps2,
                -forwardAcceleration);
            brakingQuietDuration = 0f;
            isBraking =
                brakingDuration >= settings.HardBrakingDurationSeconds;
            isHardBraking = isBraking
                && peakBrakingDecelerationMps2
                    >= -settings.HardBrakingThresholdMetersPerSecondSquared;

            if (EstimatedVehicleSpeedMps
                <= settings.StationarySpeedThresholdMetersPerSecond
                && HasMinimumStopBrakingEvidence())
            {
                ConfirmStationary(timestamp, true);
            }

            return;
        }

        if (brakingDuration <= 0f)
        {
            isBraking = false;
            return;
        }

        if (Mathf.Abs(forwardAcceleration)
            <= settings.AccelerationNoiseDeadZoneMetersPerSecondSquared)
        {
            brakingQuietDuration += deltaTime;
            if (brakingQuietDuration >= QuietAfterBrakingSeconds
                && BrakingProfileIndicatesStop())
            {
                ConfirmStationary(timestamp, true);
                return;
            }

            if (brakingQuietDuration >= BrakingResetSeconds)
            {
                ResetBrakingEvidence();
            }

            return;
        }

        if (forwardAcceleration > 0f)
        {
            ResetBrakingEvidence();
        }
    }

    private bool BrakingProfileIndicatesStop()
    {
        if (!HasMinimumStopBrakingEvidence())
        {
            return false;
        }

        float fractionOfEntrySpeed = brakingEntrySpeedMps <= 0.1f
            ? 1f
            : brakingObservedSpeedDropMps / brakingEntrySpeedMps;
        bool shortHardStop =
            peakBrakingDecelerationMps2
                >= -settings.HardBrakingThresholdMetersPerSecondSquared
            && brakingDuration >= settings.HardBrakingDurationSeconds
            && brakingObservedSpeedDropMps
                >= MinimumShortBrakingSpeedDropMps
            && (EstimatedVehicleSpeedMps
                    <= MaximumShortStopResidualSpeedMps
                || (EstimatedVehicleSpeedMps
                        <= MaximumShortStopFractionResidualSpeedMps
                    && fractionOfEntrySpeed
                        >= MinimumShortStopSpeedFraction));
        bool longServiceStop =
            brakingDuration >= MinimumLongBrakingSeconds
            && brakingObservedSpeedDropMps
                >= MinimumLongBrakingSpeedDropMps
            && EstimatedVehicleSpeedMps
                <= MaximumLongStopResidualSpeedMps
            && fractionOfEntrySpeed
                >= MinimumLongStopSpeedFraction;
        return shortHardStop || longServiceStop;
    }

    private bool HasMinimumStopBrakingEvidence()
    {
        bool hardEvidence =
            peakBrakingDecelerationMps2
                >= -settings.HardBrakingThresholdMetersPerSecondSquared
            && brakingDuration >= settings.HardBrakingDurationSeconds
            && brakingObservedSpeedDropMps
                >= MinimumShortBrakingSpeedDropMps;
        bool longEvidence =
            brakingDuration >= MinimumLongBrakingSeconds
            && brakingObservedSpeedDropMps
                >= MinimumLongBrakingSpeedDropMps;
        return hardEvidence || longEvidence;
    }

    private void UpdateStartMotionEvidence(
        Vector3 acceleration,
        float deltaTime)
    {
        float threshold = Mathf.Max(
            MinimumStartAcceleration,
            settings.AccelerationNoiseDeadZoneMetersPerSecondSquared * 2.5f);
        if (acceleration.magnitude < threshold)
        {
            DecayStartMotionEvidence(deltaTime);
            return;
        }

        Vector3 candidateAxis = acceleration.normalized;
        if (isStationary
            && inferredForwardAxis.sqrMagnitude >= 0.5f
            && stationaryQuietDuration < DirectionChangeUnlockQuietSeconds
            && Vector3.Dot(candidateAxis, inferredForwardAxis)
                < StartDirectionSimilarity)
        {
            ResetStartMotionEvidence();
            return;
        }

        if (startMotionAxis.sqrMagnitude < 0.5f
            || Vector3.Dot(startMotionAxis, candidateAxis)
                < StartDirectionSimilarity)
        {
            startMotionAxis = candidateAxis;
            startMotionEvidenceDuration = deltaTime;
            return;
        }

        startMotionAxis = Vector3.Slerp(
            startMotionAxis,
            candidateAxis,
            0.2f).normalized;
        startMotionEvidenceDuration += deltaTime;
    }

    private void DecayStartMotionEvidence(float deltaTime)
    {
        startMotionEvidenceDuration = Mathf.Max(
            0f,
            startMotionEvidenceDuration - deltaTime * 2f);
        if (startMotionEvidenceDuration <= 0f)
        {
            startMotionAxis = Vector3.zero;
        }
    }

    private void ResetStartMotionEvidence()
    {
        startMotionEvidenceDuration = 0f;
        startMotionAxis = Vector3.zero;
    }

    private void UpdateStationaryQuietEvidence(
        Vector3 acceleration,
        float deltaTime)
    {
        if (!isStationary)
        {
            stationaryQuietDuration = 0f;
            return;
        }

        float quietThreshold = Mathf.Max(
            MinimumStartAcceleration,
            settings.AccelerationNoiseDeadZoneMetersPerSecondSquared * 2.5f);
        if (acceleration.magnitude <= quietThreshold)
        {
            stationaryQuietDuration += deltaTime;
        }
    }

    private void ConfirmStationary(double timestamp, bool emitStopEvent)
    {
        isStationary = true;
        consecutiveMovingGpsSamples = 0;
        candidateMovingCourseDegrees = float.NaN;
        lastReliableMovingCourseDegrees = float.NaN;
        ResetStartMotionEvidence();
        stationaryQuietDuration = 0f;
        EstimatedVehicleSpeedMps = 0f;
        accelerometerDeltaSpeedMps = -lastGoodGpsSpeedMps;
        if (emitStopEvent)
        {
            stopHoldUntil = Math.Max(
                stopHoldUntil,
                timestamp + settings.StopHoldSeconds);
            gpsRestartSuppressedUntil = Math.Max(
                gpsRestartSuppressedUntil,
                timestamp + GpsRestartSuppressionSeconds);
        }

        ResetBrakingEvidence();
    }

    private void ResetBrakingEvidence()
    {
        isBraking = false;
        brakingDuration = 0f;
        brakingObservedSpeedDropMps = 0f;
        brakingEntrySpeedMps = 0f;
        brakingQuietDuration = 0f;
        peakBrakingDecelerationMps2 = 0f;
        isHardBraking = false;
    }

    private void UpdateForwardAxisFromCourse(
        float speedMetersPerSecond,
        float horizontalAccuracyMeters,
        float courseDegrees)
    {
        if (float.IsNaN(courseDegrees)
            || float.IsInfinity(courseDegrees)
            || speedMetersPerSecond < MinimumCourseUpdateSpeedMps
            || horizontalAccuracyMeters
                > settings.MaximumGpsAccuracyMeters
                    * MaximumCourseAccuracyFraction)
        {
            return;
        }

        if (!float.IsNaN(lastReliableMovingCourseDegrees)
            && inferredForwardAxis.sqrMagnitude >= 0.5f)
        {
            float courseChange = Mathf.DeltaAngle(
                lastReliableMovingCourseDegrees,
                courseDegrees);
            if (Mathf.Abs(courseChange) <= MaximumCourseUpdateDegrees)
            {
                inferredForwardAxis = (
                    Quaternion.AngleAxis(courseChange, Vector3.up)
                    * inferredForwardAxis).normalized;
            }
        }

        lastReliableMovingCourseDegrees = courseDegrees;
    }

    private bool IsPlausibleGpsSpeed(
        float speed,
        float horizontalAccuracyMeters,
        double timestamp,
        bool returningFromLoss)
    {
        if (returningFromLoss)
        {
            return true;
        }

        float elapsed = Mathf.Max(
            0.02f,
            (float)(timestamp - lastGoodGpsTimestamp));
        float accuracyAllowance = Mathf.Clamp(
            horizontalAccuracyMeters * 0.1f,
            0f,
            7.5f);
        float allowance = GpsOutlierBaseAllowanceMps
            + GpsOutlierAccelerationAllowanceMps2 * elapsed
            + accuracyAllowance;
        return Mathf.Abs(speed - EstimatedVehicleSpeedMps) <= allowance;
    }

    private float EffectiveGpsWeight(float horizontalAccuracyMeters)
    {
        float maximumAccuracy = Mathf.Max(
            1f,
            settings.MaximumGpsAccuracyMeters);
        float normalized = Mathf.Clamp01(
            horizontalAccuracyMeters / maximumAccuracy);
        float reliability = Mathf.Lerp(
            1f,
            MinimumGpsAccuracyReliability,
            normalized * normalized);
        return Mathf.Clamp01(settings.GpsWeight * reliability);
    }

    private float ApplyDeadZone(float acceleration)
    {
        float magnitude = Mathf.Abs(acceleration);
        if (magnitude
            <= settings.AccelerationNoiseDeadZoneMetersPerSecondSquared)
        {
            return 0f;
        }

        return Mathf.Clamp(
            acceleration,
            -settings.MaximumAccelerationMetersPerSecondSquared,
            settings.MaximumAccelerationMetersPerSecondSquared);
    }

    private void UpdateGpsHealth(double localTimestamp)
    {
        if (!hasGpsFix)
        {
            GpsHealth = global::GpsHealth.Missing;
            return;
        }

        double age = Math.Max(0d, localTimestamp - lastGoodGpsTimestamp);
        if (age <= settings.GpsStaleThresholdSeconds)
        {
            GpsHealth = global::GpsHealth.Healthy;
        }
        else if (age <= settings.GpsMissingThresholdSeconds)
        {
            GpsHealth = global::GpsHealth.Stale;
        }
        else
        {
            GpsHealth = global::GpsHealth.Missing;
        }
    }

    private void UpdateConfidence(double localTimestamp)
    {
        float normalizedAccuracy = Mathf.Clamp01(
            lastGpsAccuracyMeters
            / Mathf.Max(1f, settings.MaximumGpsAccuracyMeters));
        float accuracyConfidence = Mathf.Lerp(
            1f,
            0.2f,
            normalizedAccuracy * normalizedAccuracy);
        if (GpsHealth == global::GpsHealth.Healthy)
        {
            Confidence = accuracyConfidence;
            return;
        }

        float age = Mathf.Max(
            0f,
            (float)(localTimestamp - lastGoodGpsTimestamp));
        if (GpsHealth == global::GpsHealth.Stale)
        {
            float staleProgress = Mathf.InverseLerp(
                settings.GpsStaleThresholdSeconds,
                settings.GpsMissingThresholdSeconds,
                age);
            Confidence = Mathf.Lerp(
                accuracyConfidence * 0.8f,
                0.3f,
                staleProgress);
            return;
        }

        float missingSeconds =
            age - settings.GpsMissingThresholdSeconds;
        Confidence = Mathf.Max(
            0.05f,
            0.3f * Mathf.Exp(-missingSeconds / 45f));
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
