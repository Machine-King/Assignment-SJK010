using System;
using System.Collections;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/*
 * UDP JPEG Snapshot Server (split channels)
 * A minimal UDP request/response server:
 * - The server keeps an updated "latest JPEG" from the Unity camera.
 * - Any incoming UDP datagram triggers a fragmented reply for the current JPEG frame.
 *
 * Supported request format (video/control):
 *   "snapshot?w=360;h=240;q=70;fps=10;lvx=0.2;lvy=0;lvz=0;avy=0;avz=1.2;r=1"
 *   The part before '?' is ignored (command name). The query string uses
 *   semicolon-separated key=value pairs.
 *   Keys: w, h, q, fps, lvx, lvy, lvz, avy, avz, r.
 *   An empty/missing key leaves the current value unchanged.
 *   A plain "snapshot" (no '?') simply requests the current frame.
 *
 * Channel split:
 * - videoListenPort: receives snapshot requests and sends frame chunks.
 * - controlListenPort: receives control/camera parameter updates only.
 *
 * Fragmentation strategy:
 * - JPEG data is split at application layer into fixed-size chunks.
 * - Every chunk carries a small header (frameId, chunkId, chunkCount, payloadLen).
 * - Client reassembles all chunks and discards incomplete frames.
 */
public class UdpJpegSnapshotServer : MonoBehaviour
{
    [Header("Dependency (same Camera GameObject)")]
    public JPEGCameraCapturerImproved capturer;
    public RemoteController robotController;

    [Header("Capture update rate")]
    [Range(1, 60)]
    public int fps = 10;
    
    [Header("UDP server (split channels)")]
    public int videoListenPort = 5000;
    public int controlListenPort = 5001;
    public string ipAddress = "192.168.1.149";

    private readonly object _jpegLock = new object();
    private byte[] _latestJpeg;
    private bool _requestInFlight = false;
    private uint _nextFrameId = 0;

    private const int ChunkHeaderSize = 10;
    private const int ChunkDataSize = 1200;

    /*
    *  Shared parameter block (lock-protected)
    *  Parsed on the network thread; applied on the main thread.
    */
    private struct CaptureParams
    {
        public int width;
        public int height;
        public int quality;
        public int fps;
        public float lvx;
        public float lvy;
        public float lvz;
        public float avy;
        public float avz;
        public bool resetRequested;
        public bool captureDirty; // true when camera params changed on network thread
        public bool motionDirty;  // true when velocity params changed on network thread
    }

    private readonly object _paramsLock = new object();
    private CaptureParams _pendingParams;

    private UdpClient _udpVideo;
    private UdpClient _udpControl;
    private CancellationTokenSource _cts;

    private void Start()
    {
        if (capturer == null)
        {
            capturer = GetComponent<JPEGCameraCapturerImproved>();
            if (capturer == null)
            {
                Debug.LogError("JPEGCameraCapturerImproved not found. Add it to the same Camera GameObject.");
                enabled = false;
                return;
            }
        }

        if (robotController == null)
        {
            // Server lives on camera; controller lives on parent car.
            robotController = GetComponentInParent<RemoteController>();
            if (robotController == null)
            {
                Debug.LogError("RemoteController not found in parent hierarchy. Add it to the car parent object.");
                enabled = false;
                return;
            }
        }

        // Seed pending params with the capturer's current values.
        lock (_paramsLock)
        {
            _pendingParams.width   = capturer.resWidth;
            _pendingParams.height  = capturer.resHeight;
            _pendingParams.quality = capturer.jpgQuality;
            _pendingParams.fps     = fps;
            _pendingParams.lvx     = 0f;
            _pendingParams.lvy     = 0f;
            _pendingParams.lvz     = 0f;
            _pendingParams.avy     = 0f;
            _pendingParams.avz     = 0f;
            _pendingParams.resetRequested = false; // Key r
            _pendingParams.captureDirty = false;
            _pendingParams.motionDirty = false;
        }

        StartCoroutine(FrameProducerLoop());
        StartUdpServer();
        Debug.Log($"UDP server listening on {ipAddress} (video:{videoListenPort}, control:{controlListenPort})");
    }

