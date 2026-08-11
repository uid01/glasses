import UIKit

/// Hosts the primary iPhone-screen scene (`role == .windowApplication`): a
/// full-black `TrackpadViewController`.
final class PhoneSceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    private var bridgeSession: BridgeSession { AppEnvironment.shared.bridgeSession }

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        let newWindow = UIWindow(windowScene: windowScene)
        newWindow.rootViewController = TrackpadViewController(bridgeSession: bridgeSession)
        newWindow.backgroundColor = .black
        window = newWindow
        newWindow.makeKeyAndVisible()
    }

    func sceneDidDisconnect(_ scene: UIScene) {
        window = nil
    }
}

/// Hosts the external-display scene (the XREAL glasses' `UIWindowScene`,
/// `role == .windowExternalDisplay`): a full-bleed `ExternalDisplayView`
/// with no letterboxing chrome, showing decoded PC video. Created
/// automatically by UIKit when the glasses are connected over USB-C (given
/// `UIApplicationSupportsMultipleScenes = true` in Info.plist) and torn
/// down automatically on disconnect -- this class never has to detect the
/// physical connection itself.
final class ExternalDisplaySceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    private var bridgeSession: BridgeSession { AppEnvironment.shared.bridgeSession }

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        selectWidestAvailableMode(for: windowScene.screen)

        let newWindow = UIWindow(windowScene: windowScene)
        newWindow.rootViewController = ExternalDisplayViewController(bridgeSession: bridgeSession)
        newWindow.backgroundColor = .black
        window = newWindow
        newWindow.makeKeyAndVisible()
    }

    func sceneDidDisconnect(_ scene: UIScene) {
        window = nil
    }

    /// Selects the widest `UIScreenMode` the connected display advertises, before creating the
    /// window, so the window's initial bounds (and therefore the video layer's rendering
    /// surface) already reflect it.
    ///
    /// Why this exists: `ExternalDisplayView` uses `AVSampleBufferDisplayLayer` with
    /// `.resizeAspect`, which scales *down* to fit whatever the screen's current bounds are --
    /// it cannot make the screen itself wider. Left on the screen's default mode, a wide
    /// multi-monitor canvas from pc-host (e.g. 3840x1080 for 2 tiled monitors) would just get
    /// shrunk/letterboxed to fit that default, narrower mode before ever reaching the glasses'
    /// panel -- meaning the glasses' onboard 3DoF panning (which needs to actually be operating
    /// in a wide display mode to have anything to pan across) would likely never engage. This
    /// picks the widest mode UIKit reports as available for the external screen so the full wide
    /// canvas has somewhere to go.
    ///
    /// GENUINELY UNVERIFIED, flagged clearly rather than asserted as working: whether the XREAL
    /// 1S actually advertises more than one `UIScreenMode` at all (it may report only its native
    /// panel resolution as the sole available mode, in which case this is a no-op and the
    /// multi-monitor canvas would need a different mechanism than "pick a wider UIScreenMode" --
    /// there's no way to know without testing on the real device). If logging shows only one
    /// mode is ever available, that's the first thing to investigate.
    private func selectWidestAvailableMode(for screen: UIScreen) {
        let modes = screen.availableModes
        print("[external-display] available modes: \(modes.map { "\(Int($0.size.width))x\(Int($0.size.height))" })")

        guard let widest = modes.max(by: { $0.size.width < $1.size.width }) else {
            print("[external-display] no availableModes reported; leaving screen on its default mode")
            return
        }

        if widest.size.width > screen.currentMode?.size.width ?? 0 {
            print("[external-display] selecting widest mode: \(Int(widest.size.width))x\(Int(widest.size.height)) (was \(screen.currentMode.map { "\(Int($0.size.width))x\(Int($0.size.height))" } ?? "unknown"))")
            screen.currentMode = widest
        }
    }
}
