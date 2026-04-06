using System;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/*
 * HTTP MJPG Stream Server (split endpoints)
 * - Keeps an updated latest JPEG from Unity camera.
 * - Serves MJPG stream at /stream.
 * - Applies control/capture commands at /control without reopening the stream.
 *
 * Supported query format (same keys as TCP/UDP):
 *   /stream?w=360;h=240;q=70;fps=10;lvx=0.2;lvy=0;lvz=0;avy=0;avz=1.2&r=1
 * Also supports '&' as separator:
 *   /stream?w=360&h=240&q=70&fps=10&lvx=0.2&lvy=0&lvz=0&avy=0&avz=1.2&r=1
 */
public class HttpMjpgStreamServer : MonoBehaviour
{
    [Header("Dependency (same Camera GameObject)")]
    public JPEGCameraCapturerImproved capturer;
    public RemoteController robotController;

    [Header("Capture update rate")]
    [Range(1, 60)]
    public int fps = 10;

    [Header("HTTP server (split endpoints)")]
    public int listenPort = 8080;
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
        public bool captureDirty;
        public bool motionDirty;
    }

    private readonly object _paramsLock = new object();
    private CaptureParams _pendingParams;

    private HttpListener _listener;
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

        lock (_paramsLock)
        {
            _pendingParams.width = capturer.resWidth;
            _pendingParams.height = capturer.resHeight;
            _pendingParams.quality = capturer.jpgQuality;
            _pendingParams.fps = fps;
            _pendingParams.lvx = 0f;
            _pendingParams.lvy = 0f;
            _pendingParams.lvz = 0f;
            _pendingParams.avy = 0f;
            _pendingParams.avz = 0f;
            _pendingParams.resetRequested = false;
            _pendingParams.captureDirty = false;
            _pendingParams.motionDirty = false;
        }

        StartCoroutine(FrameProducerLoop());
        StartHttpServer();
        Debug.Log($"HTTP server listening on http://{ipAddress}:{listenPort} (stream:/stream, control:/control)");
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
                    sizeChanged = _pendingParams.width != capturer.resWidth
                               || _pendingParams.height != capturer.resHeight;

                    capturer.resWidth = _pendingParams.width;
                    capturer.resHeight = _pendingParams.height;
                    capturer.jpgQuality = _pendingParams.quality;
                    fps = _pendingParams.fps;

                    _pendingParams.captureDirty = false;
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

            if (sizeChanged)
            {
                capturer.EnsureBuffers();
                Debug.Log($"Capturer buffers recreated: {capturer.resWidth}x{capturer.resHeight}");
            }

            if (!_requestInFlight)
            {
                _requestInFlight = true;
                capturer.IsCaptureEnable = true;

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

    private void StartHttpServer()
    {
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{ipAddress}:{listenPort}/");
        _listener.Start();

        Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleContext(ctx, token), token);
        }
    }

    private async Task HandleContext(HttpListenerContext ctx, CancellationToken token)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        try
        {
            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 405;
                res.Close();
                return;
            }

            string path = req.Url.AbsolutePath;
            if (string.Equals(path, "/control", StringComparison.OrdinalIgnoreCase))
            {
                ParseAndEnqueueParams(req.Url.Query);
                byte[] ok = Encoding.UTF8.GetBytes("OK");
                res.StatusCode = 200;
                res.ContentType = "text/plain; charset=utf-8";
                res.ContentLength64 = ok.Length;
                await res.OutputStream.WriteAsync(ok, 0, ok.Length);
                res.Close();
                return;
            }

            if (!string.Equals(path, "/stream", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 404;
                byte[] notFound = Encoding.UTF8.GetBytes("Use /stream or /control");
                res.ContentType = "text/plain; charset=utf-8";
                res.ContentLength64 = notFound.Length;
                await res.OutputStream.WriteAsync(notFound, 0, notFound.Length);
                res.Close();
                return;
            }

            ParseAndEnqueueParams(req.Url.Query);

            const string boundary = "frame";
            res.StatusCode = 200;
            res.SendChunked = true;
            res.KeepAlive = true;
            res.ContentType = "multipart/x-mixed-replace; boundary=" + boundary;
            res.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            res.Headers["Pragma"] = "no-cache";
            res.Headers["Expires"] = "0";

            byte[] lineBreak = Encoding.ASCII.GetBytes("\r\n");
            var stream = res.OutputStream;

            while (!token.IsCancellationRequested)
            {
                byte[] jpg;
                lock (_jpegLock) jpg = _latestJpeg;

                if (jpg == null || jpg.Length == 0)
                {
                    await Task.Delay(10);
                    continue;
                }

                string header = $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);

                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.WriteAsync(jpg, 0, jpg.Length);
                await stream.WriteAsync(lineBreak, 0, lineBreak.Length);
                await stream.FlushAsync();

                int delayMs = Mathf.Max(1, 1000 / Mathf.Max(1, fps));
                await Task.Delay(delayMs);
            }
        }
        catch
        {
            // Client disconnected or stream closed.
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
            try { res.Close(); } catch { }
        }
    }

    private void ParseAndEnqueueParams(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            return;

        string query = queryString;
        if (query.StartsWith("?"))
            query = query.Substring(1);

        if (string.IsNullOrEmpty(query) || query.IndexOf('=') < 0)
            return;

        string[] tokens = query.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries);

        int newW = -1;
        int newH = -1;
        int newQ = -1;
        int newFps = -1;
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
            if (eq <= 0 || eq >= tok.Length - 1)
                continue;

            string key = WebUtility.UrlDecode(tok.Substring(0, eq).Trim());
            string val = WebUtility.UrlDecode(tok.Substring(eq + 1).Trim());

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

        lock (_paramsLock)
        {
            if (newW > 0) _pendingParams.width = newW;
            if (newH > 0) _pendingParams.height = newH;
            if (newQ > 0) _pendingParams.quality = newQ;
            if (newFps > 0) _pendingParams.fps = newFps;
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
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }
}
