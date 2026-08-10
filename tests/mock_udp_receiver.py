#!/usr/bin/env python3
"""
Standalone test tool for the PC Host, implementing just enough of
shared-protocol/PROTOCOL.md to drive/observe it from the "iOS client" side without needing the
real iOS app.

Usage examples:
  # Pure mock-mode smoke test: PcHost auto-starts a session in --mock mode and streams
  # synthetic frames straight to 127.0.0.1:9001 with no handshake needed. Just listen:
  python tests/mock_udp_receiver.py --listen-only --duration 8

  # Real-capture smoke test: send a Handshake to trigger a session, then listen for real
  # H264 fragments and write the first N reassembled frames to a file for ffprobe.
  python tests/mock_udp_receiver.py --handshake --duration 10 --write-h264 test_capture.h264 --max-frames 50

  # Input path smoke test: also fire a few InputEvent packets at port 9002.
  python tests/mock_udp_receiver.py --handshake --send-input --duration 5
"""

import argparse
import socket
import struct
import sys
import time
import uuid

MAGIC0 = 0xAF
MAGIC1 = 0x51
VERSION = 1

PT_HANDSHAKE = 0
PT_HANDSHAKE_ACK = 1
PT_HEARTBEAT = 2
PT_DISCONNECT = 3
PT_VIDEO_FRAME_FRAGMENT = 10
PT_INPUT_EVENT = 20

IET_MOUSE_MOVE = 0
IET_SCROLL = 1
IET_LEFT_CLICK = 2
IET_RIGHT_CLICK = 3
IET_LEFT_DOWN = 4
IET_LEFT_UP = 5
IET_KEY_DOWN = 6
IET_KEY_UP = 7


def header(packet_type: int) -> bytes:
    return bytes([MAGIC0, MAGIC1, VERSION, packet_type])


def build_handshake(width, height, fps, codec_mask=0b01):
    nonce = uuid.uuid4().bytes  # 16 random bytes
    body = struct.pack("<HHHBB", 1, width, height, fps, codec_mask) + nonce
    return header(PT_HANDSHAKE) + body


def parse_handshake_ack(data: bytes):
    if len(data) < 32:
        return None
    if data[0] != MAGIC0 or data[1] != MAGIC1 or data[2] != VERSION or data[3] != PT_HANDSHAKE_ACK:
        return None
    server_version, width, height, fps, codec = struct.unpack("<HHHBB", data[4:12])
    nonce = data[12:28]
    (session_id,) = struct.unpack("<I", data[28:32])
    return {
        "serverVersion": server_version,
        "width": width,
        "height": height,
        "fps": fps,
        "codec": codec,
        "nonce": nonce,
        "sessionId": session_id,
    }


def build_heartbeat(session_id: int) -> bytes:
    return header(PT_HEARTBEAT) + struct.pack("<I", session_id)


def build_input_event(session_id: int, event_type: int, dx=0.0, dy=0.0, key_code=0) -> bytes:
    return header(PT_INPUT_EVENT) + struct.pack("<IBffH", session_id, event_type, dx, dy, key_code)