    private IEnumerator FrameProducerLoop()
    {
        var wait = new WaitForSeconds(1f / Mathf.Max(1, fps));

        while (true)
        {
            // Local variables that will be updated if any changes from client
            bool sizeChanged = false;
            bool motionChanged = false;
            bool resetRequested = false;
            float nextLvx = 0f;
            float nextLvy = 0f;
            float nextLvz = 0f;
            float nextAvy = 0f;
            float nextAvz = 0f;
            lock (_paramsLock)
            {
                if (_pendingParams.captureDirty)
                {
                    // Detect resolution change before applying.
                    sizeChanged = _pendingParams.width  != capturer.resWidth
                               || _pendingParams.height != capturer.resHeight;

                    capturer.resWidth   = _pendingParams.width;
                    capturer.resHeight  = _pendingParams.height;
                    capturer.jpgQuality = _pendingParams.quality;
                    fps                 = _pendingParams.fps;

                    _pendingParams.captureDirty = false;

                    // Recompute wait interval.
                    wait = new WaitForSeconds(1f / Mathf.Max(1, fps));
                }

                if (_pendingParams.motionDirty)
                {
                    nextLvx = _pendingParams.lvx;
                    nextLvy = _pendingParams.lvy;
                    nextLvz = _pendingParams.lvz;
                    nextAvy = _pendingParams.avy;
                    nextAvz = _pendingParams.avz;
                    motionChanged = true;
                    _pendingParams.motionDirty = false;
                }

                if (_pendingParams.resetRequested)
                {
                    resetRequested = true;
                    _pendingParams.resetRequested = false;
                }
            }

            if (resetRequested)
            {
                robotController.resetPosition();
            }

            if (motionChanged)
            {
                robotController.moveVelocity(nextLvx, nextLvy, nextLvz, nextAvy, nextAvz);
            }

            // If width or height changed, force the capturer to recreate
            // its RenderTexture / Texture2D buffers immediately.
            if (sizeChanged)
            {
                capturer.EnsureBuffers();
                Debug.Log($"Capturer buffers recreated: {capturer.resWidth}x{capturer.resHeight}");
            }

            if (!_requestInFlight)
            {
                _requestInFlight = true;
                capturer.IsCaptureEnable = true;

                // Wait for completion with timeout.
                float t0 = Time.realtimeSinceStartup;
                float timeoutSec = 1.0f;

                while (capturer.IsCaptureEnable && (Time.realtimeSinceStartup - t0) < timeoutSec)
                {
                    yield return null;
                }

                if (capturer.IsCaptureEnable)
                {
                    capturer.IsCaptureEnable = false;
                    _requestInFlight = false;
                    Debug.LogWarning("Capture timed out. Check capturer.");
                    yield return wait;
                    continue;
                }

                _requestInFlight = false;
                var bytes = capturer.jpg;
                if (bytes != null && bytes.Length > 0)
                {
                    lock (_jpegLock) _latestJpeg = bytes;
                }
            }
            yield return wait;
        }
    }

    private void StartUdpServer()
    {
        _cts = new CancellationTokenSource();

        // Bind independent sockets so control traffic never blocks video replies.
        _udpVideo = new UdpClient(new IPEndPoint(IPAddress.Parse(ipAddress), videoListenPort));
        _udpControl = new UdpClient(new IPEndPoint(IPAddress.Parse(ipAddress), controlListenPort));

        Task.Run(() => ReceiveVideoLoop(_cts.Token));
        Task.Run(() => ReceiveControlLoop(_cts.Token));
    }

    private async Task ReceiveVideoLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult rx;
            try
            {
                rx = await _udpVideo.ReceiveAsync();
            }
            catch
            {
                break; // socket closed
            }

            // Parse the received string
            string req = "(non-utf8)";
            try { req = Encoding.UTF8.GetString(rx.Buffer); } catch { }

            ParseAndEnqueueParams(req);

            // Reply with current JPEG
            byte[] jpg;
            lock (_jpegLock) jpg = _latestJpeg;

            if (jpg == null || jpg.Length == 0)
            {
                byte[] msg = Encoding.UTF8.GetBytes("NO_FRAME");
                try { await _udpVideo.SendAsync(msg, msg.Length, rx.RemoteEndPoint); } catch { }
                continue;
            }

