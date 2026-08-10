# XREAL Wireless PC↔iOS Display & Trackpad Bridge

Streams a Windows desktop (RTX GPU, hardware H264/HEVC encode) over Wi-Fi to
an iPhone, which drives XREAL AR glasses as a dedicated external display
(`UIWindowScene`, full-bleed, no mirroring) and turns its own touchscreen
into a wireless trackpad + keyboard for the PC.

> **Status note (read before relying on this):** the iOS client was written
> without access to a Mac. It's now confirmed to actually **compile and
> pass its unit tests** via GitHub Actions CI
> ([latest run](https://github.com/uid01/glasses/actions)) — that CI run
> also caught and required fixing one real API-signature error, which is
> exactly the kind of thing "written blind" risks. What CI *cannot* cover —
> real VideoToolbox decode behavior, external-display/glasses routing, and
> actual PC↔phone network interop — is still unverified and needs a
> physical iPhone + XREAL glasses. The PC host, by contrast, was built and
> smoke-tested for real on the machine that authored it (real RTX GPU, real
> ffmpeg/NVENC fallback, real UDP traffic, ffprobe-confirmed decodable
> output). See `overnight_build_log.txt` for the full narrative and each
> subproject's README for specifics.

## Layout

```
pc-host/            Windows service: screen capture, hardware encode, UDP streaming, input injection (C# / .NET 8)
ios-client/          iPhone app: external display routing, decode, render, trackpad UI (Swift / Xcode project)
shared-protocol/     PROTOCOL.md — the authoritative UDP wire format both sides implement against
tests/               Cross-cutting mock/test tooling
.github/workflows/   CI (iOS build verification via GitHub Actions macOS runners)
```

## Quick start

### 1. PC host (Windows, the machine with the RTX GPU)
See [`pc-host/README.md`](pc-host/README.md) for full detail. Short version:
```bash
cd pc-host
dotnet build
dotnet run -- --mock      # no ffmpeg/client needed, exercises the network+protocol path
dotnet run                # real capture; waits for a Handshake on UDP 9000
```
Prerequisites: .NET 8 SDK, ffmpeg on PATH. An NVIDIA GPU is not strictly
required — the host probes `h264_nvenc` at startup and falls back to
software `libx264` automatically if it's unavailable or the driver is too
old for the ffmpeg build's NVENC API version (this happened on the actual
dev machine — RTX 5070 Ti, driver 595.95 — see
[`pc-host/README.md`](pc-host/README.md#encoder-fallback-nvenc---libx264)).
Capture itself stays GPU-side (DXGI Desktop Duplication via ffmpeg's
`ddagrab`) either way; only encode falls back to CPU.

### 2. iOS client (requires a Mac + Xcode 16+ — not buildable from this repo alone)
See [`ios-client/README.md`](ios-client/README.md) for full detail. Short version: open
`ios-client/XrealBridge.xcodeproj` in **Xcode 16 or later** (required — the
project uses Xcode 16's file-system-synchronized groups), set your team for
signing, build to a real iPhone (external-display + USB accessory features
don't work in the Simulator), enter the PC's LAN IP address in the app,
connect the XREAL glasses via USB-C.

### 3. Connecting the glasses
Plug XREAL glasses into the iPhone's USB-C port. iOS should fire
`UIScreen.didConnectNotification`; the app's external-display scene picks
that up and renders the video full-bleed on the glasses while the trackpad
UI stays on the phone screen. If nothing appears on the glasses, see
Troubleshooting below.

## Protocol

Both sides implement [`shared-protocol/PROTOCOL.md`](shared-protocol/PROTOCOL.md)
independently — it's the single source of truth for packet formats. If
you're debugging an interop issue, start there.

## Troubleshooting

- **PC host: `dotnet run` exits immediately / no NVENC.** Run
  `ffmpeg -encoders | findstr nvenc` — if `h264_nvenc` isn't listed, your
  ffmpeg build lacks NVENC support or the NVIDIA driver is out of date.
- **PC host: firewall blocks the phone.** Windows Firewall will prompt on
  first run for UDP 9000-9002; allow it on Private networks. If you don't
  get prompted, add a rule manually (`New-NetFirewallRule -DisplayName
  "XrealBridge" -Direction Inbound -Protocol UDP -LocalPort 9000-9002
  -Action Allow`).
- **iOS: glasses don't light up / mirrors the phone instead of full-bleed.**
  This is almost always the scene-role routing — check that
  `UIApplicationSupportsMultipleScenes` is `true` in Info.plist and that the
  app delegate is actually branching on
  `connectingSceneSession.role == .windowExternalDisplay`. See
  `ios-client/README.md` for the specific files involved.
- **iOS: connects but video never starts.** Confirm the PC's IP entered in
  the app is reachable from the phone (same LAN/subnet, no client isolation
  on the router/AP — a lot of "guest" Wi-Fi networks block device-to-device
  traffic, which will look exactly like this symptom). Check the PC host
  console log for whether a Handshake was even received.
- **Connection keeps dropping.** The app keeps the phone's screen awake
  and auto-reconnects with backoff on an unexpected drop (see
  `ios-client/README.md#keeping-the-connection-alive`) — if it's still
  dropping after that, check Wi-Fi client isolation (below) and Low Power
  Mode, both of which can throttle/block the background UDP traffic this
  app depends on.
- **High latency / stutter.** Wired 5GHz or 6GHz Wi-Fi with the PC on
  Ethernet will beat any wireless-to-wireless hop by a wide margin at
  1080p/60. `-tune ll -zerolatency 1` on the ffmpeg encode is already set
  for lowest latency over throughput; don't raise the GOP size much beyond
  2x the frame rate or recovery from a dropped keyframe gets slow.
- **Build fails in Xcode with an unrecognized project format / corrupt
  project error.** The `.xcodeproj` in this repo was hand-authored without
  access to Xcode to validate it (see status note above) — this is the most
  likely single point of failure in the whole repo. Worst case, create a
  fresh Xcode project and copy the `ios-client/XrealBridge/**/*.swift`
  source files + `Info.plist` keys into it by hand.

## What's real vs. simulated in this repo

- **Real and verified on this machine:** PC-side screen capture, NVENC
  hardware encode, UDP fragmentation/reassembly protocol logic, unit tests.
- **Written but not locally verified:** the entire iOS app (no Mac
  available) — verified only by compilation in CI, not by running.
- **Not verifiable in this environment at all, by anyone, regardless of
  tooling:** actual XREAL glasses lighting up, actual touch-to-cursor feel,
  actual end-to-end latency numbers. Needs the physical hardware.