def parse_video_fragment(data: bytes):
    if len(data) < 27:
        return None
    if data[0] != MAGIC0 or data[1] != MAGIC1 or data[2] != VERSION or data[3] != PT_VIDEO_FRAME_FRAGMENT:
        return None
    session_id, frame_id, frag_index, frag_count = struct.unpack("<IIHH", data[4:16])
    (pts_micros,) = struct.unpack("<Q", data[16:24])
    flags = data[24]
    (payload_len,) = struct.unpack("<H", data[25:27])
    payload = data[27:27 + payload_len]
    if len(payload) != payload_len:
        return None
    return {
        "sessionId": session_id,
        "frameId": frame_id,
        "fragmentIndex": frag_index,
        "fragmentCount": frag_count,
        "ptsMicros": pts_micros,
        "isKeyframe": bool(flags & 0x1),
        "isLast": bool(flags & 0x2),
        "payload": payload,
        "datagramSize": len(data),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pc-host", default="127.0.0.1", help="PC host address")
    parser.add_argument("--control-port", type=int, default=9000)
    parser.add_argument("--video-port", type=int, default=9001)
    parser.add_argument("--input-port", type=int, default=9002)
    parser.add_argument("--duration", type=float, default=8.0, help="seconds to listen")
    parser.add_argument("--handshake", action="store_true", help="send a Handshake to trigger a session before listening")
    parser.add_argument("--listen-only", action="store_true", help="skip handshake, just listen on the video port (for --mock auto-session)")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fps", type=int, default=60)
    parser.add_argument("--send-input", action="store_true", help="also fire a few InputEvent packets")
    parser.add_argument("--heartbeat", action="store_true", help="send a Heartbeat every 1s to keep the session alive for the full --duration")
    parser.add_argument("--write-h264", metavar="PATH", help="write the first N reassembled frame payloads (Annex-B) to this file")
    parser.add_argument("--max-frames", type=int, default=50)
    args = parser.parse_args()

    video_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    video_sock.bind(("0.0.0.0", args.video_port))
    video_sock.settimeout(0.5)

    session_id = None

    if args.handshake and not args.listen_only:
        ctrl_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        ctrl_sock.bind(("0.0.0.0", 0))
        ctrl_sock.settimeout(3.0)
        pkt = build_handshake(args.width, args.height, args.fps)
        ctrl_sock.sendto(pkt, (args.pc_host, args.control_port))
        print(f"[handshake] sent Handshake {args.width}x{args.height}@{args.fps} to {args.pc_host}:{args.control_port}")
        try:
            data, _ = ctrl_sock.recvfrom(2048)
            ack = parse_handshake_ack(data)
            if ack:
                session_id = ack["sessionId"]
                print(f"[handshake] got HandshakeAck: {ack}")
            else:
                print("[handshake] received datagram that did not parse as HandshakeAck:", data[:16])
        except socket.timeout:
            print("[handshake] WARNING: no HandshakeAck received within timeout")
        ctrl_sock.close()

    if args.send_input and session_id is not None:
        input_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        events = [
            build_input_event(session_id, IET_MOUSE_MOVE, dx=5.0, dy=-3.0),
            build_input_event(session_id, IET_MOUSE_MOVE, dx=-2.0, dy=1.0),
            build_input_event(session_id, IET_SCROLL, dy=120.0),
            build_input_event(session_id, IET_LEFT_CLICK),
        ]
        for e in events:
            input_sock.sendto(e, (args.pc_host, args.input_port))
        print(f"[input] sent {len(events)} InputEvent packets to {args.pc_host}:{args.input_port}")
        input_sock.close()

    hb_stop = None
    if args.heartbeat and session_id is not None:
        import threading

        hb_stop = threading.Event()

        def _hb_loop():
            hb_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            while not hb_stop.is_set():
                hb_sock.sendto(build_heartbeat(session_id), (args.pc_host, args.control_port))
                hb_stop.wait(1.0)
            hb_sock.close()

        threading.Thread(target=_hb_loop, daemon=True).start()
        print(f"[heartbeat] sending Heartbeat every 1s for session {session_id}")

    print(f"[video] listening on UDP {args.video_port} for {args.duration}s ...")

    frames = {}  # frameId -> {index: payload}
    frame_meta = {}  # frameId -> parsed fragment meta (last one seen)
    total_datagrams = 0
    total_bytes = 0
    session_ids_seen = set()
    written_frames = 0
    completed_frame_count = 0
    out_file = open(args.write_h264, "wb") if args.write_h264 else None

    deadline = time.time() + args.duration
    try:
        while time.time() < deadline:
            try:
                data, addr = video_sock.recvfrom(4096)
            except socket.timeout:
                continue

            frag = parse_video_fragment(data)
            if frag is None:
                print(f"[video] WARNING: unparseable datagram from {addr}, len={len(data)}")
                continue

            total_datagrams += 1
            total_bytes += frag["datagramSize"]
            session_ids_seen.add(frag["sessionId"])

            fid = frag["frameId"]
            frames.setdefault(fid, {})[frag["fragmentIndex"]] = frag["payload"]
            frame_meta[fid] = frag

            if len(frames[fid]) == frag["fragmentCount"]:
                # Frame fully reassembled.
                ordered = b"".join(frames[fid][i] for i in range(frag["fragmentCount"]))
                expected_total = sum(len(p) for p in frames[fid].values())
                assert len(ordered) == expected_total
                completed_frame_count += 1
                if out_file and written_frames < args.max_frames:
                    out_file.write(ordered)
                    written_frames += 1
                del frames[fid]

    finally:
        if hb_stop:
            hb_stop.set()
        if out_file:
            out_file.close()
        video_sock.close()

    print()
    print("=== Summary ===")
    print(f"total datagrams received : {total_datagrams}")
    print(f"total bytes received     : {total_bytes}")
    print(f"distinct session ids seen: {sorted(session_ids_seen)}")
    print(f"frames fully reassembled : {completed_frame_count}")
    print(f"frames left incomplete   : {len(frames)} (frameIds: {list(frames.keys())[:10]})")
    if args.write_h264:
        print(f"wrote {written_frames} reassembled frames to {args.write_h264}")

    if total_datagrams == 0:
        print("FAIL: no video datagrams received at all")
        sys.exit(1)


if __name__ == "__main__":
    main()