            try
            {
                await SendFrameFragmented(jpg, rx.RemoteEndPoint);
            }
            catch (Exception e)
            {
                // Typically happens if the datagram is too large for the OS/network stack.
                Debug.LogWarning($"UDP send failed (jpg bytes={jpg.Length}). Reason: {e.Message}");
            }
        }
    }

    private async Task ReceiveControlLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult rx;
            try
            {
                rx = await _udpControl.ReceiveAsync();
            }
            catch
            {
                break; // socket closed
            }

            string req = "(non-utf8)";
            try { req = Encoding.UTF8.GetString(rx.Buffer); } catch { }

            ParseAndEnqueueParams(req);
        }
    }

    private async Task SendFrameFragmented(byte[] jpg, IPEndPoint remoteEndPoint)
    {
        uint frameId = unchecked(++_nextFrameId);
        int chunkCount = (jpg.Length + ChunkDataSize - 1) / ChunkDataSize;

        if (chunkCount <= 0)
            return;

        for (int chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            int payloadOffset = chunkId * ChunkDataSize;
            int payloadLen = Math.Min(ChunkDataSize, jpg.Length - payloadOffset);

            byte[] packet = new byte[ChunkHeaderSize + payloadLen];

            // frameId(4), chunkId(2), chunkCount(2), payloadLen(2) (10 total)
            // Build a custom header for each fragment packet.
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), (ushort)chunkId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), (ushort)chunkCount);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)payloadLen);

            // After the header, copy the corresponding JPEG data chunk into packet and send it.
            Buffer.BlockCopy(jpg, payloadOffset, packet, ChunkHeaderSize, payloadLen);
            await _udpVideo.SendAsync(packet, packet.Length, remoteEndPoint);
        }
    }

    /*
    *  Parser for incoming UDP messages (video/control)
    *  Expected format: "snapshot?w=360;h=240;q=70;fps=10;lvx=0;lvy=0;lvz=0;avy=0;avz=0;r=1"
    *  The command name before '?' is ignored.
    *  Unknown keys are silently ignored; missing keys keep defaults.
    */
    private void ParseAndEnqueueParams(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        int queryStart = message.IndexOf('?');
        if (queryStart < 0)
            return;

        string query = message.Substring(queryStart + 1);
        if (string.IsNullOrEmpty(query) || query.IndexOf('=') < 0)
            return;

        // Parse key=value pairs separated by ';'.
        string[] tokens = query.Split(';', StringSplitOptions.RemoveEmptyEntries);

        int  newW   = -1;
        int  newH   = -1;
        int  newQ   = -1;
        int  newFps = -1;
        float? newLvx = null;
        float? newLvy = null;
        float? newLvz = null;
        float? newAvy = null;
        float? newAvz = null;
        bool requestReset = false;
        bool anyCapture = false;
        bool anyMotion = false;
        bool anyReset = false;

        foreach (string tok in tokens)
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0 || eq >= tok.Length - 1) continue;

            string key = tok.Substring(0, eq).Trim();
            string val = tok.Substring(eq + 1).Trim();

            switch (key.ToLowerInvariant())
            {
                case "w":
                    if (int.TryParse(val, out int parsedW))
                    {
                        newW = Math.Max(16, parsedW);
                        anyCapture = true;
                    }
                    break;

                case "h":
                    if (int.TryParse(val, out int parsedH))
                    {
                        newH = Math.Max(16, parsedH);
                        anyCapture = true;
                    }
                    break;

                case "q":
                    if (int.TryParse(val, out int parsedQ))
                    {
                        newQ = Math.Clamp(parsedQ, 1, 100);
                        anyCapture = true;
                    }
                    break;

                case "fps":
                    if (int.TryParse(val, out int parsedFps))
                    {
                        newFps = Math.Clamp(parsedFps, 1, 60);
                        anyCapture = true;
                    }
                    break;

                case "lvx":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedLvx))
                    {
                        newLvx = parsedLvx;
                        anyMotion = true;
                    }
                    break;

                case "lvy":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedLvy))
                    {
                        newLvy = parsedLvy;
                        anyMotion = true;
                    }
                    break;

                case "lvz":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedLvz))
                    {
                        newLvz = parsedLvz;
                        anyMotion = true;
                    }
                    break;

                case "avy":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedAvy))
                    {
                        newAvy = parsedAvy;
                        anyMotion = true;
                    }
                    break;

                case "avz":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedAvz))
                    {
                        newAvz = parsedAvz;
                        anyMotion = true;
                    }
                    break;

                case "r":
                    if (int.TryParse(val, out int resetVal) && resetVal != 0)
                    {
                        requestReset = true;
                        anyReset = true;
                    }
                    break;
            }
        }

        if (!anyCapture && !anyMotion && !anyReset)
            return;

        // Write into the shared parameter block (consumed on the main thread).
        lock (_paramsLock)
        {
            if (newW   > 0) _pendingParams.width   = newW;
            if (newH   > 0) _pendingParams.height  = newH;
            if (newQ   > 0) _pendingParams.quality = newQ;
            if (newFps > 0) _pendingParams.fps     = newFps;
            if (newLvx.HasValue) _pendingParams.lvx = newLvx.Value;
            if (newLvy.HasValue) _pendingParams.lvy = newLvy.Value;
            if (newLvz.HasValue) _pendingParams.lvz = newLvz.Value;
            if (newAvy.HasValue) _pendingParams.avy = newAvy.Value;
            if (newAvz.HasValue) _pendingParams.avz = newAvz.Value;
            if (requestReset) _pendingParams.resetRequested = true;
            if (anyCapture) _pendingParams.captureDirty = true;
            if (anyMotion) _pendingParams.motionDirty = true;
        }
    }

    private void OnDestroy()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udpVideo?.Close(); } catch { }
        try { _udpControl?.Close(); } catch { }
    }
}