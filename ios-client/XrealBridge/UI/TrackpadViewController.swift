import UIKit

/// Full-black trackpad surface shown on the iPhone's own screen (the
/// primary `.windowApplication` scene). Gesture -> protocol mapping, per
/// the coordinator's spec:
///   - 1-finger pan  -> stream of MouseMove events (relative dx/dy deltas)
///   - 2-finger pan  -> Scroll events (wheel dx/dy deltas)
///   - 1-finger tap  -> LeftClick (momentary)
///   - 2-finger tap  -> RightClick (momentary)
///   - floating button -> summons the system keyboard for text/key input
final class TrackpadViewController: UIViewController {
    private let bridgeSession: BridgeSession

    private let statusLabel = UILabel()
    private let keyboardButton = UIButton(type: .system)
    /// Effectively-invisible text field used purely to summon the system
    /// keyboard via becomeFirstResponder(); see the KeyInput extension
    /// below for why it can't be `isHidden = true` (hidden views can't
    /// become first responder / show the keyboard).
    private let hiddenTextField = UITextField()

    private let oneFingerPan = UIPanGestureRecognizer()
    private let twoFingerPan = UIPanGestureRecognizer()
    private let oneFingerTap = UITapGestureRecognizer()
    private let twoFingerTap = UITapGestureRecognizer()

    init(bridgeSession: BridgeSession) {
        self.bridgeSession = bridgeSession
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported -- TrackpadViewController is only created programmatically by PhoneSceneDelegate")
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        overrideUserInterfaceStyle = .dark
        view.backgroundColor = .black

        configureStatusLabel()
        configureHiddenTextField()
        configureKeyboardButton()
        configureGestures()

        bridgeSession.statusDelegate = self
        updateStatusLabel(bridgeSession.status)

        if bridgeSession.pcHost.isEmpty {
            // First launch: prompt for the PC's IP right away since there's
            // no discovery mechanism in the protocol (manual entry only).
            DispatchQueue.main.async { [weak self] in
                self?.presentIPEntryAlert()
            }
        } else {
            bridgeSession.connect(host: bridgeSession.pcHost)
        }
    }

    override var prefersStatusBarHidden: Bool { true }
    override var prefersHomeIndicatorAutoHidden: Bool { true }

    // MARK: - UI setup

    private func configureStatusLabel() {
        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        statusLabel.textColor = .white
        statusLabel.font = .systemFont(ofSize: 13, weight: .medium)
        statusLabel.textAlignment = .center
        statusLabel.numberOfLines = 2
        statusLabel.isUserInteractionEnabled = true
        statusLabel.addGestureRecognizer(UITapGestureRecognizer(target: self, action: #selector(statusLabelTapped)))
        view.addSubview(statusLabel)
        NSLayoutConstraint.activate([
            statusLabel.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            statusLabel.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            statusLabel.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16)
        ])
    }

    private func configureHiddenTextField() {
        // Not `isHidden = true` deliberately -- a hidden UIView cannot
        // become first responder / summon the keyboard. Instead it's
        // shrunk to 1x1 and made effectively transparent.
        hiddenTextField.frame = CGRect(x: 0, y: 0, width: 1, height: 1)
        hiddenTextField.alpha = 0.01
        hiddenTextField.autocorrectionType = .no
        hiddenTextField.autocapitalizationType = .none
        hiddenTextField.smartQuotesType = .no
        hiddenTextField.smartDashesType = .no
        hiddenTextField.smartInsertDeleteType = .no
        hiddenTextField.delegate = self
        view.addSubview(hiddenTextField)
    }

    private func configureKeyboardButton() {
        keyboardButton.translatesAutoresizingMaskIntoConstraints = false
        var config = UIButton.Configuration.filled()
        config.baseBackgroundColor = UIColor.white.withAlphaComponent(0.15)
        config.baseForegroundColor = .white
        config.image = UIImage(systemName: "keyboard")
        config.cornerStyle = .capsule
        config.contentInsets = NSDirectionalEdgeInsets(top: 14, leading: 14, bottom: 14, trailing: 14)
        keyboardButton.configuration = config
        keyboardButton.addTarget(self, action: #selector(keyboardButtonTapped), for: .touchUpInside)
        view.addSubview(keyboardButton)
        NSLayoutConstraint.activate([
            keyboardButton.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -20),
            keyboardButton.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -20)
        ])
    }

