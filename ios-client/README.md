# XrealBridge -- iOS Client

The iPhone side of the PC-to-iOS wireless display+trackpad bridge. Decodes
the PC's H264 desktop stream and shows it full-bleed on XREAL AR glasses
connected via USB-C (as a native external-display `UIWindowScene`), while
the iPhone's own screen becomes a black trackpad for controlling the PC's
mouse and keyboard.

> Built entirely on a Windows machine with no macOS/Xcode/Swift toolchain
> available, so **none of this has been compiled or run locally**. It has
> only been validated by static review and by GitHub Actions
> (`.github/workflows/ios-build.yml`), which builds and unit-tests the app
> on a macOS runner. See "Known risk areas" below before assuming
> everything just works.

## Opening the project

1. Install Xcode 16 or later (required for the `.xcodeproj`'s use of
   Xcode 16's file-system-synchronized groups -- source files are picked
   up automatically from the `XrealBridge/` and `XrealBridgeTests/`
   folders rather than being individually listed in the project file).
2. Open `ios-client/XrealBridge.xcodeproj` in Xcode.
3. Select the `XrealBridge` scheme.
4. Pick a run destination:
   - **iOS Simulator** builds and runs, but cannot show anything useful on
     an external display or talk to real XREAL glasses -- the Simulator
     has no external-display support. Use it only to confirm the app
     launches and the trackpad UI renders.
   - **A physical iPhone** is required to actually test video decode,
     external-display routing, and the glasses.

## Deploying to a device

1. In the project's target settings (Signing & Capabilities), select your
   own Apple ID / development team under **Signing Certificate** --
   `CODE_SIGN_STYLE` is set to `Automatic` but no team is pre-configured in
   the checked-in project (there's no shared team to bake in).
2. Plug in your iPhone, select it as the run destination, and hit Run.
3. The first launch will trigger the iOS **Local Network** permission
   prompt (needed because the app talks UDP to a LAN IP address) --
   allow it, or the handshake to the PC will silently never get a
   response.

## Connecting to the PC

There's no discovery mechanism in the wire protocol (see
`shared-protocol/PROTOCOL.md`) -- the PC's IP address is entered manually:

- On first launch (no PC IP saved yet), the app immediately prompts for
  the PC's IP address.
- Afterwards, tap the status text at the top of the trackpad screen at any
  time to re-open that prompt and change the IP or reconnect.
- The app persists the last IP you entered (`UserDefaults`) and
  auto-connects to it on subsequent launches.
- Default ports (9000 Control / 9001 Video / 9002 Input) match
  `PROTOCOL.md`'s defaults and aren't currently user-configurable from the
  UI -- only the host/IP is.

## Connecting the XREAL glasses

1. Plug the XREAL glasses into the iPhone via a USB-C cable (the glasses
   themselves act as a USB-C external display accessory).
2. iOS creates a new `UIWindowScene` for the glasses automatically; the
   app detects this via scene role (`role == .windowExternalDisplay`, see
   `XrealBridgeApp.swift`) and routes the decoded video view to it --
   nothing needs to be done in-app to "activate" the glasses beyond
   plugging them in.
3. The iPhone's own screen switches to being purely the black trackpad
   surface; the glasses show the PC's decoded desktop full-bleed.
4. Unplugging the glasses tears down that scene automatically; the
   Control channel connection to the PC is unaffected (it's owned by an
   app-wide singleton, not the external-display scene) and reattaches
   decode output automatically if the glasses are plugged back in.

## Using the trackpad

- **1-finger drag**: move the PC's mouse cursor (relative deltas, not
  absolute position).
- **2-finger drag**: scroll.
- **1-finger tap**: left click.
- **2-finger tap**: right click.
- **Floating keyboard button** (bottom-right): summons the system
  keyboard for typing. Typed characters are sent as `KeyDown`+`KeyUp`
  pairs immediately (the software keyboard has no real key-hold timing;
  see the comment above `TrackpadViewController`'s `UITextFieldDelegate`
  extension for why). A physical/Bluetooth hardware keyboard, if paired,
  additionally gets real key-down/key-up timing via `pressesBegan`/
  `pressesEnded`, independent of whether the on-screen keyboard is
  summoned.

