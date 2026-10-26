# camera

Two script modules for video input. Frames are the core image object (`aqua/image.h`), so they go straight to
Processing, Images, ONNX or MediaPipe.

| Module | Served on | Platform layer |
| --- | --- | --- |
| `攝影機` / `Webcam` / `網路攝影機` | Windows, Linux, web | Media Foundation (`src/platform/windows.c`), Video4Linux2 (`linux.c`), `getUserMedia` through `web-glue/camera` (`web.c`) |
| `Camera` / `相機` | Android | NDK Camera2 (`src/platform/android.c`) |

Each module is registered on every target; where it is not served it says so (`是否支援()` is false). New operating
systems start from `src/platform/unsupported.c`.

## Script interface (unchanged)

```aquarius
變數 攝影機 = 匯入("攝影機");                 # Android: 匯入("相機")
變數 裝置 = 攝影機.列出裝置();                # each has 識別碼 and 名稱 (Camera also 方向: Back/Front/External)
變數 工作階段 = 攝影機.開啟("", 640, 480, 30); # "" = the default device (Android: the first back camera)
變數 影格 = 工作階段.擷取影格();
印出(影格.寬度, 影格.高度, 影格.序號, 影格.時間戳記);
變數 像素 = 影格.取得像素();                  # new byte array, R G B A, top row first
工作階段.釋放();                              # safe to repeat
```

| 攝影機 (Traditional Chinese names only, as the old catalog) | Camera | Arguments |
| --- | --- | --- |
| `是否支援` | `IsSupported` / `是否支援` | 0 |
| `列出裝置` | `Enumerate` / `列出裝置` | 0 |
| `開啟` → session | `Open` / `開啟` | 4: id, width 1..1920, height 1..1080, frames per second 1..60 |
| session `是否開啟` / `擷取影格` / `釋放` | `IsOpen` / `Capture` / `Dispose` (and the Chinese names) | 0 |
| frame `寬度` `高度` `序號` `時間戳記` `影像`, `取得像素()` | the same, plus `方向`; `GetPixels` / `取得像素` | 0 |

Behavior kept from the old runtime: arguments are validated before any device is touched; at most four sessions are
open per runtime; listing never asks for permission or turns a camera on; `擷取影格` returns the newest frame not yet
delivered and waits only when there is none (slow scripts skip old frames instead of accumulating latency); a capture
while another capture of the same session waits fails; a failed or timed-out capture closes the session; frames are
independent opaque RGBA snapshots. Webcam frames have exactly the requested size, the picture scaled to fit with black
bars; Camera frames are the upright image (sensor and display rotation, front cameras compensated, never mirrored)
stretched to the request, portrait when the device is. Errors read as before: `攝影機.開啟：參數無效：…`,
`相機.Open：InvalidArgument：…`. The failure kinds are 不支援, 權限遭拒, 找不到裝置, 裝置無法使用, 擷取逾時, 已取消, 已關閉
and 參數無效. Opening a session is the only call that touches a device or asks for permission; nothing records audio or
video, and no frame leaves the application unless the script sends it.

## How it works

`src/camera.c` owns everything scripts see: validation, sessions, a two-buffer latest-frame mailbox, the 10 s timeout
of a waiting call, messages and objects. Opening and waiting captures are deferred calls; platform threads report
frames and failures with `camera_frame` / `camera_fail`, which reach the runtime thread through `aqua_post`.
`src/convert.c` turns every delivered layout (RGBA, RGB32/BGRA, RGB24/BGR24, YUYV, UYVY, NV12/NV21/I420/YV12 through
plane strides, gray; bottom-up rows; rotations) into upright RGBA (BT.601) and scales it with a separable triangle filter
(area averaging when shrinking, bilinear when enlarging). The platform layers only list devices and move pixels:

- **Windows**: one thread per session activates the device, picks the native mode that serves the request best
  (frame rate first, then size and aspect, uncompressed preferred), lets Media Foundation decode compressed modes (MJPG)
  to NV12/YUY2/RGB32 and reads samples synchronously. Closing shuts the media source down.
- **Linux**: `/dev/video*` capture nodes (metadata nodes are skipped), the best uncompressed mode, memory-mapped
  streaming on one thread per session, woken through a pipe to close.
- **Web**: the glue keeps the `MediaStream` in a muted `<video>` and copies a decoded frame at the video's own size into
  memory C provides (one call per frame); C reads the next frame when the video presents one. DOMException names map to
  the old failure kinds. Needs a secure context (HTTPS or localhost).
- **Android**: `android.permission.CAMERA` through `aqua_android_request_permission` (declared in `aqua.toml`), a
  `YUV_420_888` image reader at the supported size closest to the request (aspect, then area, at most 1920x1080) and a
  repeating preview request at the closest frame-rate range.

## Dependencies

None vendored. System APIs only: Media Foundation (`mfplat`, `mf`, `mfreadwrite`, `mfuuid`, `ole32`) and winpthreads
(linked statically) on Windows, V4L2 and pthreads on Linux, `camera2ndk` and `mediandk` on Android, the browser's Media
Capture API on the web.

## Differences from the old implementation

- Desktop capture uses Media Foundation and V4L2 directly instead of an FFmpeg process: no FFmpeg installation or
  `AQUARIUS_FFMPEG`. `是否支援()` is true on Windows when Media Foundation starts and always on Linux.
- Device identifiers are the Media Foundation symbolic links on Windows (were FFmpeg's `video=…` names); Linux keeps
  `/dev/videoN`. They were never meant to be stored or shared between platforms.
- Linux supports uncompressed camera modes only; a camera that offers nothing but MJPEG reports `裝置無法使用`.
- Wrong argument counts report the runtime's message (`攝影機.開啟 expects 4 arguments, got 0.`) instead of
  `需要 4 個參數…`; the other messages are unchanged.
- Sessions, frames and devices are native objects: `型別(...)` shows their class (`攝影機.影格`, `Camera.Session`, …)
  instead of `module`. Fields and functions are read the same way.
- The 10 s timeout applies while a call waits (opening, capturing); an idle session is not closed for lack of frames.
- `序號` counts the frames the session received, so it may skip numbers (as on desktop before), also on the web, where
  the first capture can report 2.
- `攝影機` is also registered on Android, as unsupported (the old Android host had no such module); `Camera` on desktop
  and web behaves as before (unsupported, lists nothing).
- Android: opening and capturing no longer block the runtime; permission comes from the runtime's permission request;
  errors use the old CameraRuntime.cs format (`相機.Open：PermissionDenied：…`) instead of the Java host's English
  strings. Sessions end through the camera service's disconnect callback when the application loses the camera rather
  than an explicit pause hook.

## Tests

`tests/*.aqua` with exact `*.expected` output. `webcam_list` passes with any number of devices; `webcam_capture` needs a
camera and is run explicitly (Chromium's fake device on the web).