    private func configureGestures() {
        oneFingerPan.minimumNumberOfTouches = 1
        oneFingerPan.maximumNumberOfTouches = 1
        oneFingerPan.addTarget(self, action: #selector(handleOneFingerPan(_:)))
        view.addGestureRecognizer(oneFingerPan)

        twoFingerPan.minimumNumberOfTouches = 2
        twoFingerPan.maximumNumberOfTouches = 2
        twoFingerPan.addTarget(self, action: #selector(handleTwoFingerPan(_:)))
        view.addGestureRecognizer(twoFingerPan)

        oneFingerTap.numberOfTouchesRequired = 1
        oneFingerTap.numberOfTapsRequired = 1
        oneFingerTap.addTarget(self, action: #selector(handleOneFingerTap(_:)))
        view.addGestureRecognizer(oneFingerTap)

        twoFingerTap.numberOfTouchesRequired = 2
        twoFingerTap.numberOfTapsRequired = 1
        twoFingerTap.addTarget(self, action: #selector(handleTwoFingerTap(_:)))
        view.addGestureRecognizer(twoFingerTap)
    }

    // MARK: - Gesture handlers

    @objc private func handleOneFingerPan(_ gr: UIPanGestureRecognizer) {
        guard gr.state == .changed else { return }
        let translation = gr.translation(in: view)
        gr.setTranslation(.zero, in: view)
        guard translation != .zero else { return }
        bridgeSession.sendMouseMove(dx: Float(translation.x), dy: Float(translation.y))
    }

    @objc private func handleTwoFingerPan(_ gr: UIPanGestureRecognizer) {
        guard gr.state == .changed else { return }
        let translation = gr.translation(in: view)
        gr.setTranslation(.zero, in: view)
        guard translation != .zero else { return }
        // Scale confirmed against the actual pc-host implementation
        // (pc-host/Input/InputInjector.cs, read-only reference -- not
        // modified by this agent): it does
        // `SendMouse(MOUSEEVENTF_WHEEL, ..., mouseData: Dy * WHEEL_DELTA)`
        // with WHEEL_DELTA = 120, i.e. it expects Scroll's dx/dy in "wheel
        // notch" units (1.0 == one notch), NOT raw points -- sending raw
        // point deltas directly would produce a scroll ~20x+ too fast.
        // pointsPerNotch is a UX tuning constant, not a protocol constant;
        // adjust to taste once tested on a real device.
        let pointsPerNotch: Float = 20.0
        let dx = Float(translation.x) / pointsPerNotch
        let dy = Float(translation.y) / pointsPerNotch
        // Sign convention (natural vs. "traditional" scrolling) is a UX
        // detail, not a protocol one -- passed through directly here;
        // flip if it feels backwards once tested against the real PC host.
        bridgeSession.sendScroll(dx: dx, dy: dy)
    }

    @objc private func handleOneFingerTap(_ gr: UITapGestureRecognizer) {
        bridgeSession.sendLeftClick()
    }

    @objc private func handleTwoFingerTap(_ gr: UITapGestureRecognizer) {
        bridgeSession.sendRightClick()
    }

    // MARK: - Keyboard button / IP entry

    @objc private func keyboardButtonTapped() {
        if hiddenTextField.isFirstResponder {
            hiddenTextField.resignFirstResponder()
        } else {
            hiddenTextField.text = ""
            hiddenTextField.becomeFirstResponder()
        }
    }

    @objc private func statusLabelTapped() {
        presentIPEntryAlert()
    }

    private func presentIPEntryAlert() {
        let alert = UIAlertController(
            title: "PC IP Address",
            message: "Enter the Windows PC's IP address on your local network.",
            preferredStyle: .alert
        )
        alert.addTextField { field in
            field.placeholder = "e.g. 192.168.1.42"
            field.text = self.bridgeSession.pcHost
            field.keyboardType = .numbersAndPunctuation
            field.autocorrectionType = .no
            field.autocapitalizationType = .none
        }
        alert.addAction(UIAlertAction(title: "Cancel", style: .cancel))
        alert.addAction(UIAlertAction(title: "Connect", style: .default) { [weak self, weak alert] _ in
            guard let self = self, let text = alert?.textFields?.first?.text, !text.isEmpty else { return }
            self.bridgeSession.connect(host: text.trimmingCharacters(in: .whitespacesAndNewlines))
        })
        present(alert, animated: true)
    }

    fileprivate func updateStatusLabel(_ status: BridgeSession.Status) {
        let host = bridgeSession.pcHost.isEmpty ? "no PC set" : bridgeSession.pcHost
        switch status {
        case .disconnected:
            statusLabel.text = "Disconnected -- \(host)\nTap to set PC IP"
        case .connecting:
            statusLabel.text = "Connecting to \(host)..."
        case let .connected(width, height, fps):
            statusLabel.text = "Connected to \(host)\n\(width)x\(height)@\(fps)"
        }
    }
}

// MARK: - BridgeSessionStatusDelegate

extension TrackpadViewController: BridgeSessionStatusDelegate {
    func bridgeSession(_ session: BridgeSession, statusDidChange status: BridgeSession.Status) {
        DispatchQueue.main.async { [weak self] in
            self?.updateStatusLabel(status)
        }
    }
}

// MARK: - Keyboard input (software keyboard path)

/// NOTE on a real limitation, not just an "unverified API" caveat: the
/// *software* keyboard summoned via `becomeFirstResponder()` on a
/// UITextField never delivers discrete key-down/key-up timing -- only
/// character insertion/deletion via `UITextFieldDelegate`. So for the
/// software-keyboard path we synthesize an immediate KeyDown followed by
/// KeyUp for each character as it's typed, which is fine for basic text
/// entry but cannot represent held keys or true press-and-hold repeat on
/// the PC side. Real key-up/key-down timing IS available from a *physical*
/// (e.g. Bluetooth) hardware keyboard via `UIResponder.pressesBegan/
/// pressesEnded`, implemented separately below, and works independently of
/// whether the software keyboard is currently summoned.
extension TrackpadViewController: UITextFieldDelegate {
    func textField(
        _ textField: UITextField,
        shouldChangeCharactersIn range: NSRange,
        replacementString string: String
    ) -> Bool {
        if string.isEmpty {
            // Backspace / delete.
            sendKeyTap(WindowsVirtualKeyCode.back)
        } else {
            for scalar in string.unicodeScalars {
                let character = Character(scalar)
                if let vk = WindowsVirtualKeyCode.forCharacter(character) {
                    sendKeyTap(vk)
                }
            }
        }
        // Never actually let the text accumulate in the field -- we only
        // use it as a keyboard-summoning surface, not a real text buffer.
        return false
    }

    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        sendKeyTap(WindowsVirtualKeyCode.returnKey)
        return false
    }

    private func sendKeyTap(_ keyCode: UInt16) {
        bridgeSession.sendKeyDown(keyCode: keyCode)
        bridgeSession.sendKeyUp(keyCode: keyCode)
    }
}

// MARK: - Keyboard input (physical hardware keyboard path)

extension TrackpadViewController {
    override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        for press in presses {
            if let key = press.key, let vk = WindowsVirtualKeyCode.forHIDUsage(key.keyCode) {
                bridgeSession.sendKeyDown(keyCode: vk)
            }
        }
        super.pressesBegan(presses, with: event)
    }

    override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        for press in presses {
            if let key = press.key, let vk = WindowsVirtualKeyCode.forHIDUsage(key.keyCode) {
                bridgeSession.sendKeyUp(keyCode: vk)
            }
        }
        super.pressesEnded(presses, with: event)
    }
}

// MARK: - Virtual key code mapping

/// PROTOCOL.md defines `keyCode` as an opaque uint16 "virtual key code"
/// without pinning down the numbering scheme. Since the PC host is
/// Windows, this maps to standard Win32 Virtual-Key (VK_*) codes, which is
/// a reasonable default assumption -- but it is a genuine protocol
/// ambiguity, not just an unverified-API note, and should be confirmed
/// against what pc-host actually expects for non-letter/digit keys.
/// Coverage here is intentionally partial (common typing + navigation
/// keys); anything not listed is simply not sent.
enum WindowsVirtualKeyCode {
    static let back: UInt16 = 0x08
    static let tab: UInt16 = 0x09
    static let returnKey: UInt16 = 0x0D
    static let escape: UInt16 = 0x1B
    static let space: UInt16 = 0x20
    static let leftArrow: UInt16 = 0x25
    static let upArrow: UInt16 = 0x26
    static let rightArrow: UInt16 = 0x27
    static let downArrow: UInt16 = 0x28
    static let deleteKey: UInt16 = 0x2E

