import socket
import struct
import time
import argparse
import threading
from pynput import keyboard

import cv2
import numpy as np

# python udp_client_viewer.py --ip 192.168.1.149 --video-port 5000 --control-port 5001 --w 360 --h 240 --q 70 --fps 10

# -----------------------------
# UDP client configuration
# -----------------------------
SERVER_IP = "192.168.1.149" # Change this to your server's IP address.
SERVER_VIDEO_PORT = 5000
SERVER_CONTROL_PORT = 5001
WINDOW_NAME = "UDP JPEG Snapshot"

# Request/camera parameters
DEFAULT_W = 720
DEFAULT_H = 480
DEFAULT_Q = 10
DEFAULT_FPS = 10

# UDP socket/reassembly settings
RECV_MAX = 65535
SOCKET_TIMEOUT_S = 1.0
CHUNK_HEADER_FORMAT = "<IHHH"
CHUNK_HEADER_SIZE = struct.calcsize(CHUNK_HEADER_FORMAT)
FRAME_ASSEMBLY_TIMEOUT_S = 1.0 #higher values may increase latency but reduce frame drops in bad network conditions
MAX_INFLIGHT_FRAMES = 32
CONTROL_SEND_HZ = 30.0

KEY_W = "w"
KEY_A = "a"
KEY_S = "s"
KEY_D = "d"
KEY_X = "x"
KEY_R = "r"
KEY_Q = "q"
KEY_SPACE = "space"

KEYBOARD_CHAR_MAP = {
    "w": KEY_W,
    "a": KEY_A,
    "s": KEY_S,
    "d": KEY_D,
    "x": KEY_X,
    "r": KEY_R,
    "q": KEY_Q,
}


_KEYS_LOCK = threading.Lock()
_PRESSED_KEYS = set()


def _normalize_key(key) -> str | None:
    if key == keyboard.Key.space:
        return KEY_SPACE

    char = getattr(key, "char", None)
    if char is None:
        return None

    return KEYBOARD_CHAR_MAP.get(char.lower())


def _on_key_press(key):
    mapped = _normalize_key(key)
    if mapped is not None: # Not in our mapping -> ignore
        with _KEYS_LOCK:
            _PRESSED_KEYS.add(mapped)


def _on_key_release(key):
    mapped = _normalize_key(key)
    if mapped is not None: # Not in our mapping -> ignore
        with _KEYS_LOCK:
            _PRESSED_KEYS.discard(mapped)


def start_keyboard_listener():
    listener = keyboard.Listener(on_press=_on_key_press, on_release=_on_key_release)
    listener.start()
    return listener


def stop_keyboard_listener(listener):
    listener.stop()
    with _KEYS_LOCK:
        _PRESSED_KEYS.clear()


def is_key_down(key_name: str) -> bool:
    with _KEYS_LOCK:
        return key_name in _PRESSED_KEYS


def parse_args():
    parser = argparse.ArgumentParser(description="UDP JPEG snapshot client")
    parser.add_argument("--ip", default=SERVER_IP, help="Server IP address")
    parser.add_argument("--video-port", type=int, default=SERVER_VIDEO_PORT, help="Server UDP video port")
    parser.add_argument("--control-port", type=int, default=SERVER_CONTROL_PORT, help="Server UDP control port")
    parser.add_argument("--w", type=int, default=DEFAULT_W, help="Requested width")
    parser.add_argument("--h", type=int, default=DEFAULT_H, help="Requested height")
    parser.add_argument("--q", type=int, default=DEFAULT_Q, help="JPEG quality (1-100)")
    parser.add_argument("--fps", type=int, default=DEFAULT_FPS, help="Requested FPS")
    return parser.parse_args()


