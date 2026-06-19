# Python video & control over TCP / UDP / HTTP - Unity+Python

A small benchmark project: a Unity scene with a drivable car streams JPEG
frames from its camera to a Python viewer over three different transports,
while the viewer pushes movement commands back. Same task, three transports,
side-by-side comparison.

## Layout

```
Assets/Scripts/
├── HTTP/
│   ├── HttpMjpgStreamServer.cs       Unity server
│   └── Http_mjpg_client_viewer.py    Python viewer
├── TCP/
│   ├── TcpJpegSnapshotServer.cs      Unity server
│   └── Tcp_client_viewer.py          Python viewer
├── UDP/
│   ├── UdpJpegSnapshotServer.cs      Unity server
│   └── udp_client_viewer.py          Python viewer
├── JPEGCameraCapturerImproved.cs     On-demand camera→JPEG capture, used by all three servers
├── RemoteController.cs               Receives motion commands and drives the car Rigidbody
├── MoveSphereScript.cs               Local keyboard control (no networking)
├── PROTOCOLOS.md                     High-level walkthrough of the three server implementations
├── COMPARISON.md                     Head-to-head benchmark with screenshots
└── Comparison/imgs/                  Reference screenshots
```

## Running it

### 1. Server (Unity)

1. Open this folder as a Unity project.
2. Select the corresponding scene according to the protocol you want to use (`UDP`,`TCP`,`HTTP`).
3. In the inspector, set `ipAddress` to a real local interface IP
   (`ipconfig` on the Unity machine), override ports if needed, and press
   Play. The Unity console will log the bound address.

### 2. Client (Python)

Dependencies are declared in `pyproject.toml`. Install with `uv sync` or:

```bash
pip install opencv-python numpy pynput
```

Then run the viewer that matches the server you started. Defaults are
`720x480 q=10 fps=10`, override on the CLI:

```bash
# HTTP - continuous MJPEG stream
python Assets/Scripts/HTTP/Http_mjpg_client_viewer.py \
    --ip <server-ip> --port 8080 --w 360 --h 240 --q 70 --fps 10

# TCP - per-frame request/response
python Assets/Scripts/TCP/Tcp_client_viewer.py \
    --ip <server-ip> --video-port 5000 --control-port 5001 \
    --w 360 --h 240 --q 70 --fps 10

# UDP - per-frame request/response, application-layer chunked
python Assets/Scripts/UDP/udp_client_viewer.py \
    --ip <server-ip> --video-port 5000 --control-port 5001 \
    --w 360 --h 240 --q 70 --fps 10
```

A window opens with the live frame and a status / FPS / size overlay.

### Controls

Focus the viewer window, then:

| Key | Action |
|---|---|
| `W` / `S` | Forward / backward |
| `A` / `D` | Yaw left / right |
| `Space` | Lift + roll (right the car if it flips) |
| `R` | Reset car position |
| `Q` | Quit the viewer |

All three control loops sum simultaneous keys (e.g. `W` + `A` together).

## Comparison

See [Assets/Scripts/COMPARISON.md](Assets/Scripts/COMPARISON.md) for a
1920x1080 q=100 fps=60 stress test of the three transports. Short version:
HTTP/MJPEG (continuous push) wins on throughput, TCP per-frame trails by
about 30 % because each frame waits for a request/response cycle, and UDP collapses under
application-layer fragmentation at high resolutions.

## Protocols
See [Assets/Scripts/PROTOCOLS.md](Assets/Scripts/PROTOCOLS.md) for a brief explication of the protocols implemented.
