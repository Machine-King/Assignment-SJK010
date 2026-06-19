using System;
using System.Collections;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/*
 * TCP JPEG Snapshot Server (split channels)
 * A minimal TCP snapshot/control server:
 * - The server keeps an updated "latest JPEG" from the Unity camera.
 * - Video channel replies with current snapshot JPEG bytes.
 * - Control channel receives control/camera updates only.
 *
 * Protocol:
 *   Video channel request: one UTF-8 line ending in '\n'.
 *   Video channel response: 4-byte length prefix (big-endian) + payload bytes (JPEG or "NO_FRAME").
 *   Control channel request: one UTF-8 line ending in '\n' (no response body).
 *
 * Supported request format (video/control):
 *   "snapshot?w=360;h=240;q=70;fps=10;lvx=0.2;lvy=0;lvz=0;avy=0;avz=1.2;r=1"
 *   The part before '?' is ignored (command name). The query string uses
 *   semicolon-separated key=value pairs.
 *   Keys: w, h, q, fps, lvx, lvy, lvz, avy, avz, r.
 *   An empty/missing key leaves the current value unchanged.
 *   A plain "snapshot" (no '?') simply requests the current frame.
 *
 * Thread-safety approach:
 *   Shared variables protected by a lock.
 *   Parsed parameters are stored in _pendingParams (network thread)
 *   and consumed on the main thread inside FrameProducerLoop().
 */
public class TcpJpegSnapshotServer : MonoBehaviour
{
    [Header("Dependency (same Camera GameObject)")]
    public JPEGCameraCapturerImproved capturer;
    public RemoteController robotController;

    [Header("Capture update rate")]
    [Range(1, 60)]
    public int fps = 10;
    
    [Header("TCP server (split channels)")]
    public int videoListenPort = 5000;
    public int controlListenPort = 5001;
    public string ipAddress = "192.168.1.149";

    private readonly object _jpegLock = new object();
    private byte[] _latestJpeg;
    private bool _requestInFlight = false;

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

    private TcpListener _videoListener;
    private TcpListener _controlListener;
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
            _pendingParams.resetRequested = false;
            _pendingParams.captureDirty = false;
            _pendingParams.motionDirty = false;
        }

        StartCoroutine(FrameProducerLoop());
        StartTcpServer();
        Debug.Log($"TCP server listening on {ipAddress} (video:{videoListenPort}, control:{controlListenPort})");
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

    private void StartTcpServer()
    {
        _cts = new CancellationTokenSource();

        _videoListener = new TcpListener(IPAddress.Parse(ipAddress), videoListenPort);
        _controlListener = new TcpListener(IPAddress.Parse(ipAddress), controlListenPort);
        _videoListener.Start();
        _controlListener.Start();

        Task.Run(() => AcceptVideoLoop(_cts.Token));
        Task.Run(() => AcceptControlLoop(_cts.Token));
    }

    private async Task AcceptVideoLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _videoListener.AcceptTcpClientAsync();
            }
            catch
            {
                break; // listener stopped
            }

            _ = Task.Run(() => HandleVideoClient(client, token), token);
        }
    }

    private async Task AcceptControlLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _controlListener.AcceptTcpClientAsync();
            }
            catch
            {
                break; // listener stopped
            }

            _ = Task.Run(() => HandleControlClient(client, token), token);
        }
    }

    private async Task HandleVideoClient(TcpClient client, CancellationToken token)
    {
        NetworkStream stream = null;
        StreamReader reader = null;
        try
        {
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);

            while (!token.IsCancellationRequested && client.Connected)
            {
                string req;
                try
                {
                    req = await reader.ReadLineAsync();
                }
                catch
                {
                    break; // connection closed or error
                }

                if (req == null)
                    break; // client disconnected

                ParseAndEnqueueParams(req);

                // --- Reply with current JPEG (length-prefixed) ---
                byte[] jpg;
                lock (_jpegLock) jpg = _latestJpeg;

                byte[] payload;
                if (jpg == null || jpg.Length == 0)
                {
                    payload = Encoding.UTF8.GetBytes("NO_FRAME");
                }
                else
                {
                    payload = jpg;
                }

                try
                {
                    // Send payload with explicit frame length (big-endian uint32).
                    byte[] header = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
                    await stream.WriteAsync(header, 0, header.Length, token);
                    await stream.WriteAsync(payload, 0, payload.Length, token);
                    await stream.FlushAsync(token);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"TCP send failed (payload bytes={payload.Length}). Reason: {e.Message}");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"TCP video client handler error: {e.Message}");
        }
        finally
        {
            try { reader?.Dispose(); } catch { }
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }
    }

    private async Task HandleControlClient(TcpClient client, CancellationToken token)
    {
        NetworkStream stream = null;
        StreamReader reader = null;
        try
        {
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);

            while (!token.IsCancellationRequested && client.Connected)
            {
                string req;
                try
                {
                    req = await reader.ReadLineAsync();
                }
                catch
                {
                    break;
                }

                if (req == null)
                    break;

                ParseAndEnqueueParams(req);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"TCP control client handler error: {e.Message}");
        }
        finally
        {
            try { reader?.Dispose(); } catch { }
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }
    }

    /*
    *  Parser for incoming TCP messages (video/control)
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
        try { _videoListener?.Stop(); } catch { }
        try { _controlListener?.Stop(); } catch { }
    }
}