def consume_datagram(data: bytes, frames: dict, now: float):
    if data == b"NO_FRAME":
        return "NO_FRAME"

    if len(data) < CHUNK_HEADER_SIZE:
        return None

    try:
        frame_id, chunk_id, chunk_count, payload_len = struct.unpack(
            CHUNK_HEADER_FORMAT,
            data[:CHUNK_HEADER_SIZE],
        )
    except struct.error:
        return None

    if chunk_count == 0 or chunk_id >= chunk_count:
        return None

    payload = data[CHUNK_HEADER_SIZE:CHUNK_HEADER_SIZE + payload_len]
    if len(payload) != payload_len:
        return None

    # If this is the FIRST TIME we see this frame_id, create a new entry
    if frame_id not in frames:
        # Memory cleanup: if there are too many incomplete frames (>32),
        # remove the oldest one to avoid RAM saturation
        if len(frames) >= MAX_INFLIGHT_FRAMES:
            oldest = min(frames.items(), key=lambda item: item[1]["first_seen"])[0]
            del frames[oldest]

        # Create structure to store the fragments of this frame
        frames[frame_id] = {
            "first_seen": now,           # arrival timestamp
            "chunk_count": chunk_count, # total expected fragments
            "chunks": {},               # dictionary to store fragments
        }

    # Get the info of the frame we are processing
    info = frames[frame_id]

    # If we receive fragments with different chunk_count for the same frame_id,
    # it means the data is corrupted -> discard the entire frame
    if info["chunk_count"] != chunk_count:
        del frames[frame_id]
        return None

    # Store the fragment in its position (only if we don't already have it)
    # Fragments may arrive out of order, so they are stored by chunk_id
    if chunk_id not in info["chunks"]:
        info["chunks"][chunk_id] = payload


    # Rebuild image if we have all the fragments (chunk_count)
    if len(info["chunks"]) == info["chunk_count"]:
        assembled = b"".join(info["chunks"][i] for i in range(info["chunk_count"]))
        
        del frames[frame_id]
        
        return assembled

    return None


def receive_frame_bytes(sock, stop_event: threading.Event, inflight_frames: dict):
    receive_deadline = time.time() + SOCKET_TIMEOUT_S

    while time.time() < receive_deadline and not stop_event.is_set():
        now = time.time()
        expired_ids = [
            frame_id
            for frame_id, info in inflight_frames.items()
            if (now - info["first_seen"]) > FRAME_ASSEMBLY_TIMEOUT_S
        ]
        for frame_id in expired_ids:
            del inflight_frames[frame_id]

        timeout_left = max(0.01, receive_deadline - time.time())
        sock.settimeout(timeout_left)

        try:
            data, _ = sock.recvfrom(RECV_MAX)
        except socket.timeout:
            return None
        except OSError:
            return None

        #Will return None unless we recive all the chunks of a frame, in which case it will return the reassembled bytes of that frame
        result = consume_datagram(data, inflight_frames, time.time())
        if result == "NO_FRAME":
            return None
        if isinstance(result, bytes):
            return result
    # Probably timeout
    return None


def control_loop(stop_event: threading.Event, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)
    send_hz = max(1.0, CONTROL_SEND_HZ)
    send_period = 1.0 / send_hz
    prev_r_down = False

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(SOCKET_TIMEOUT_S)

    try:
        while not stop_event.is_set():
            lvx = 0.0
            lvy = 0.0
            lvz = 0.0
            avy = 0.0
            avz = 0.0
            reset = 0

            r_down = is_key_down(KEY_R)
            if r_down and not prev_r_down:
                reset = 1
            prev_r_down = r_down

            # Sum movement vectors to allow simultaneous key presses (Press signal)
            if is_key_down(KEY_W):
                lvx += 1.0
            if is_key_down(KEY_S):
                lvx -= 1.0
            if is_key_down(KEY_A):
                avz -= 1.0
            if is_key_down(KEY_D):
                avz += 1.0
            if is_key_down(KEY_SPACE):
                lvz += 1.0
                avy += 1.0

            payload = (
                f"snapshot?w={req_w};h={req_h};q={req_q};fps={req_fps};"
                f"lvx={lvx};lvy={lvy};lvz={lvz};avy={avy};avz={avz};r={reset}"
            ).encode("utf-8")

            try:
                sock.sendto(payload, (args.ip, args.control_port))
            except OSError as e:
                print( f"sendto control failed : {e}")
                pass

            stop_event.wait(send_period)
    finally:
        sock.close()


