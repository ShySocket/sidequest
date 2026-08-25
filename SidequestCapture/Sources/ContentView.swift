import SwiftUI

struct ContentView: View {
    @StateObject private var capture = CaptureController()

    var body: some View {
        ZStack {
            CameraPreviewView(session: capture.captureSession)
                .ignoresSafeArea()

            VStack {
                SensorHUD(sensors: capture.sensors, state: capture.state,
                          elapsed: capture.elapsed)
                Spacer()
                if let error = capture.setupError {
                    Text(error)
                        .font(.footnote)
                        .foregroundStyle(.white)
                        .padding(10)
                        .background(.red.opacity(0.8), in: RoundedRectangle(cornerRadius: 10))
                        .padding(.bottom, 8)
                }
                if let saved = capture.lastSavedSession {
                    Text("Saved \(saved) — Files ▸ SidequestCapture ▸ Sessions")
                        .font(.footnote)
                        .foregroundStyle(.white)
                        .padding(10)
                        .background(.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 10))
                        .padding(.bottom, 8)
                }
                RecordButton(state: capture.state) {
                    capture.toggleRecording()
                }
                .padding(.bottom, 30)
            }
        }
        .statusBarHidden()
        .onAppear { capture.onAppear() }
    }
}

private struct SensorHUD: View {
    @ObservedObject var sensors: SensorRecorder
    let state: CaptureController.State
    let elapsed: TimeInterval

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(fixColor)
                .frame(width: 10, height: 10)
            Text(speedText)
                .font(.system(.title3, design: .monospaced).weight(.semibold))
            Text(accuracyText)
                .font(.system(.footnote, design: .monospaced))
                .foregroundStyle(.secondary)
            Spacer()
            if state == .recording {
                Circle().fill(.red).frame(width: 10, height: 10)
                Text(timeText)
                    .font(.system(.title3, design: .monospaced).weight(.semibold))
                    .foregroundStyle(.red)
            }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.black.opacity(0.45), in: RoundedRectangle(cornerRadius: 14))
        .padding(.horizontal, 12)
        .padding(.top, 8)
    }

    private var fixColor: Color {
        if sensors.locationDenied { return .red }
        return sensors.hasFix ? .green : .yellow
    }

    private var speedText: String {
        guard sensors.lastSpeedMps >= 0 else { return "-- km/h" }
        return String(format: "%.0f km/h", sensors.lastSpeedMps * 3.6)
    }

    private var accuracyText: String {
        if sensors.locationDenied { return "GPS off" }
        guard sensors.horizontalAccuracy > 0 else { return "no fix" }
        return String(format: "±%.0fm", sensors.horizontalAccuracy)
    }

    private var timeText: String {
        let total = Int(elapsed)
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

private struct RecordButton: View {
    let state: CaptureController.State
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle()
                    .strokeBorder(.white, lineWidth: 4)
                    .frame(width: 78, height: 78)
                RoundedRectangle(cornerRadius: state == .recording ? 6 : 32)
                    .fill(.red)
                    .frame(width: state == .recording ? 32 : 64,
                           height: state == .recording ? 32 : 64)
                    .animation(.easeInOut(duration: 0.2), value: state)
            }
        }
        .disabled(state == .saving)
        .opacity(state == .saving ? 0.5 : 1)
    }
}
