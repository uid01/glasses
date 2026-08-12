# PC Host

Windows console app that captures the desktop, encodes it to H264, and streams it to the iOS
client over UDP, while relaying touch input from the client back into real Windows mouse/keyboard
events. Implements the PC side of `shared-protocol/PROTOCOL.md` byte-for-byte.

## Prerequisites

- Windows, .NET 8 SDK (`dotnet --version` should report 8.x).
- `ffmpeg` on `PATH`, built with NVENC + `ddagrab` support (desktop capture). Only needed for real
  (non-`--mock`) capture; `--mock` mode has zero external dependencies.
- An NVIDIA GPU is not required. If present, NVENC is used automatically when the installed
  driver is new enough for the ffmpeg build's NVENC API requirement; otherwise the app falls back
  to software encoding (`libx264`) transparently -- see "Encoder fallback" below.

## Build & test

```
cd pc-host
dotnet build PcHost.csproj                      # main app
dotnet test PcHost.Tests/PcHost.Tests.csproj     # unit tests
```

Both build with 0 warnings / 0 errors (`TreatWarningsAsErrors` is enabled on the main project).

## Running

```
dotnet run --project PcHost.csproj                       # real capture (needs ffmpeg)
dotnet run --project PcHost.csproj -- --mock              # synthetic frames, no ffmpeg needed
```

### CLI flags

| Flag | Default | Meaning |
|------|---------|---------|
| `--mock` | off | Use a synthetic frame generator instead of launching ffmpeg. |
| `--control-port <port>` | 9000 | Control channel (Handshake/Heartbeat/Disconnect), iOS -> PC. |
| `--video-port <port>` | 9001 | Video channel (VideoFrameFragment), PC -> iOS. |
| `--input-port <port>` | 9002 | Input channel (InputEvent), iOS -> PC. |
| `--mock-target-ip <ip>` | 127.0.0.1 | Destination for the auto-started `--mock` session (see below). |
| `--mock-target-port <port>` | = video-port | Destination port for the auto-started `--mock` session. |
| `--log-dir <path>` | `<exe dir>/logs` | Where per-session ffmpeg stderr logs are written. |
| `--monitors <grid>` | `0` | ddagrab `output_idx` values arranged as a grid: rows separated by `;`, columns by `,` (e.g. `0,1` = 1x2 strip, `0,1;2,3` = 2x2 grid). See "Multi-monitor capture" below. |
| `--tile-width <px>` | 1920 | Width each captured monitor is scaled to before tiling. |
| `--tile-height <px>` | 1080 | Height each captured monitor is scaled to before tiling. |
| `--gap-x <px>` | 0 | Solid black gutter between adjacent columns (no effect with 1 column). |
| `--gap-y <px>` | 0 | Solid black gutter between adjacent rows (no effect with 1 row). |

Ctrl+C shuts down cleanly: cancels all background loops, kills any running ffmpeg child
processes, and closes the UDP sockets.

### Mock mode

`--mock` is the zero-dependency automated-test path: on startup it immediately auto-starts a
session (id 1, 1920x1080@60) that streams synthetic frames (not valid H264, just a counter byte
pattern) through the exact same fragmentation/UDP-send code path real capture uses, targeting
`--mock-target-ip:--mock-target-port` (default `127.0.0.1:9001`). No Handshake is required for
this to start flowing. The Control (9000) and Input (9002) listeners are still fully live in
`--mock` mode too, so a real Handshake can additionally be exercised against the same process --
any session created that way also uses the mock frame source instead of ffmpeg.

### Session lifecycle

- A client sends `Handshake` to the control port; the host always responds with a
  `HandshakeAck` (see "Resolution negotiation" below), assigns a random `sessionId`, and starts
  a capture pipeline for that session.
