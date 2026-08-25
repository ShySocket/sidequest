import AVFoundation
import SwiftUI
import UIKit

/// Full-screen live preview of the capture session.
struct CameraPreviewView: UIViewRepresentable {
    let session: AVCaptureSession

    final class PreviewView: UIView {
        override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
        var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
    }

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspectFill
        return view
    }

    func updateUIView(_ uiView: PreviewView, context: Context) {
        // SwiftUI re-invokes this on rotation; keep the preview upright.
        if let connection = uiView.previewLayer.connection {
            let angle = VideoRotation.currentInterfaceAngle()
            if connection.isVideoRotationAngleSupported(angle) {
                connection.videoRotationAngle = angle
            }
        }
    }
}

enum VideoRotation {
    /// Rotation angle (degrees) that makes back-camera buffers upright for the
    /// current interface orientation.
    @MainActor
    static func currentInterfaceAngle() -> CGFloat {
        let scene = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .first
        switch scene?.interfaceOrientation {
        case .landscapeRight: return 0
        case .landscapeLeft: return 180
        case .portraitUpsideDown: return 270
        default: return 90 // portrait
        }
    }
}
