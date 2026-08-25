import CoreLocation
import CoreMotion
import Foundation
import QuartzCore

/// Logs GPS fixes and device motion to a JSONL file. Every line carries `t`,
/// the monotonic host clock (CACurrentMediaTime) — the same clock
/// CaptureController stamps the video start with, so ingest can align
/// sensor samples to video time exactly.
///
/// Units: gps.speed m/s (-1 = invalid), lat/lon degrees, alt m, hacc/vacc m;
/// motion.ua* userAcceleration in g, gv* gravity in g, rr* rotation rad/s,
/// q* attitude quaternion.
final class SensorRecorder: NSObject, ObservableObject, CLLocationManagerDelegate {
    @Published private(set) var lastSpeedMps: Double = -1
    @Published private(set) var horizontalAccuracy: Double = -1
    @Published private(set) var hasFix = false
    @Published private(set) var gpsCount = 0
    @Published private(set) var motionCount = 0
    @Published private(set) var locationDenied = false

    private let locationManager = CLLocationManager()
    private let motionManager = CMMotionManager()
    private let motionQueue: OperationQueue = {
        let q = OperationQueue()
        q.maxConcurrentOperationCount = 1
        q.name = "sidequest.sensors.motion"
        return q
    }()

    private let writeQueue = DispatchQueue(label: "sidequest.sensors.write")
    private var fileHandle: FileHandle?
    private var buffer = Data()
    private var logging = false
    private var motionSamplesSinceUIUpdate = 0

    // MARK: - Lifecycle

    /// Called once at app start: ask for location permission and start GPS
    /// immediately so the fix is warm before the first recording.
    func requestPermissionsAndWarmUp() {
        locationManager.delegate = self
        locationManager.desiredAccuracy = kCLLocationAccuracyBestForNavigation
        locationManager.distanceFilter = kCLDistanceFilterNone
        locationManager.requestWhenInUseAuthorization()
        locationManager.startUpdatingLocation()
    }

    func beginLogging(to url: URL) {
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try? FileHandle(forWritingTo: url)
        let startHost = CACurrentMediaTime()
        let startUnix = Date().timeIntervalSince1970
        writeQueue.sync {
            fileHandle = handle
            buffer.removeAll()
            logging = true
        }
        appendLine(String(
            format: "{\"type\":\"begin\",\"t\":%.6f,\"unix\":%.3f}",
            startHost, startUnix))
        gpsCount = 0
        motionCount = 0

        motionManager.deviceMotionUpdateInterval = 1.0 / 100.0
        motionManager.startDeviceMotionUpdates(
            using: .xArbitraryZVertical, to: motionQueue
        ) { [weak self] motion, _ in
            guard let self, let m = motion else { return }
            let t = CACurrentMediaTime()
            self.appendLine(String(
                format: "{\"type\":\"motion\",\"t\":%.6f,"
                    + "\"uax\":%.5f,\"uay\":%.5f,\"uaz\":%.5f,"
                    + "\"gvx\":%.5f,\"gvy\":%.5f,\"gvz\":%.5f,"
                    + "\"rrx\":%.5f,\"rry\":%.5f,\"rrz\":%.5f,"
                    + "\"qw\":%.5f,\"qx\":%.5f,\"qy\":%.5f,\"qz\":%.5f}",
                t,
                m.userAcceleration.x, m.userAcceleration.y, m.userAcceleration.z,
                m.gravity.x, m.gravity.y, m.gravity.z,
                m.rotationRate.x, m.rotationRate.y, m.rotationRate.z,
                m.attitude.quaternion.w, m.attitude.quaternion.x,
                m.attitude.quaternion.y, m.attitude.quaternion.z))
            // Publishing at 100 Hz would hammer SwiftUI; update ~1 Hz.
            DispatchQueue.main.async {
                self.motionSamplesSinceUIUpdate += 1
                if self.motionSamplesSinceUIUpdate >= 100 {
                    self.motionCount += self.motionSamplesSinceUIUpdate
                    self.motionSamplesSinceUIUpdate = 0
                }
            }
        }
    }

    /// Stops motion updates and flushes/closes the log. GPS keeps running so
    /// the HUD stays live between recordings.
    func endLogging() {
        motionManager.stopDeviceMotionUpdates()
        appendLine(String(
            format: "{\"type\":\"end\",\"t\":%.6f,\"unix\":%.3f}",
            CACurrentMediaTime(), Date().timeIntervalSince1970))
        writeQueue.sync {
            logging = false
            if !buffer.isEmpty {
                fileHandle?.write(buffer)
                buffer.removeAll()
            }
            try? fileHandle?.close()
            fileHandle = nil
        }
        DispatchQueue.main.async {
            self.motionCount += self.motionSamplesSinceUIUpdate
            self.motionSamplesSinceUIUpdate = 0
        }
    }

    // MARK: - CLLocationManagerDelegate

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        switch manager.authorizationStatus {
        case .denied, .restricted:
            locationDenied = true
        case .authorizedWhenInUse, .authorizedAlways:
            locationDenied = false
            manager.startUpdatingLocation()
        default:
            break
        }
    }

    func locationManager(
        _ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]
    ) {
        let t = CACurrentMediaTime()
        for loc in locations {
            lastSpeedMps = loc.speed
            horizontalAccuracy = loc.horizontalAccuracy
            hasFix = loc.horizontalAccuracy > 0 && loc.horizontalAccuracy < 50
            guard isLoggingNow() else { continue }
            appendLine(String(
                format: "{\"type\":\"gps\",\"t\":%.6f,"
                    + "\"lat\":%.7f,\"lon\":%.7f,\"alt\":%.2f,"
                    + "\"speed\":%.3f,\"course\":%.2f,"
                    + "\"hacc\":%.2f,\"vacc\":%.2f,\"unix\":%.3f}",
                t,
                loc.coordinate.latitude, loc.coordinate.longitude, loc.altitude,
                loc.speed, loc.course,
                loc.horizontalAccuracy, loc.verticalAccuracy,
                loc.timestamp.timeIntervalSince1970))
            gpsCount += 1
        }
    }

    func locationManager(
        _ manager: CLLocationManager, didFailWithError error: Error
    ) {
        // Transient (e.g. kCLErrorLocationUnknown in tunnels); keep going.
    }

    // MARK: - JSONL writing

    private func isLoggingNow() -> Bool {
        writeQueue.sync { logging }
    }

    private func appendLine(_ line: String) {
        guard let data = (line + "\n").data(using: .utf8) else { return }
        writeQueue.async { [weak self] in
            guard let self, self.logging || self.fileHandle != nil else { return }
            self.buffer.append(data)
            if self.buffer.count > 16 * 1024 {
                self.fileHandle?.write(self.buffer)
                self.buffer.removeAll(keepingCapacity: true)
            }
        }
    }
}