- The client should send `Heartbeat` (or any `InputEvent`) at least once every 5 seconds, or the
  host tears the session down (kills its ffmpeg process, per PROTOCOL.md's idle-timeout rule).
- The host also proactively sends `Heartbeat` back to every active session's control endpoint
  once per second (`Network/ControlServer.cs`'s `SendHeartbeatsLoopAsync`). This was missed
  initially -- real-device testing showed the client reconnecting in a tight ~5-6s loop because
  the client's own watchdog only tracks Control-channel traffic it *receives*, and with the host
  never sending anything back after the initial `HandshakeAck`, that watchdog had nothing to see.
  Heartbeat is bidirectional per PROTOCOL.md; this now actually implements that.
  The auto-started `--mock` session is exempt from this timeout, since it has no real client
  sending it heartbeats.
- `Disconnect` tears the session down immediately.

### Resolution negotiation

The host is authoritative on resolution, not the client. The client's requested width/height/fps
in `Handshake` is parsed but not honored -- the `HandshakeAck` always reports the canvas size
implied by `--monitors`/`--tile-width`/`--tile-height` (see "Multi-monitor capture" below), and
fps is fixed at 60. An ack is always sent (never silently dropped), so every Handshake results in
a usable session. This is a deliberate design choice, not a limitation to fix later: "how many
screens and what layout" is meant to be a host-side decision (the PC drives monitor
creation/layout; the client is just a display+input gateway), not something the client requests.

## Multi-monitor capture

The XREAL 1S's onboard X1 chip already does 3DoF head-tracking-driven panning across whatever
video signal it's fed, entirely on its own hardware, independent of the source -- this is how
XREAL's own Nebula software achieves its ultrawide/multi-monitor "follow mode" experience. So
getting a multi-monitor experience through the glasses doesn't require this host to do any
head-tracking or reprojection itself: it only needs to hand the glasses a wide enough canvas, and
the glasses pan across it on their own.

`--monitors <grid>` picks which real DXGI outputs (ddagrab's `output_idx`, 0-based) to capture,
arranged as a grid: rows separated by `;`, columns within a row by `,` -- e.g. `0,1` is a 1x2
horizontal strip, `0,1;2,3` is a 2x2 grid (0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right).
Every row must have the same number of columns. Each captured output is scaled to
`--tile-width x --tile-height` regardless of its native resolution/aspect ratio (a non-16:9
monitor will look slightly stretched, which was visually confirmed acceptable when testing with a
3440x1440 ultrawide primary scaled into a 1920x1080 tile), each row is tiled via ffmpeg's `hstack`
filter, and the rows are stacked via `vstack` (skipped entirely for a single row/column -- see
`BuildFilterComplex`). Bitrate scales with total tile count (8Mbps per tile, same ratio the
original single-monitor default used) so a bigger canvas isn't starved of bits relative to how
much more picture content it actually has.

`--gap-x <px>` / `--gap-y <px>` insert a solid black gutter between adjacent columns / rows
(implemented as extra `color=black` lavfi inputs interleaved into the `hstack`/`vstack` chains),
so the glasses show a visible break between monitors instead of one seamless edge-to-edge image,
closer to how physical monitor bezels read as separate screens.

Run once with a single `--monitors` index first to confirm which `output_idx` corresponds to
which physical monitor before combining them (verified empirically on the dev machine:
`output_idx=0` was the primary monitor, `output_idx=1` the secondary -- matched Windows'
enumeration order, but this isn't guaranteed and is worth confirming on any given machine).

Verified end-to-end on real hardware:
- 2 real monitors horizontally (`--monitors 0,1`, a 3440x1440 primary and 1920x1080 secondary
  tiled into a 3840x1080 canvas, and separately with `--gap-x 60` into 3900x1080): `HandshakeAck`
  correctly reported the canvas size, frames reassembled with 0 incomplete, ffprobe confirmed a
  valid decodable H264 stream at the exact expected dimensions, and a black gutter was visually
  confirmed present between tiles when `--gap-x` was set (extracted and inspected preview frames).
- 2 real monitors vertically (`--monitors "0;1"` with `--gap-y 20`, a 2x1 grid): same
  end-to-end verification, plus a preview frame confirmed the primary monitor's content on top,
  secondary on bottom, with a visible horizontal gutter between them -- proving `vstack` and the
  vertical gap path both work, not just `hstack`.

### Virtual monitors

Real monitors aren't the only option: [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
(MIT licensed, signed via SignPath Foundation) creates additional DXGI outputs Windows treats as
ordinary monitors -- installed and verified working on the dev machine (`winget install
--id=VirtualDrivers.Virtual-Display-Driver -e`, then run `VDD Control.exe` as Administrator, or
use `devcon install <path to MttVDD.inf> Root\MttVDD` from an elevated prompt). Once installed, it
shows up in Device Manager as "Virtual Display Driver" and Windows enumerates it as a normal
display (confirmed: a new `\\.\DISPLAYn` appeared at whatever resolution `C:\VirtualDisplayDriver\vdd_settings.xml`'s
`<monitors><count>` + `<resolutions>` specify). **The capture side here needed zero code changes
for this** -- ddagrab enumerates virtual outputs exactly like real ones, confirmed by capturing
the virtual monitor directly (`--monitors 2` after a 2-real-monitor setup picked it up immediately,
ffprobe-validated). Changing the virtual monitor count/resolutions requires editing that XML and
reloading the driver (Device Manager disable/enable, or the VDC app's restart button) -- both need
Administrator privileges.

## GPU 3D compositor (curved monitors, arbitrary placement)

The grid pipeline above ties every monitor to a rectangular cell in a shared grid, all cells the
same size, no rotation, no curvature. `--render` switches to an alternate pipeline (`PcHost.Render`)
that instead captures each monitor the same way (DXGI Desktop Duplication) but composites them
itself on the GPU as textured meshes -- flat or cylindrically curved, placed and rotated anywhere
in 3D, viewed through a virtual camera -- and hands ffmpeg only the final rendered frame to encode
(`-f rawvideo` over stdin, no `ddagrab`/`filter_complex` involved on this path).

Single-panel form, matching what today's CLI flags can express directly:

```
pc-host --render --render-monitor 0 --render-width 1920 --render-height 1080 --render-curvature 30
```

For more than one panel, or non-default position/rotation, use `--scene-file <path.json>` instead
(supersedes `--render`'s flags if both are given) -- a JSON file describing an arbitrary number of
objects, each with its own source monitor, size, curvature, position, and yaw/pitch/roll (degrees;
converted to the radians the renderer actually uses at load time). See
`pc-host/Render/SceneFileFormat.cs` for the exact shape. `pc-host-gui`'s "3D Scene" tab is a
drag-drop builder that writes exactly this format and launches pc-host with it.

The camera is currently driven by WASD + arrow keys read from the console (`ConsoleCameraController`)
-- a manual stand-in until the XREAL Eye's onboard 6DoF pose is wired in (`Camera` is the intended
plug-in point for that; see its doc comment). When pc-host has no interactive console attached to
stdin (e.g. launched as a subprocess by `pc-host-gui`), camera polling silently disables itself
after the first failed check rather than crashing the render loop.

Verified end-to-end on real hardware: real `Handshake`/`HandshakeAck`, frames rendered and
reassembled correctly (0 incomplete) for both a single flat panel and a curved+rotated+off-center
panel driven by `--scene-file`, and `ffprobe`-validated as correct H264 at the requested resolution.
Also verified via `pc-host-gui`: scanned real monitors, dragged a panel to a new position in the
scene builder, assigned it a real source, started the bridge, and confirmed pc-host streamed from
the resulting scene file.

## Real capture pipeline

For a real (non-mock) session, the host launches (shown for a single monitor; `--monitors 0,1`
adds a second `-f lavfi -i ddagrab=...` input and an `hstack` step in `-filter_complex` -- see
`Capture/FfmpegCaptureSource.cs`'s `BuildFilterComplex`):

```
ffmpeg -hide_banner -loglevel warning -f lavfi -i ddagrab=framerate=<fps> \
  -vf "hwdownload,format=bgra,scale=<w>:<h>,format=yuv420p" \
  -c:v <libx264|h264_nvenc> ... -x264-params "aud=1" (or -aud 1 for nvenc) \
  -profile:v baseline -g <fps*2> -b:v 8M -maxrate 8M -bufsize 4M -f h264 -
```

`ddagrab` captures the desktop via DXGI Desktop Duplication (GPU-side). The encoded Annex-B H264
stream is read from ffmpeg's stdout, split into access units (frames), and each frame is
fragmented into <=1173-byte payloads and sent as `VideoFrameFragment` packets.

Stderr is drained continuously on a background task to a per-session log file under
`--log-dir` (`ffmpeg-session-<id>.log`) so a full pipe can never deadlock ffmpeg; lines containing
"error" are also echoed to the console.

### Encoder fallback (NVENC -> libx264)

The app probes once at startup whether `h264_nvenc` actually works (`Capture/EncoderProbe.cs`) and
uses it when available, otherwise falls back to software `libx264`. **On the development machine
this was built and tested on (RTX 5070 Ti, driver 595.95), NVENC does NOT work** -- ffmpeg 8.1.2's
nvenc wrapper requires driver >= 610.00 ("Driver does not support the required nvenc API version.
Required: 13.1 Found: 13.0"), and the probe correctly detects this and falls back to `libx264
-preset ultrafast -tune zerolatency`, which was verified end-to-end (see "Verification" below).
Desktop capture itself (`ddagrab`/DXGI) is unaffected by this and stays GPU-side either way -- only
the encode stage falls back to CPU. The NVENC code path is implemented per standard ffmpeg usage
but has not been exercised on real hardware in this environment; it will engage automatically on
a machine with a new enough driver.

### Access Unit Delimiters are required, not optional

Both encoder paths are launched with `-x264-params "aud=1"` / `-aud 1`. This was discovered to be
necessary, not cosmetic: under a zero-latency tune, both libx264 and (per ffmpeg's nvenc option
docs) nvenc use slice-based threading to parallelize a single picture across CPU/GPU execution
units without adding frame-pipeline latency. On this machine that meant **every picture, including
IDRs, was split into 17 separate slice NAL units, all sharing the same `nal_unit_type`** --
verified via manual NAL-level analysis of real ffmpeg output. A naive "one VCL NAL = one frame"
splitter (the original implementation) fragmented each real picture into 17 bogus "frames",
inflating the apparent frame rate ~17x and producing corrupt, non-decodable access units.
`Capture/AnnexBFrameSplitter.cs` now treats AUD (NAL type 9) as the authoritative per-picture
boundary once any AUD has been observed on the stream, falling back to the old VCL-transition
heuristic only for the (unexpected) case of a stream that never emits AUDs at all. See the code
comments in that file and the regression test
`PcHost.Tests/AnnexBSplitterTests.cs::GroupsMultiSliceIdrPictureIntoOneFrameWhenAudsArePresent`.

## Input injection

`InputEvent` packets from the client are translated to real Windows input via `user32.dll`
`SendInput` (`Input/InputInjector.cs`): `MouseMove` -> relative `MOUSEEVENTF_MOVE`, `Scroll` ->
`MOUSEEVENTF_WHEEL`/`MOUSEEVENTF_HWHEEL`, `LeftClick`/`RightClick` -> down+up pairs,
`LeftDown`/`LeftUp` -> individual events (for drag support), `KeyDown`/`KeyUp` -> `INPUT_KEYBOARD`
with the given virtual key code. Extended-key handling (e.g. numpad/arrow key
`KEYEVENTF_EXTENDEDKEY` nuances) is not implemented -- out of scope for this MVP.

The wire format has no per-event sequence number, so true packet-loss/gap detection isn't
possible from the payload alone; the host instead logs periodic input throughput and counts
events addressed to an unknown/expired session as a proxy for the most common loss symptom.

## Known protocol-doc discrepancy (flagged, not silently "fixed")

`shared-protocol/PROTOCOL.md`'s `Disconnect` packet lists `reason` at offset 5, which overlaps
the 4-byte `sessionId` field at offset 4 (spanning bytes 4-7). This implementation places `reason`
at offset 8 instead (immediately after `sessionId`, matching the non-overlapping layout used by
every other packet in the doc) since the literal offset can't be implemented without clobbering
`sessionId`. See the doc comment on `Protocol/Disconnect.cs` for details. This should be confirmed
against whatever the iOS client actually implements -- `Disconnect` is not on the hot path (the 5s
heartbeat timeout is the real backstop), so a mismatch here is lower-risk than one in
Handshake/VideoFrameFragment/InputEvent, but it's still worth reconciling in PROTOCOL.md.

## Project layout

```
pc-host/
  PcHost.csproj              main app (net8.0-windows)
  Program.cs                 CLI parsing, wiring, shutdown
  Protocol/                  wire format: header + all 6 packet types (PROTOCOL.md)
  Session/                   SessionInfo, SessionManager (5s timeout sweep)
  Network/                   ControlServer, InputServer, VideoSender (UDP listeners/senders)
  Capture/                   AnnexBFrameSplitter, FrameFragmenter, FfmpegCaptureSource,
                              EncoderProbe, MockFrameSource, CapturePipeline
  Input/                     InputInjector (SendInput P/Invoke)
  PcHost.Tests/               xUnit tests: packet round-trips, Annex-B splitting, fragmentation math
```

## Verification performed on real hardware

- `dotnet build PcHost.csproj`: 0 warnings, 0 errors.
- `dotnet test PcHost.Tests/PcHost.Tests.csproj`: 36/36 passed.
- `dotnet run -- --mock` against `tests/mock_udp_receiver.py --listen-only`: fragments arrive and
  reassemble correctly (verified byte counts and frame counts over an 8s window).
- Real capture: triggered a session via `tests/mock_udp_receiver.py --handshake --heartbeat`,
  confirmed ffmpeg launches (libx264 fallback, since NVENC isn't usable on this driver), reassembled
  50 real frames to a file, and `ffprobe` confirmed a valid decodable H264 stream at the
  requested 1920x1080, `yuv420p`, `Constrained Baseline` profile, with exactly one AUD per
  reassembled frame and every picture's slices grouped correctly.

## Known limitations / future work

- One ffmpeg process per session; concurrent multi-client sessions each run their own independent
  desktop capture rather than sharing one encode -- fine for the expected single-client use case,
  wasteful if ever extended to multiple simultaneous viewers.
- HEVC (`codecMask` bit 1 / `chosenCodec` 1) is not implemented; the host always answers H264.
- The desktop is captured at its native resolution and scaled to the client's chosen resolution
  with a plain `scale` filter, which stretches the image if the aspect ratios differ.
- No adaptive bitrate; a fixed 8 Mbps cap (`-b:v 8M -maxrate 8M -bufsize 4M`) is used for both
  encoder paths.
