import socket
import struct
import time
import argparse
import threading
from pynput import keyboard

import numpy as np
import cv2

# python Tcp_client_viewer.py --ip 192.168.1.149 --video-port 5000 --control-port 5001 --w 360 --h 240 --q 70 --fps 10

# -----------------------------
# TCP client configuration
# -----------------------------
SERVER_IP = "192.168.1.149" # Change this to your server's IP address.
SERVER_VIDEO_PORT = 5000
SERVER_CONTROL_PORT = 5001
WINDOW_NAME = "TCP JPEG Snapshot"

# Request/camera parameters
DEFAULT_W = 720
DEFAULT_H = 480
DEFAULT_Q = 10
DEFAULT_FPS = 10

# Normalized command values consumed by RemoteController (-1..1).
CMD_LINEAR = 1.0
CMD_ANGULAR = 1.0

SOCKET_TIMEOUT_S = 1.0
MAX_FRAME_SIZE = 20 * 1024 * 1024
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
    if mapped is not None:
        with _KEYS_LOCK:
            _PRESSED_KEYS.add(mapped)


def _on_key_release(key):
    mapped = _normalize_key(key)
    if mapped is not None:
        with _KEYS_LOCK:
            _PRESSED_KEYS.discard(mapped)


def start_keyboard_listener():
    listener = keyboard.Listener(on_press=_on_key_press, on_release=_on_key_release)
    listener.start()
    return listener


def stop_keyboard_listener(listener):
    if listener is not None:
        listener.stop()
    with _KEYS_LOCK:
        _PRESSED_KEYS.clear()


def is_key_down(key_name: str) -> bool:
    with _KEYS_LOCK:
        return key_name in _PRESSED_KEYS


def parse_args():
    parser = argparse.ArgumentParser(description="TCP JPEG snapshot client")
    parser.add_argument("--ip", default=SERVER_IP, help="Server IP address")
    parser.add_argument("--video-port", type=int, default=SERVER_VIDEO_PORT, help="Server TCP video port")
    parser.add_argument("--control-port", type=int, default=SERVER_CONTROL_PORT, help="Server TCP control port")
    parser.add_argument("--w", type=int, default=DEFAULT_W, help="Requested width")
    parser.add_argument("--h", type=int, default=DEFAULT_H, help="Requested height")
    parser.add_argument("--q", type=int, default=DEFAULT_Q, help="JPEG quality (1-100)")
    parser.add_argument("--fps", type=int, default=DEFAULT_FPS, help="Requested FPS")
    return parser.parse_args()


def recv_exact(sock: socket.socket, count: int):
    chunks = []
    received = 0
    while received < count:
        chunk = sock.recv(count - received)
        if not chunk:
            return None
        chunks.append(chunk)
        received += len(chunk)
    return b"".join(chunks)


def connect_tcp(ip: str, port: int) -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(SOCKET_TIMEOUT_S)
    sock.connect((ip, port))
    return sock


def control_loop(stop_event: threading.Event, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)
    send_hz = max(1.0, CONTROL_SEND_HZ)
    send_period = 1.0 / send_hz
    sock = None
    prev_r_down = False

    try:
        while not stop_event.is_set():
            if sock is None:
                try:
                    sock = connect_tcp(args.ip, args.control_port)
                except OSError:
                    stop_event.wait(0.5)
                    continue

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

            if is_key_down(KEY_X):
                lvx = lvy = lvz = avy = avz = 0.0
            elif is_key_down(KEY_W):
                lvx = CMD_LINEAR
            elif is_key_down(KEY_S):
                lvx = -CMD_LINEAR
            elif is_key_down(KEY_A):
                avz = -CMD_ANGULAR
            elif is_key_down(KEY_D):
                avz = CMD_ANGULAR
            elif is_key_down(KEY_SPACE):
                lvz = CMD_LINEAR
                avy = CMD_ANGULAR

            payload = (
                f"snapshot?w={req_w};h={req_h};q={req_q};fps={req_fps};"
                f"lvx={lvx:.3f};lvy={lvy:.3f};lvz={lvz:.3f};avy={avy:.3f};avz={avz:.3f};r={reset}"
            ).encode("utf-8") + b"\n"

            try:
                sock.sendall(payload)
            except OSError:
                try:
                    sock.close()
                except OSError:
                    pass
                sock = None

            stop_event.wait(send_period)
    finally:
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass


def video_loop(stop_event: threading.Event, state: dict, state_lock: threading.Lock, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)

    request_payload = f"snapshot?w={req_w};h={req_h};q={req_q};fps={req_fps}".encode("utf-8") + b"\n"
    sock = None
    frame_count = 0
    fps_timer = time.time()

    try:
        while not stop_event.is_set():
            if sock is None:
                try:
                    sock = connect_tcp(args.ip, args.video_port)
                    with state_lock:
                        state["status_text"] = "Streaming"
                except OSError:
                    with state_lock:
                        state["status_text"] = "Video connect failed"
                    stop_event.wait(0.5)
                    continue

            try:
                sock.sendall(request_payload)
                header = recv_exact(sock, 4)
                if header is None:
                    raise ConnectionError("video socket closed")

                payload_len = struct.unpack(">I", header)[0]
                if payload_len <= 0 or payload_len > MAX_FRAME_SIZE:
                    raise ValueError(f"invalid payload length: {payload_len}")

                data = recv_exact(sock, payload_len)
                if data is None:
                    raise ConnectionError("video payload receive failed")
            except (OSError, ConnectionError, ValueError):
                with state_lock:
                    state["status_text"] = "Video reconnecting"
                try:
                    sock.close()
                except OSError:
                    pass
                sock = None
                stop_event.wait(0.2)
                continue

            if data == b"NO_FRAME":
                with state_lock:
                    state["status_text"] = "No frame"
                stop_event.wait(1.0 / req_fps)
                continue

            arr = np.frombuffer(data, dtype=np.uint8)
            img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
            if img is None:
                with state_lock:
                    state["status_text"] = f"Decode failed ({len(data)} bytes)"
                stop_event.wait(1.0 / req_fps)
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
                state["last_frame_size"] = len(data)
                if fps > 0.0:
                    state["video_fps"] = fps
                state["status_text"] = "Streaming"

            stop_event.wait(1.0 / req_fps)
    finally:
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass


def main():
    args = parse_args()
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)
    print(f"Video TCP:   {args.ip}:{args.video_port}")
    print(f"Control TCP: {args.ip}:{args.control_port}")
    print(f"Request params: w={req_w}, h={req_h}, q={req_q}, fps={req_fps}")
    print("Hold W/A/S/D/SPACE to move, hold X to stop, tap R to reset, tap Q to quit.")

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
    prev_q_down = False

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
            if q_down and not prev_q_down:
                break
            prev_q_down = q_down

            time.sleep(0.001)
    finally:
        stop_event.set()
        video_thread.join(timeout=1.0)
        control_thread.join(timeout=1.0)
        stop_keyboard_listener(listener)
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()