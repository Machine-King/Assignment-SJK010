# UDP, TCP and HTTP Servers - How do they work?

The underlying idea is the same for all three protocols: inside Unity, a script lives on the camera's `GameObject` and takes care of two things: sending JPEG frames to the client and receiving commands to move the car. The only thing that changes between them is *how* that information is sent and received over the network.

Each server depends on two components that were already in the project: [`JPEGCameraCapturerImproved`](JPEGCameraCapturerImproved.cs), which generates the JPEG from the camera, and [`RemoteController`](RemoteController.cs), which applies the velocities to the car's rigidbody.

## What the three of them share

Before getting into the differences, it's worth understanding the common part, because that's where most of the logic lives.

The **frame producer** is a coroutine (`FrameProducerLoop`) that runs on Unity's main thread. Every so often (depending on the configured `fps`) it asks the capturer for a new JPEG and stores it in `_latestJpeg`. Since the server thread can read that buffer at any time, anything that touches that variable is protected by `_jpegLock`.

The client never talks directly to the camera or to the car's controller. What it does is send a parameter string. The server thread parses it and writes the result into a shared struct (`_pendingParams`) under `_paramsLock`. On the next iteration of the capture loop, the main thread looks at that struct, checks which flags are `dirty` and applies the changes. These changes can be resizing the image, changing the quality, moving the car or resetting its position.

The command format is the same for all three: a string with `key=value` pairs covering resolution (`w`, `h`), JPEG quality (`q`), frame rate (`fps`), linear and angular velocities (`lvx`, `lvy`, `lvz`, `avy`, `avz`) and a reset flag (`r`). If a key isn't present, its value stays as it was.

## UDP - [UdpJpegSnapshotServer.cs](UDP/UdpJpegSnapshotServer.cs)

Here I use **two independent `UdpClient`s**, one on port 5000 for video and another on 5001 for control. Splitting them prevents movement commands from having to wait for a frame to finish being sent.

The problem with UDP is that a datagram has a fairly limited maximum size, and a camera JPEG almost never fits in a single one. The solution I applied is to **fragment at the application layer**: I split the JPEG into 1200-byte chunks and attach a 10-byte header to each one with `frameId`, `chunkId`, `chunkCount` and `payloadLen`. That's what `SendFrameFragmented` does.

With those four fields, the client can reassemble the chunks even if they arrive out of order and discard frames that are incomplete.

## TCP - [TcpJpegSnapshotServer.cs](TCP/TcpJpegSnapshotServer.cs)

The two-channel structure repeats here: a `TcpListener` on port 5000 for video and another on 5001 for control, for the same reason of not mixing traffic.

The client sends a UTF-8 line ending in `\n` with the request, and the server replies with **4 bytes (big-endian) indicating the size + the JPEG**. The control channel works the same way, except it doesn't reply with anything (it only applies the parameters).

Unlike UDP, TCP already guarantees ordering and delivery, so there's no need to worry about fragmentation or lost packets. The connection stays open and is reused for all the client's requests.

## HTTP - [HttpMjpgStreamServer.cs](HTTP/HttpMjpgStreamServer.cs)

I use a single `HttpListener` on port 8080 with two endpoints:

- **`/stream`** returns an MJPG (`multipart/x-mixed-replace; boundary=frame`), which is basically an infinite sequence of JPEGs separated by a marker.

- **`/control`** receives the parameters as a query string and replies `OK`.

The important difference here is that, unlike TCP where each frame is served as a *request/response* (the client asks, the server replies with a JPEG, and repeats), `/stream` works as a **continuous stream**: the client opens the connection just once and the server keeps pushing frames one after another for as long as the session lasts. There's no need to request each image. It's enough to read from the socket. That eliminates the per-frame request overhead.

## Summary

In the end the three servers do the same thing: capture a JPEG and apply commands to the car. The difference is in the wrapper.

In [Comparison.md](Comparison.md) you'll find a detailed comparison of the three protocols.
