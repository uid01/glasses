import UIKit

/// App entry point (`@main`). Plain UIKit, no SwiftUI -- this app's most
/// important and trickiest piece is multi-scene, role-based routing (see
/// below), which is best-documented and most predictable via the classic
/// `UIApplicationDelegate` + `UIWindowSceneDelegate` mechanism. Mixing that
/// with a SwiftUI `App`/`WindowGroup` lifecycle would add a layer of
/// uncertainty about how SwiftUI's own scene handling interacts with
/// manual `UISceneSession` role routing -- not worth the risk in an
/// environment where none of this can be compiled or run to check.
///
/// ## Scene routing: why role-based, and how it works
///
/// Prior to scene-based multi-window support, a second physical display
/// was handled via `UIScreen.didConnectNotification`/
/// `didDisconnectNotification`, manually creating a second `UIWindow` for
/// `UIScreen.screens[1]`. That mechanism still technically exists but is
/// legacy. Once `UIApplicationSupportsMultipleScenes` is `true` (set in
/// Info.plist) and the app is scene-based, UIKit instead represents an
/// external display as its own distinct `UIWindowScene`, created and
/// destroyed automatically as the XREAL glasses are plugged/unplugged over
/// USB-C, with `session.role == .windowExternalDisplay` -- alongside the
/// phone's own primary scene, which has `role == .windowApplication`. Both
/// route through the single delegate method below,
/// `application(_:configurationForConnecting:options:)`, which this app
/// uses as the one place that decides which UIKit scene delegate class
/// (and therefore which root view controller) handles which physical
/// screen. This is the modern, correct mechanism, and this app relies on
/// it exclusively -- there is no `UIScreen.didConnectNotification` code
/// anywhere in this project.
@main
final class AppDelegate: UIResponder, UIApplicationDelegate {

    var window: UIWindow?

    func application(
        _ application: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options: UIScene.ConnectionOptions
    ) -> UISceneConfiguration {
        if connectingSceneSession.role == .windowExternalDisplay {
            let config = UISceneConfiguration(name: "External Display Configuration", sessionRole: connectingSceneSession.role)
            config.delegateClass = ExternalDisplaySceneDelegate.self
            return config
        }

        let config = UISceneConfiguration(name: "Phone Configuration", sessionRole: connectingSceneSession.role)
        config.delegateClass = PhoneSceneDelegate.self
        return config
    }

    func application(
        _ application: UIApplication,
        didDiscardSceneSessions sceneSessions: Set<UISceneSession>
    ) {
        // Nothing to clean up beyond what each UIWindowSceneDelegate
        // already does in sceneDidDisconnect(_:); the long-lived
        // networking objects live in AppEnvironment.shared.bridgeSession,
        // independent of any single scene's lifetime.
    }
}
