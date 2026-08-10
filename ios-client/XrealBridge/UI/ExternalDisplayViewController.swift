import UIKit

/// Trivial container view controller for the external-display scene: hosts
/// a full-bleed `ExternalDisplayView` with no additional chrome (no nav
/// bar, no safe-area insetting concerns beyond what the glasses' own
/// display panel imposes). Wires itself into `BridgeSession` as the
/// current decode-output target for as long as it's on screen.
final class ExternalDisplayViewController: UIViewController {
    private let bridgeSession: BridgeSession
    private let displayView = ExternalDisplayView()

    init(bridgeSession: BridgeSession) {
        self.bridgeSession = bridgeSession
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported -- ExternalDisplayViewController is only created programmatically by ExternalDisplaySceneDelegate")
    }

    override func loadView() {
        view = displayView
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        overrideUserInterfaceStyle = .dark
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        bridgeSession.attachExternalDisplay(displayView)
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        bridgeSession.detachExternalDisplay()
    }

    override var prefersStatusBarHidden: Bool { true }

    override var prefersHomeIndicatorAutoHidden: Bool { true }
}