    /// VK_0...VK_9 == ASCII '0'...'9' on Windows, conveniently.
    /// VK_A...VK_Z == ASCII 'A'...'Z' on Windows, conveniently (case is
    /// conveyed by a separate shift-state bit on the PC side, not by
    /// which VK code is sent, so lowercase letters map to the same code
    /// as their uppercase form).
    static func forCharacter(_ character: Character) -> UInt16? {
        if let ascii = character.asciiValue {
            switch character {
            case "0"..."9", "A"..."Z":
                return UInt16(ascii)
            case "a"..."z":
                return UInt16(ascii) - 32 // lowercase -> uppercase VK code
            case " ":
                return space
            default:
                break
            }
        }
        switch character {
        case ";", ":": return 0xBA // VK_OEM_1
        case "=", "+": return 0xBB // VK_OEM_PLUS
        case ",", "<": return 0xBC // VK_OEM_COMMA
        case "-", "_": return 0xBD // VK_OEM_MINUS
        case ".", ">": return 0xBE // VK_OEM_PERIOD
        case "/", "?": return 0xBF // VK_OEM_2
        case "`", "~": return 0xC0 // VK_OEM_3
        case "[", "{": return 0xDB // VK_OEM_4
        case "\\", "|": return 0xDC // VK_OEM_5
        case "]", "}": return 0xDD // VK_OEM_6
        case "'", "\"": return 0xDE // VK_OEM_7
        default: return nil
        }
    }

