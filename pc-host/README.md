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
  The auto-started `--mock` session is exempt from this timeout, since it has no real client
  sending it heartbeats.
- `Disconnect` tears the session down immediately.

### Resolution negotiation

Per the task's MVP scope, the host accepts a client's requested resolution/fps only if it's
exactly one of: 1920x1080@60, 1920x1200@60, or a ~16:18 aspect vertical mode at 60fps. Any other
request falls back to 1920x1080@60 H264 -- an ack is always sent (never silently dropped), so
every Handshake results in a usable session.

## Real capture pipeline

For a real (non-mock) session, the host launches:

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
