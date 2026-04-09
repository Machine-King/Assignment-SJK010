import time
import urllib.request
import argparse
import threading
from pynput import keyboard

import cv2
import numpy as np

# python Http_mjpg_client_viewer.py --ip 192.168.1.149 --port 8080 --w 360 --h 240 --q 70 --fps 10

# -----------------------------
# HTTP MJPG client configuration
# -----------------------------
SERVER_IP = "192.168.1.149" # Change this to your server's IP address.
SERVER_PORT = 8080

WINDOW_NAME = "HTTP MJPG Stream"

# Request defaults
DEFAULT_W = 720
DEFAULT_H = 480
DEFAULT_Q = 10
DEFAULT_FPS = 10

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
    parser = argparse.ArgumentParser(description="HTTP MJPG stream client")
    parser.add_argument("--ip", default=SERVER_IP, help="Server IP address")
    parser.add_argument("--port", type=int, default=SERVER_PORT, help="Server HTTP port")
    parser.add_argument("--w", type=int, default=DEFAULT_W, help="Requested width")
    parser.add_argument("--h", type=int, default=DEFAULT_H, help="Requested height")
    parser.add_argument("--q", type=int, default=DEFAULT_Q, help="JPEG quality (1-100)")
    parser.add_argument("--fps", type=int, default=DEFAULT_FPS, help="Requested FPS")
    return parser.parse_args()


def control_loop(stop_event: threading.Event, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)

    send_hz = max(1.0, CONTROL_SEND_HZ)
    send_period = 1.0 / send_hz
    prev_r_down = False

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

        url = (
            f"http://{args.ip}:{args.port}/control?"
            f"w={req_w}&h={req_h}&q={req_q}&fps={req_fps}&"
            f"lvx={lvx}&lvy={lvy}&lvz={lvz}&avy={avy}&avz={avz}&r={reset}"
        )

        try:
            req = urllib.request.Request(url, method="GET")
            with urllib.request.urlopen(req, timeout=0.2):
                pass
        except Exception:
            pass

        stop_event.wait(send_period)


def stream_loop(stop_event: threading.Event, state: dict, state_lock: threading.Lock, args):
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)

    frame_count = 0
    fps_timer = time.time()
    loop_sleep = 1.0 / req_fps

    while not stop_event.is_set():
        # 1) Open stream
        stream_url = (
            f"http://{args.ip}:{args.port}/stream?"
            f"w={req_w}&h={req_h}&q={req_q}&fps={req_fps}&"
            f"lvx=0.000&lvy=0.000&lvz=0.000&avy=0.000&avz=0.000&r=0"
        )
        cap = cv2.VideoCapture(stream_url)
        if not cap.isOpened():
            with state_lock:
                state["status_text"] = f"Stream open failed: {stream_url}"
            stop_event.wait(loop_sleep)
            continue

        with state_lock:
            state["status_text"] = "Streaming"

        try:
            while not stop_event.is_set():
                # 2) Receive response frame
                ok, frame = cap.read()
                if not ok or frame is None:
                    with state_lock:
                        state["status_text"] = "Stream read failed, reconnecting"
                    break

                frame_count += 1
                now = time.time()
                elapsed = now - fps_timer
                fps = 0.0
                if elapsed >= 1.0:
                    fps = frame_count / elapsed
                    frame_count = 0
                    fps_timer = now

                with state_lock:
                    state["latest_img"] = frame
                    if fps > 0.0:
                        state["video_fps"] = fps
                    state["status_text"] = "Streaming"
        finally:
            cap.release()

        # 3) Keep reconnect cadence
        stop_event.wait(loop_sleep)


def main():
    args = parse_args()
    req_w = max(16, args.w)
    req_h = max(16, args.h)
    req_q = min(100, max(1, args.q))
    req_fps = max(1, args.fps)
    cv2.namedWindow(WINDOW_NAME, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(WINDOW_NAME, req_w, req_h)

    stream_url = (
        f"http://{args.ip}:{args.port}/stream?"
        f"w={req_w}&h={req_h}&q={req_q}&fps={req_fps}&"
        f"lvx=0.000&lvy=0.000&lvz=0.000&avy=0.000&avz=0.000&r=0"
    )
    print(f"MJPG stream URL: {stream_url}")
    print(f"HTTP control URL: http://{args.ip}:{args.port}/control")
    print(f"Request params: w={req_w}, h={req_h}, q={req_q}, fps={req_fps}")
    print("Hold W/A/S/D/SPACE to move, tap R to reset, tap Q to quit.")

    state_lock = threading.Lock()
    state = {
        "latest_img": None,
        "video_fps": 0.0,
        "status_text": "Waiting for first frame...",
    }
    stop_event = threading.Event()
    listener = start_keyboard_listener()

    stream_thread = threading.Thread(target=stream_loop, args=(stop_event, state, state_lock, args), daemon=True)
    control_thread = threading.Thread(target=control_loop, args=(stop_event, args), daemon=True)

    stream_thread.start()
    control_thread.start()

    try:
        while True:
            frame = None
            measured_fps = 0.0
            status_text = "Waiting for first frame..."

            with state_lock:
                if state["latest_img"] is not None:
                    frame = state["latest_img"].copy()
                measured_fps = state["video_fps"]
                status_text = state["status_text"]

            if frame is None:
                frame = np.zeros((req_h, req_w, 3), dtype=np.uint8)

            h, w = frame.shape[:2]
            cv2.putText(frame, f"HTTP MJPG {w}x{h}", (8, 20),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(frame, f"Status: {status_text}", (8, 44),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(frame, f"FPS: {measured_fps:.1f}", (8, 68),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 1, cv2.LINE_AA)
            cv2.putText(frame, f"Req {req_w}x{req_h} q={req_q} fps={req_fps}", (8, 92),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 1, cv2.LINE_AA)

            cv2.resizeWindow(WINDOW_NAME, req_w, req_h)
            cv2.imshow(WINDOW_NAME, frame)

            cv2.waitKey(1)
            q_down = is_key_down(KEY_Q)
            if q_down:
                break

            time.sleep(1.0/req_fps)
    finally:
        stop_event.set()
        stream_thread.join(timeout=1.0)
        control_thread.join(timeout=1.0)
        stop_keyboard_listener(listener)
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