    /// Best-effort mapping from a subset of `UIKeyboardHIDUsage` values
    /// (physical/hardware keyboard input) to the same VK code space used
    /// above. `UIKeyboardHIDUsage` mirrors the USB HID keyboard usage
    /// table; only common keys are covered.
    static func forHIDUsage(_ hidUsage: UIKeyboardHIDUsage) -> UInt16? {
        switch hidUsage {
        case .keyboardA: return UInt16(("A" as Character).asciiValue!)
        case .keyboardB: return UInt16(("B" as Character).asciiValue!)
        case .keyboardC: return UInt16(("C" as Character).asciiValue!)
        case .keyboardD: return UInt16(("D" as Character).asciiValue!)
        case .keyboardE: return UInt16(("E" as Character).asciiValue!)
        case .keyboardF: return UInt16(("F" as Character).asciiValue!)
        case .keyboardG: return UInt16(("G" as Character).asciiValue!)
        case .keyboardH: return UInt16(("H" as Character).asciiValue!)
        case .keyboardI: return UInt16(("I" as Character).asciiValue!)
        case .keyboardJ: return UInt16(("J" as Character).asciiValue!)
        case .keyboardK: return UInt16(("K" as Character).asciiValue!)
        case .keyboardL: return UInt16(("L" as Character).asciiValue!)
        case .keyboardM: return UInt16(("M" as Character).asciiValue!)
        case .keyboardN: return UInt16(("N" as Character).asciiValue!)
        case .keyboardO: return UInt16(("O" as Character).asciiValue!)
        case .keyboardP: return UInt16(("P" as Character).asciiValue!)
        case .keyboardQ: return UInt16(("Q" as Character).asciiValue!)
        case .keyboardR: return UInt16(("R" as Character).asciiValue!)
        case .keyboardS: return UInt16(("S" as Character).asciiValue!)
        case .keyboardT: return UInt16(("T" as Character).asciiValue!)
        case .keyboardU: return UInt16(("U" as Character).asciiValue!)
        case .keyboardV: return UInt16(("V" as Character).asciiValue!)
        case .keyboardW: return UInt16(("W" as Character).asciiValue!)
        case .keyboardX: return UInt16(("X" as Character).asciiValue!)
        case .keyboardY: return UInt16(("Y" as Character).asciiValue!)
        case .keyboardZ: return UInt16(("Z" as Character).asciiValue!)
        case .keyboard0: return UInt16(("0" as Character).asciiValue!)
        case .keyboard1: return UInt16(("1" as Character).asciiValue!)
        case .keyboard2: return UInt16(("2" as Character).asciiValue!)
        case .keyboard3: return UInt16(("3" as Character).asciiValue!)
        case .keyboard4: return UInt16(("4" as Character).asciiValue!)
        case .keyboard5: return UInt16(("5" as Character).asciiValue!)
        case .keyboard6: return UInt16(("6" as Character).asciiValue!)
        case .keyboard7: return UInt16(("7" as Character).asciiValue!)
        case .keyboard8: return UInt16(("8" as Character).asciiValue!)
        case .keyboard9: return UInt16(("9" as Character).asciiValue!)
        case .keyboardReturnOrEnter: return returnKey
        case .keyboardEscape: return escape
        case .keyboardDeleteOrBackspace: return back
        case .keyboardTab: return tab
        case .keyboardSpacebar: return space
        case .keyboardRightArrow: return rightArrow
        case .keyboardLeftArrow: return leftArrow
        case .keyboardDownArrow: return downArrow
        case .keyboardUpArrow: return upArrow
        case .keyboardDeleteForward: return deleteKey
        default: return nil
        }
    }
}