## Auto-updating builds during active development (AltStore)

Re-uploading a fresh `.ipa` to a signing service (Signulous, Sideloadly)
by hand every time the code changes gets old fast. For rapid iteration,
set up AltStore once instead:

1. Install **AltServer** on this PC ([altstore.io](https://faq.altstore.io/getting-started/installation)) and pair it with your iPhone over USB once (it'll ask for an Apple ID -- a free one is fine, same as any sideload tool).
2. AltServer installs the **AltStore** app on your phone. Open it.
3. In AltStore, add this repo's auto-updating source (Sources tab -> "+"):
   ```
   https://raw.githubusercontent.com/uid01/glasses/main/distribution/source.json
   ```
4. Install "XrealBridge" from that source.

After that: every push that touches `ios-client/**` triggers
`.github/workflows/ios-sideload-build.yml`, which builds a fresh unsigned
`.ipa`, bumps the build number, and republishes it to that same release
URL. AltStore will show an "Update" button on its own -- no more manual
re-signing per build. Free-account signatures still expire after 7 days;
AltServer (or SideStore's phone-only self-refresh, if you switch to that
later) handles re-signing the *existing* install automatically as long as
it can periodically reach the device -- that's separate from, and doesn't
require, a rebuild here.

Signulous/Sideloadly still work fine any time you just want a one-off
install without setting any of this up.

## Keeping the connection alive

Two things matter here because you're looking through the glasses, not at
the phone screen, so nothing is touching the display:

- The app disables the iOS idle timer (`UIApplication.isIdleTimerDisabled`)
  for as long as it intends to stay connected, so the phone won't auto-lock
  and suspend the app's networking mid-session. It's re-enabled on an
  explicit disconnect.
- If a session does drop anyway (Wi-Fi blip, PC restarted, etc.),
  `BridgeSession` automatically retries the handshake with exponential
  backoff (1s, 2s, 4s... capped at 10s, reset on the next successful
  connect) rather than sitting disconnected until you manually reopen the
  IP prompt.

If it's still dropping repeatedly after this, the next things to check are
Wi-Fi client isolation on your router (see the top-level README's
Troubleshooting section) and whether Low Power Mode is throttling
background network activity.

## Running the unit tests

`XrealBridgeTests/ProtocolTests.swift` covers packet encode/decode round
trips for every `PacketType` and `FragmentReassembler` reassembly logic
(in-order, out-of-order, missing fragments, stale-frame purging,
concurrent frames). These tests touch only `Foundation` -- no
UIKit/Network/VideoToolbox -- so they're the most reliable thing in this
project to run in CI or locally: `Product > Test` in Xcode, or
`xcodebuild test` (see the CI workflow for the exact invocation).

## Multi-monitor display mode (genuinely unverified, read before testing)

`pc-host` can now send a wide multi-monitor canvas (e.g. 3840x1080 for 2
tiled monitors) instead of a single 1920x1080 screen -- see
[`pc-host/README.md`](pc-host/README.md#multi-monitor-capture). For that
to actually work, the glasses need to be *operating in* a display mode
wide enough to show it; `SceneDelegate.swift`'s
`ExternalDisplaySceneDelegate` now selects the widest `UIScreenMode` the
external display reports via `UIScreen.availableModes` before creating
the window, rather than relying on whatever the default mode is (which
`AVSampleBufferDisplayLayer`'s `.resizeAspect` would otherwise just
shrink/letterbox a wide canvas down to fit).

This is real, but genuinely unverified: there's no way to know from this
environment whether the 1S actually advertises more than one
`UIScreenMode` at all (it may report only its native panel resolution as
the sole available mode, in which case this is a no-op and multi-monitor
would need a different mechanism entirely). Check the console log line
`[external-display] available modes: [...]` first if the wide canvas
looks cropped/shrunk rather than pannable.

## Known risk areas (read before assuming this "just works")

Since nothing here could be compiled or run in the environment this was
built in, treat the following as the first places to look if something
doesn't work. Note: `pc-host/` turned out to already exist in this repo
(built by a parallel agent) by the time this client was finished, so its
read-only source was used to cross-check the wire format and a couple of
assumptions below -- see the "confirmed" notes.

- **Wire format cross-check (confirmed)**: `ProtocolTypes.swift`'s byte
  layout for every packet type was compared field-by-field against
  `pc-host/Protocol/*.cs` and matches exactly, including the Disconnect
  offset question below and the common 4-byte header/magic handling. This
  is real evidence of interop, not just self-consistency.
- **Virtual key codes (confirmed)**: `pc-host/Input/InputInjector.cs`
  passes `InputEvent.KeyCode` straight through to Win32 `SendInput`'s
  `KEYBDINPUT.wVk` field with no translation, confirming the Win32 `VK_*`
  assumption in `WindowsVirtualKeyCode` (`TrackpadViewController.swift`)
  is exactly right.
- **Scroll scale (bug found and fixed via cross-check)**: `pc-host`'s
  scroll handling does `mouseData: Dy * WHEEL_DELTA` with
  `WHEEL_DELTA = 120`, i.e. it expects `Scroll`'s dx/dy in "wheel notch"
  units, not raw points. The two-finger pan handler now divides the pan
  translation by a `pointsPerNotch` tuning constant (currently `20.0`,
  a guess -- adjust to taste) before sending; without this the original
  raw-point pass-through would have scrolled roughly 20x+ too fast.
- **Annex-B framing assumption (confirmed)**: `pc-host/Capture/
  AnnexBFrameSplitter.cs` confirms each keyframe access unit carries
  leading SPS(7)/PPS(8) NALs followed by the IDR slice, and subsequent
  frames are single VCL NALs -- exactly what `H264Decoder.swift` expects.

- **`XrealBridge.xcodeproj/project.pbxproj`**: hand-written using Xcode
  16's `PBXFileSystemSynchronizedRootGroup` mechanism (verified against
  real-world Xcode-generated project files found via web search, not
  against an actual Xcode install). Structurally sanity-checked (balanced
  braces/parens, every object ID defined exactly once and referenced
  consistently, every `isa` a real class name) but never opened in Xcode.
  If Xcode complains on first open, this is the most likely culprit.
- **`Info.plist` + synchronized-group interaction**: `Info.plist` lives
  inside the synchronized `XrealBridge/` source folder and is excluded
  from that target's build-file membership via a
  `PBXFileSystemSynchronizedBuildFileExceptionSet`
  (`membershipExceptions = (Info.plist,)`), following the same pattern
  found in a real open-source project's checked-in `.pbxproj`. If this
  doesn't work as expected, Xcode will likely report "multiple commands
  produce Info.plist" -- the fix is either confirming/adjusting that
  exception set in Xcode's UI, or moving `Info.plist` out of the
  synchronized folder and pointing `INFOPLIST_FILE` at its new location.
- **`H264Decoder.swift`**: the VideoToolbox decode pipeline (SPS/PPS
  extraction, Annex-B -> AVCC conversion, `VTDecompressionSession`
  create/decode, C callback via `Unmanaged`) follows the standard
  documented pattern but every function signature was written from memory
  without a compiler to check argument labels. The single detail most
  worth checking first: the Annex-B -> AVCC length-prefix rewrite in
  `makeAVCCBlockBuffer`.
- **`VideoReceiver.swift`**: uses `NWListener` to receive UDP from the PC
  (rather than `NWConnection`, since the PC dials in, not the iOS
  client). Whether `newConnectionHandler` fires the way expected for
  repeated datagrams from the same PC source address/port has not been
  exercised against a real peer.
- **`Disconnect` packet byte layout** (`ProtocolTypes.swift`): PROTOCOL.md
  lists `reason` at offset 5, which overlaps the 4-byte `sessionId` field
  at offset 4 and can't be literally correct. This implementation puts
  `reason` at offset 8 (right after `sessionId`) -- confirmed to match
  `pc-host/Protocol/Disconnect.cs`, which independently reached the same
  conclusion for the same reason. `PROTOCOL.md` itself still has the
  offset-5 typo and should be fixed at the source so future readers don't
  have to rediscover this.
