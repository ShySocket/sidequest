import AVFoundation
import Foundation
import QuartzCore
import SwiftUI
import UIKit

/// Owns the AVFoundation session and the lifecycle of one recording session:
/// a folder under Documents/Sessions/ containing video.mov, sensors.jsonl and
/// session.json. The video start and every sensor sample share one monotonic
/// clock (CACurrentMediaTime), recorded in session.json for ingest alignment.
@MainActor
final class CaptureController: NSObject, ObservableObject {
    enum State: Equatable { case idle, recording, saving }

    @Published private(set) var state: State = .idle
    @Published private(set) var elapsed: TimeInterval = 0
    @Published private(set) var lastSavedSession: String?
    @Published private(set) var setupError: String?

    let captureSession = AVCaptureSession()
    let sensors = SensorRecorder()

    private let movieOutput = AVCaptureMovieFileOutput()
    private let sessionQueue = DispatchQueue(label: "sidequest.capture.session")
    private var sessionDir: URL?
    private var videoStartHostTime: Double = 0
    private var rotationAngleAtStart: CGFloat = 90
    private var recordStartDate = Date()
    private var elapsedTimer: Timer?
    private var configured = false

    func onAppear() {
        sensors.requestPermissionsAndWarmUp()
        Task { await configureIfNeeded() }
    }

    // MARK: - Session setup

    private func configureIfNeeded() async {
        guard !configured else { return }

        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined:
            let granted = await AVCaptureDevice.requestAccess(for: .video)
            guard granted else {
                setupError = "Camera access denied. Enable it in Settings > SidequestCapture."
                return
            }
        case .denied, .restricted:
            setupError = "Camera access denied. Enable it in Settings > SidequestCapture."
            return
        default:
            break
        }

        configured = true
        let session = captureSession
        let output = movieOutput
        sessionQueue.async {
            session.beginConfiguration()
            if session.canSetSessionPreset(.hd1920x1080) {
                session.sessionPreset = .hd1920x1080
            }
            guard
                let device = AVCaptureDevice.default(
                    .builtInWideAngleCamera, for: .video, position: .back),
                let input = try? AVCaptureDeviceInput(device: device),
                session.canAddInput(input)
            else {
                session.commitConfiguration()
                Task { @MainActor in
                    self.setupError = "No back camera available."
                }
                return
            }
            session.addInput(input)
            if session.canAddOutput(output) {
                session.addOutput(output)
            }
            session.commitConfiguration()

            // Lock 30 fps so every clip matches the analyzer's expectations.
            do {
                try device.lockForConfiguration()
                let thirty = CMTime(value: 1, timescale: 30)
                let supports30 = device.activeFormat.videoSupportedFrameRateRanges
                    .contains { $0.minFrameRate <= 30 && 30 <= $0.maxFrameRate }
                if supports30 {
                    device.activeVideoMinFrameDuration = thirty
                    device.activeVideoMaxFrameDuration = thirty
                }
                device.unlockForConfiguration()
            } catch {
                // Non-fatal: variable frame rate still records timestamps.
            }

            // Keep raw camera geometry: stabilization warps edges the CV
            // pipeline (ledges, markers, flow) reasons about.
            if let connection = output.connection(with: .video),
                connection.isVideoStabilizationSupported {
                connection.preferredVideoStabilizationMode = .off
            }

            session.startRunning()
        }
    }

    // MARK: - Recording

    func toggleRecording() {
        switch state {
        case .idle: startRecording()
        case .recording: stopRecording()
        case .saving: break
        }
    }

    private func startRecording() {
        guard captureSession.isRunning else { return }

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        let name = formatter.string(from: Date())
        let docs = FileManager.default.urls(
            for: .documentDirectory, in: .userDomainMask)[0]
        let dir = docs.appendingPathComponent("Sessions/\(name)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(
                at: dir, withIntermediateDirectories: true)
        } catch {
            setupError = "Could not create session folder: \(error.localizedDescription)"
            return
        }

        sessionDir = dir
        recordStartDate = Date()
        lastSavedSession = nil
        sensors.beginLogging(to: dir.appendingPathComponent("sensors.jsonl"))

        // Fix the recording orientation for the whole clip.
        let angle = VideoRotation.currentInterfaceAngle()
        rotationAngleAtStart = angle
        if let connection = movieOutput.connection(with: .video),
            connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }

        movieOutput.startRecording(
            to: dir.appendingPathComponent("video.mov"),
            recordingDelegate: self)

        state = .recording
        elapsed = 0
        UIApplication.shared.isIdleTimerDisabled = true
        elapsedTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) {
            [weak self] _ in
            Task { @MainActor in
                guard let self, self.state == .recording else { return }
                self.elapsed = Date().timeIntervalSince(self.recordStartDate)
            }
        }
    }

    private func stopRecording() {
        state = .saving
        movieOutput.stopRecording()
        elapsedTimer?.invalidate()
        elapsedTimer = nil
        UIApplication.shared.isIdleTimerDisabled = false
    }

    private func finishSession(stopHostTime: Double, error: Error?) {
        sensors.endLogging()
        guard let dir = sessionDir else { return }

        let metadata: [String: Any] = [
            "schema": 1,
            "app": "SidequestCapture",
            "device": UIDevice.current.model,
            "systemVersion": UIDevice.current.systemVersion,
            "startedAt": ISO8601DateFormatter().string(from: recordStartDate),
            "videoFile": "video.mov",
            "sensorsFile": "sensors.jsonl",
            "clock": [
                // Both on the CACurrentMediaTime clock, same as every `t`
                // in sensors.jsonl: t_video = t - videoStartHostTime.
                "videoStartHostTime": videoStartHostTime,
                "stopHostTime": stopHostTime,
            ],
            "video": [
                "preset": "1920x1080",
                "requestedFps": 30,
                "rotationAngleAtStart": Double(rotationAngleAtStart),
                "stabilization": "off",
            ],
            "units": [
                "gps.speed": "m/s (-1 invalid)",
                "motion.ua": "g (userAcceleration)",
                "motion.gv": "g (gravity)",
                "motion.rr": "rad/s",
            ],
            "recordingError": error.map { $0.localizedDescription } ?? NSNull(),
        ]
        if let data = try? JSONSerialization.data(
            withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: dir.appendingPathComponent("session.json"))
        }

        lastSavedSession = dir.lastPathComponent
        sessionDir = nil
        state = .idle
    }
}

// MARK: - AVCaptureFileOutputRecordingDelegate

extension CaptureController: AVCaptureFileOutputRecordingDelegate {
    nonisolated func fileOutput(
        _ output: AVCaptureFileOutput,
        didStartRecordingTo fileURL: URL,
        from connections: [AVCaptureConnection]
    ) {
        // First frame is on disk; stamp video t=0 on the shared clock.
        let t = CACurrentMediaTime()
        Task { @MainActor in
            self.videoStartHostTime = t
        }
    }

    nonisolated func fileOutput(
        _ output: AVCaptureFileOutput,
        didFinishRecordingTo outputFileURL: URL,
        from connections: [AVCaptureConnection],
        error: Error?
    ) {
        let t = CACurrentMediaTime()
        Task { @MainActor in
            self.finishSession(stopHostTime: t, error: error)
        }
    }
}
