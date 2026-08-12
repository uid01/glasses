# XrealBridge Monitor Config (pc-host-gui)

A WPF desktop app that replaces manually editing `pc-host` CLI flags and running it from a
terminal: scan real + virtual monitors, arrange them into a grid visually, manage virtual
monitors, and start/stop the bridge — all from one window with a live log pane and a system tray
icon so it can run in the background.

## Prerequisites

- Windows, .NET 8 SDK.
- `ffmpeg` on `PATH` (same requirement as `pc-host` — this app shells out to it directly for the
  monitor-scanning thumbnails, independent of whatever `pc-host` process it later launches).
- A built `pc-host/PcHost.exe` somewhere on disk (the app tries to auto-detect the sibling
  dev-build path; use the Browse button if that guess is wrong, e.g. in a packaged install).

## Running

```bash
cd pc-host-gui
dotnet build
dotnet run
```

## How it works

1. **Scan Monitors** captures one real preview frame per DXGI output (`ddagrab output_idx` 0, 1,
   2, ... stopping at the first one that fails to open) via a direct `ffmpeg` invocation, and
   shows each as a real thumbnail with its actual resolution — deliberately not just trusting
   `Screen.AllScreens` order to match ddagrab's DXGI enumeration order, since those two orderings
   aren't guaranteed to agree (see `Models/MonitorSource.cs`).
2. **Arrange monitors into a grid**: add/remove rows and columns, assign a scanned monitor to
   each cell via dropdown, set tile size and horizontal/vertical gaps. This builds exactly the
   grid-spec string `pc-host --monitors` expects (`"0,1;2,3"` — rows separated by `;`, columns by
   `,`); `Models/GridConfig.ToGridSpec()` is unit-tested against that exact format.
3. **Virtual monitors**: detects whether [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
   is installed (see `pc-host/README.md`'s "Virtual monitors" section for how it was installed
   and verified), writes `C:\VirtualDisplayDriver\vdd_settings.xml` with the requested
   count/resolution, and reloads the driver via an elevated PowerShell (PnP disable+enable) —
   triggers one UAC prompt. Re-scan afterward to see the new virtual monitor(s).
4. **Start/Stop Bridge**: launches `PcHost.exe` as a subprocess with the computed
   `--monitors`/`--tile-width`/`--tile-height`/`--gap-x`/`--gap-y`/port args, streaming its
   console output into the log pane. Stop does a hard `Kill(entireProcessTree: true)` rather than
   a graceful Ctrl+C (see `Services/BridgeProcessManager.cs` for why) — fine since pc-host holds
   no state that needs graceful persistence on exit.
5. Closing the window (the X button) minimizes to the system tray instead of exiting, so the
   bridge keeps running in the background; the tray icon's right-click menu has the real Exit.
6. Settings (grid layout, PcHost.exe path, virtual monitor preferences) persist to
   `%AppData%\XrealBridge\gui_settings.json` and reload on next launch.

## Verification performed

Unlike the iOS client, this is a Windows .NET app buildable and runnable in the same environment
it was written in, so it was verified for real rather than by code review alone:

- Built clean (`dotnet build`, 0 warnings/errors) and launched successfully.
- **Found and fixed a real bug via actual execution, not review**: the monitor scanner initially
  fed ddagrab's raw GPU-resident `d3d11` frame directly to the PNG encoder without
  `hwdownload`ing it first, producing "Impossible to convert between the formats supported by the
  filter" and silently returning 0 monitors. Caught by actually running the scan against real
  hardware, not from reading ffmpeg's docs — see the comment in `Services/MonitorScanner.cs`.
- After the fix: **Scan Monitors found all 3 real/virtual outputs** on the dev machine (3440x1440
  primary, 1920x1080 secondary, 800x600 virtual) with real, distinct captured thumbnails,
  screenshotted and visually confirmed.
- **Full Start Bridge flow verified end to end**: selected a monitor via simulated mouse+keyboard
  input, clicked Start Bridge, confirmed `PcHost.exe` actually launched as a real child process,
  triggered a real client handshake against it (`tests/mock_udp_receiver.py`), confirmed a real
  session was accepted and 345 frames streamed with 0 incomplete (ffprobe-validated H264 output),
  watched the real log output appear in the GUI's log pane, then clicked Stop Bridge and confirmed
  the child process was actually terminated.
- Settings persistence confirmed by inspecting the actual written
  `%AppData%\XrealBridge\gui_settings.json` after a run.
- `PcHostGui.Tests` (10 tests) covers `GridConfig`'s pure logic (grid-spec generation matching
  pc-host's exact syntax, row/column add/remove bounds, completeness checking) — run via
  `dotnet test PcHostGui.Tests/PcHostGui.Tests.csproj`.

Screenshotting itself required two fixes worth knowing if you automate this app further:
`SetForegroundWindow` can be silently blocked by Windows' focus-stealing prevention depending on
which process last had focus (use `PrintWindow` with `PW_RENDERFULLCONTENT` instead — it captures
a window's content directly by handle regardless of Z-order/focus), and a plain screen-region
`CopyFromScreen` will happily capture whatever unrelated window is on top at those coordinates if
you don't reassert focus immediately beforehand — always verify the captured window's title
before trusting a screenshot.

## Known limitations

- Virtual monitor config only supports one uniform size/refresh rate for all requested virtual
  monitors, not per-monitor customization.
- No "Identify" overlay (flashing a number on each physical monitor) — the scan thumbnails serve
  the same purpose (visually confirm which output_idx is which screen) without needing one.
- `BridgeStatusText` doesn't currently reflect *what* the bridge is doing beyond
  running/stopped/exited (e.g. no live "client connected" indicator distinct from "process
  running") — the log pane is the source of truth for that today.