def video_loop(stop_event: threading.Event, state: dict, state_lock: threading.Lock, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)

    request_payload = f"snapshot?w={req_w};h={req_h};q={req_q};fps={req_fps}".encode("utf-8")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(SOCKET_TIMEOUT_S)

    inflight_frames = {}
    frame_count = 0
    fps_timer = time.time()
    loop_sleep = 1.0 / req_fps

    try:
        while not stop_event.is_set():
            # 1) Send request
            try:
                sock.sendto(request_payload, (args.ip, args.video_port))
            except OSError:
                with state_lock:
                    state["status_text"] = "sendto failed"
                stop_event.wait(loop_sleep)
                continue

            # 2) Receive response datagram(s) and reassemble frame if needed
            frame_bytes = receive_frame_bytes(sock, stop_event, inflight_frames)

            if frame_bytes is None:
                with state_lock:
                    state["status_text"] = "No frame"
                stop_event.wait(loop_sleep)
                continue

            # 3) Decode JPEG -> image
            arr = np.frombuffer(frame_bytes, dtype=np.uint8)
            img = cv2.imdecode(arr, cv2.IMREAD_COLOR)

            if img is None:
                with state_lock:
                    state["status_text"] = f"Decode failed ({len(frame_bytes)} bytes)"
                stop_event.wait(loop_sleep)
                continue

            frame_count += 1
            now = time.time()
            elapsed = now - fps_timer
            fps = 0.0
            if elapsed >= 1.0:
                fps = frame_count / elapsed
                frame_count = 0
                fps_timer = now

            with state_lock:
                state["latest_img"] = img
                state["last_frame_size"] = len(frame_bytes)
                if fps > 0.0:
                    state["video_fps"] = fps
                state["status_text"] = "Streaming"

            # 4) UI events are handled in main thread; keep loop cadence here
            stop_event.wait(loop_sleep)
    finally:
        sock.close()


def main():
    args = parse_args()
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)
    print(f"Video UDP:   {args.ip}:{args.video_port}")
    print(f"Control UDP: {args.ip}:{args.control_port}")
    print(f"Request params: w={req_w}, h={req_h}, q={req_q}, fps={req_fps}")
    print("Hold W/A/S/D/SPACE to move, tap R to reset, tap Q to quit.")

    cv2.namedWindow(WINDOW_NAME, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(WINDOW_NAME, req_w, req_h)
    state_lock = threading.Lock()
    state = {
        "latest_img": None,
        "last_frame_size": 0,
        "video_fps": 0.0,
        "status_text": "Waiting for first frame...",
    }
    stop_event = threading.Event()
    listener = start_keyboard_listener()

    video_thread = threading.Thread(target=video_loop, args=(stop_event, state, state_lock, args), daemon=True)
    control_thread = threading.Thread(target=control_loop, args=(stop_event, args), daemon=True)

    video_thread.start()
    control_thread.start()

    try:
        while True:
            img = None
            frame_size = 0
            measured_fps = 0.0
            status_text = "Waiting for first frame..."

            with state_lock:
                if state["latest_img"] is not None:
                    img = state["latest_img"].copy()
                frame_size = state["last_frame_size"]
                measured_fps = state["video_fps"]
                status_text = state["status_text"]

            if img is None:
                img = np.zeros((req_h, req_w, 3), dtype=np.uint8)

            overlay_font = cv2.FONT_HERSHEY_SIMPLEX
            cv2.putText(img, f"Status: {status_text}", (8, 20),
                        overlay_font, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(img, f"Size: {frame_size:,} B", (8, 44),
                        overlay_font, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(img, f"FPS: {measured_fps:.1f}", (8, 68),
                        overlay_font, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(img, f"Req {req_w}x{req_h} q={req_q} fps={req_fps}", (8, 92),
                        overlay_font, 0.55, (0, 0, 0), 1, cv2.LINE_AA)

            cv2.resizeWindow(WINDOW_NAME, req_w, req_h)
            cv2.imshow(WINDOW_NAME, img)

            cv2.waitKey(1)
            q_down = is_key_down(KEY_Q)
            if q_down:
                break

            time.sleep(1.0/req_fps)
    finally:
        stop_event.set()
        video_thread.join(timeout=1.0)
        control_thread.join(timeout=1.0)
        stop_keyboard_listener(listener)
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()