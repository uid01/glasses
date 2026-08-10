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
        let newWindow = UIWindow(windowScene: windowScene)
        newWindow.rootViewController = ExternalDisplayViewController(bridgeSession: bridgeSession)
        newWindow.backgroundColor = .black
        window = newWindow
        newWindow.makeKeyAndVisible()
    }

    func sceneDidDisconnect(_ scene: UIScene) {
        window = nil
    }
}
