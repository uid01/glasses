# Wire Protocol — PC Host ↔ iOS Client

UDP, little-endian on the wire. Three logical channels, each its own UDP port so
video, input, and control traffic never head-of-line-block each other.

| Channel | Direction      | Default Port | Purpose                              |
|---------|----------------|-------------:|---------------------------------------|
| Control | iOS -> PC      | 9000         | Handshake, heartbeat, teardown        |
| Video   | PC -> iOS      | 9001         | Fragmented H264/HEVC Annex-B frames   |
| Input   | iOS -> PC      | 9002         | Pointer / scroll / click / key events |

All ports are configurable at runtime; 9000-9002 are just the defaults both
reference implementations ship with.

## Common datagram header (all packet types)

```
offset  size  field
0       2     magic         = 0xAF51 (big-endian on the wire: bytes 0xAF,0x51)
2       1     version       = 1
3       1     type          PacketType enum (below)
4       ...   payload       type-specific
```

Any datagram that doesn't start with the magic bytes or has an unknown version
is dropped silently (defensive against stray traffic on the port).

## PacketType

| Value | Name              | Channel  |
|------:|-------------------|----------|
| 0     | Handshake         | Control  |
| 1     | HandshakeAck      | Control  |
| 2     | Heartbeat         | Control  |
| 3     | Disconnect        | Control  |
| 10    | VideoFrameFragment| Video    |
| 20    | InputEvent        | Input    |

## Handshake (type 0, iOS -> PC, sent to Control port)

```
offset  size  field
4       2     clientProtocolVersion (uint16) = 1
6       2     desiredWidth  (uint16)   e.g. 1920
8       2     desiredHeight (uint16)   e.g. 1080  (or 1200, or custom 16:18 mode)
10      1     desiredFps    (uint8)    60 or 120
11      1     codecMask     (uint8)    bit0=H264, bit1=HEVC (client advertises what it can decode)
12      16    sessionNonce  (16 random bytes, client-generated, echoed back)
```

## HandshakeAck (type 1, PC -> iOS, sent to the source port of the Handshake)

```
offset  size  field
4       2     serverProtocolVersion (uint16) = 1
6       2     chosenWidth   (uint16)
8       2     chosenHeight  (uint16)
10      1     chosenFps     (uint8)
11      1     chosenCodec   (uint8)    0=H264, 1=HEVC
12      16    sessionNonce  (echoed from Handshake, ties ack to request)
28      4     sessionId     (uint32)   included in every subsequent VideoFrameFragment
```

If the PC host rejects the handshake (e.g. unsupported resolution) it simply
does not respond; the client retries with backoff and eventually falls back
to a default 1920x1080@60 H264 request.

## Heartbeat (type 2, either direction)

```
offset  size  field
4       4     sessionId (uint32)
```

Sent every 1s on an idle connection. Either side that hasn't seen a
Heartbeat, VideoFrameFragment, or InputEvent in 5s tears the session down
locally (no explicit ack required — UDP, so absence is the signal).

## Disconnect (type 3, either direction)

```
offset  size  field
4       4     sessionId (uint32)
8       1     reason (uint8)  0=user, 1=error, 2=shutdown
```

## VideoFrameFragment (type 10, PC -> iOS, Video port)

H264/HEVC Annex-B access units are almost always larger than a safe UDP
payload (~1200 bytes after headroom for IP/UDP/VPN overhead), so every frame
is split into fragments.

```
offset  size  field
4       4     sessionId       (uint32)
8       4     frameId         (uint32)   monotonically increasing per frame
12      2     fragmentIndex   (uint16)   0-based
14      2     fragmentCount   (uint16)   total fragments for this frameId
16      8     ptsMicros       (uint64)   presentation timestamp, microseconds since session start
24      1     flags           (uint8)    bit0=keyframe, bit1=lastFragmentOfFrame (redundant w/ fragmentIndex==fragmentCount-1, kept for cheap checks)
25      2     payloadLength   (uint16)   bytes of NAL data following
27      N     payload         raw Annex-B bytes for this fragment
```

Max UDP payload target: 1200 bytes total datagram size (safely under typical
1500 MTU minus IP/UDP/tunnel overhead), so fragment payload <= ~1173 bytes.

Client reassembly: buffer fragments by frameId; once fragmentCount fragments
are present, concatenate in fragmentIndex order and feed to VTDecompressionSession.
If a keyframe's fragments aren't fully received within one frame interval,
drop the partial frame and wait for the next keyframe request cycle rather
than feeding VideoToolbox a corrupt access unit. Non-keyframes that are
incomplete are dropped outright (never partially decoded) — a single dropped
inter frame just causes one skipped display update, which reads as fine at
60-120Hz; feeding a corrupt frame to VideoToolbox can wedge the session.

## InputEvent (type 20, iOS -> PC, Input port)

```
offset  size  field
4       4     sessionId  (uint32)
8       1     eventType  (uint8)   InputEventType enum (below)
9       4     dx         (float32) relative motion X, or scroll delta X
13      4     dy         (float32) relative motion Y, or scroll delta Y
17      2     keyCode    (uint16)  virtual key code, only meaningful for KeyDown/KeyUp
```

Fixed-size 19-byte payload for every InputEvent regardless of type — simpler
to parse than a variable-length union, and small enough that padding cost is
irrelevant on a LAN.

### InputEventType

| Value | Name         | Uses            |
|------:|--------------|-----------------|
| 0     | MouseMove    | dx, dy (relative pixels, screen-space of chosen resolution) |
| 1     | Scroll       | dx, dy (wheel delta) |
| 2     | LeftClick    | (none — press+release, momentary) |
| 3     | RightClick   | (none) |
| 4     | LeftDown     | (none — for future drag support) |
| 5     | LeftUp       | (none) |
| 6     | KeyDown      | keyCode |
| 7     | KeyUp        | keyCode |

Gesture -> event mapping is defined in the iOS client (1-finger drag =
MouseMove stream, 2-finger scroll = Scroll, 2-finger tap = RightClick, tap =
LeftClick momentary).

## Versioning

`version` byte in the common header bumps only on a wire-incompatible
change. Both reference implementations reject mismatched versions rather
than attempting best-effort parsing of an unknown layout.
