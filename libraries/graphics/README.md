# graphics

Processing sketches (`Processing` / `繪圖`), the `WGPU` module, `GLM`, `TextInput`, `Window` integration and `Fonts`, on
one WebGPU renderer written in C against `webgpu.h`, plus the Aquarius-source modules `Models` / `模型`, `PBR` and
`GPUMath` / `GPU數學`. One source tree serves every target:

| target | WebGPU | windows and input | text |
| --- | --- | --- | --- |
| Windows | wgpu-native (D3D12 / Vulkan), linked statically | `aqua/window.h` (GLFW) | GDI (`src/platform/windows.c`) |
| Linux | wgpu-native (Vulkan) | `aqua/window.h` (GLFW, X11 / Wayland) | Pango + Cairo, loaded at run time (`linux.c`) |
| Android | wgpu-native (Vulkan) | the activity's surface | `android.graphics` through JNI (`android.c`) |
| web | the browser's WebGPU through `web-glue/webgpu` | `<canvas>` windows | Canvas 2D through `web-glue/canvas` (`web.c`) |

API guides: [PBR and model import](docs/pbr-models.md), [GPUMath](docs/gpu-math.md),
[textures and HDR](docs/wgpu-textures.md), [binary meshes](docs/mesh-assets.md).

## Layout

```text
aqua.toml            manifest: native modules and the Aquarius-source modules
CMakeLists.txt       sources, the pinned wgpu-native download, OS link libraries
aqua/                Models.aqua, PBR.aqua, GPUMath.aqua
src/graphics.c       entry point and per-runtime state
src/common/          calls (error prefixes, async calls), matrices (System.Numerics semantics), Unicode graphemes,
                     image codecs (stb), resource files, records, small containers
src/gpu/             the renderer: devices and surfaces, textures, render targets, WGSL shaders and the uniform ABI,
                     buffers and compute
src/processing/      Processing: module.c (registration, fields), lifecycle.c (size/run/frames), input.c, canvas.c,
                     drawing.c, geometry.c (tessellation kernel), image.c, shapes.c, shaders.c, text.c, math.c, vector.c
src/modules/         wgpu.c, glm.c, textinput.c, window.c, fonts.c
src/platform/        the per-OS text layer (one line of text: its box and an alpha mask; installed fonts)
third_party/stb/     stb_image 2.30 and stb_image_write 1.16 (MIT / public domain), compiled privately into codecs.c
tests/               <case>.aqua + <case>.expected (+ <case>.<target>.expected where a target differs)
```

Every global symbol carries the `gfx_` (or `gpu_`) prefix, since default libraries link into one executable.

## Dependencies

- **wgpu-native 29.0.1.1** (MIT or Apache-2.0, gfx-rs/wgpu-native), downloaded by `CMakeLists.txt` from the official
  GitHub release for the target (`wgpu-windows-x86_64-gnu-release.zip`, `wgpu-linux-x86_64-release.zip`,
  `wgpu-android-aarch64-release.zip`, `wgpu-android-x86_64-release.zip`), checked against its pinned SHA-256 and
  linked statically. Its copy of the Rust standard library's one unmangled symbol (`rust_eh_personality`) is renamed
  with `llvm-objcopy` so the runtime can link it next to Wasmtime. The web build only takes `webgpu.h` from the host's
  archive. `AQUA_GRAPHICS_DOWNLOADS` points several build directories at one download folder.
- **stb_image 2.30 / stb_image_write 1.16** (vendored, `third_party/stb/LICENSE`).
- Android emulator: its GFXStream Vulkan driver (`vulkan.ranchu.so`, API 37 images) crashes on wgpu's submissions
  that mix a binary wait with a timeline signal. Instead of rebuilding wgpu-native with the earlier wgpu-hal patch,
  `src/platform/android_vulkan.c` pads both timeline value arrays of each such `vkQueueSubmit` on emulators only:
  Android builds link with `--wrap=dlsym` (a usage requirement of this library), so wgpu-native's lookup of
  `vkGetInstanceProcAddr` returns a wrapper there; every other `dlsym` passes through.
- At run time on Linux: `libpango-1.0`, `libpangocairo-1.0`, `libcairo`, `libgobject-2.0`, `libglib-2.0` when present
  (otherwise text uses the built-in bitmap font).

## Frames, captures and tests

`Processing.run()` is a frame driver on `aqua_schedule` (aligned with animation frames on the web): it dispatches the
frame's events (callbacks may wait for asynchronous results), calls `draw`, presents and paces to `frameRate()`.
`AQUARIUS_GRAPHICS_FRAMES=N` ends `run()` after N drawn frames and `AQUARIUS_GRAPHICS_CAPTURE=file.png` saves the last
one, as before. `AQUARIUS_GRAPHICS_RECORD=frames/####.png` saves every drawn frame (the `#`s become the zero-padded
frame number) and makes `millis()` advance exactly one frame period per frame, so a recording plays back smoothly at the
sketch's `frameRate()` however long each frame took to save. Tests: `tests/*.aqua`; a first-line comment says when a
case needs a window or a GPU device.

## Speed

Processing draws are batched: consecutive draws with the same pipeline, texture, sampler, scissor and uniform snapshot
become one GPU draw (`src/gpu/target.c`). With the default shader the model matrix is applied to the vertices on the
CPU, so `push()`/`translate()`/`rotate()`/`pop()` around each shape no longer breaks a batch; scripts' shaders keep
receiving `processing.model`. Ellipses and arcs take 20..200 segments from their size on screen (as Processing's OpenGL
renderers do). Canvas `set()` draws one device-pixel quad (a loop of `set()` calls is one draw), reads (`get()`,
`loadPixels()`, `filter()`, `copy()`) reuse the last readback until something is drawn, `loadPixels()` refreshes its
array in place and `updatePixels()` reads it in bulk.

## Processing API coverage

Beyond the earlier implementation, the module has: blend modes `SUBTRACT`, `LIGHTEST`, `DARKEST`, `EXCLUSION` (in
`blendMode()`; `blend()` and `blendColor()` also take `DIFFERENCE`, `OVERLAY`, `HARD_LIGHT`, `SOFT_LIGHT`, `DODGE`,
`BURN`), `blend()` on images and canvases, filters `DILATE` and `ERODE`, `copy()` within an image or canvas,
`background(image)`, `textureWrap(CLAMP | REPEAT)` (and nearest sampling under `noSmooth()`), `rotate(angle, x, y, z)`,
`applyMatrix()` with 6 or 16 numbers, `frustum()`, `beginCamera()`/`endCamera()`, `hint()` (`DISABLE_DEPTH_TEST`,
`DISABLE_DEPTH_MASK` and their `ENABLE_` forms act; the other hints are accepted), `createShape(GROUP)` and
`createShape(POINT | LINE | TRIANGLE | QUAD | RECT | ELLIPSE | ARC | SPHERE | BOX, ...)` taking the canvas's style,
`shapeMode()`, `shape(s, x, y, w, h)`, PShape `beginContour`/`endContour`, `getChild`/`getChildCount`,
`setStroke`/`setStrokeWeight`, `rotateX/Y/Z`, `scale(x, y[, z])`, `setVisible`/`isVisible`,
`disableStyle`/`enableStyle`, `getWidth`/`getHeight`, `loadShape()` for `.obj` models, PVector `fromAngle()`,
`random2D()`, `random3D()` (module functions), `setHeading()` and `setX/setY/setZ()` (the runtime has no property
setters), `year()`..`second()`, `nf`, `nfs`, `nfp`, `nfc`, `hex`, `unhex`, `binary`, `unbinary`, `str`, `trim`,
`split`, `splitTokens`, `join`, `cursor(ARROW | CROSS | HAND | MOVE | TEXT | WAIT)` and `cursor(image[, x, y])`,
`windowTitle()`, `pixelDensity()`, `focused`, `requestImage()`, `saveFrame()` without a name, and the key constants
`UP`, `DOWN`, `LEFT_ARROW`, `RIGHT_ARROW`, `SHIFT`, `CONTROL`, `ALT`, `ENTER`, `RETURN`, `BACKSPACE`, `TAB`, `DELETE`,
`INSERT`, `HOME`, `END`, `PAGE_UP`, `PAGE_DOWN` (the keypad's keys with Num Lock off arrive as these)
(GLFW key codes, like `keyCode`; `LEFT` and `RIGHT` stay the alignment and mouse-button constants). Not provided:
SVG in `loadShape()`, PDF recording, `textMode()`, `loadFont()` (`.vlw`; use `createFont()`), `delay()`, and the
data classes (`Table`, `JSONObject`, `IntList`, ...), which the language's own arrays and hashes cover.

Other libraries get the windows as shared window objects: `Processing.platformWindow` / `平台視窗` (null before
`size()`) and `PlatformWindow` of a WGPU window (null once disposed), for example
`Interaction.Attach(p.platformWindow)` for native-feeling input ([interaction](../interaction/README.md)).

## Differences from the earlier implementation

Behavior, names, aliases, argument counts, results and messages follow the earlier desktop implementation (checked
case by case against it: the tests and examples produce the same output and the same captured pixels). Differences:

- Restrictions of the C# GLFW host are gone: Processing sketches, WGPU windows and other libraries' windows can be open
  together, and more than one WGPU window may exist ("This runtime supports one WGPU window at a time." and the
  "Close raw GLFW windows" / "Dispose WGPU windows" errors no longer occur).
- Wrong argument counts are reported by the runtime's signature check, in its wording.
- `Processing.close()` inside `run()` ends the run cleanly after the current frame (it used to fail the run).
- Records (TextInput events, Fonts rectangles, Window capabilities and events) are `record` objects with the same
  fields; Processing and WGPU objects report their class (`Processing.Canvas`, `WGPU.Device`, ...) instead of `module`.
- Window integration: a `fileDrop` event's `files` are the dropped paths (strings); pass them to the application
  library's `Files.Resolve` for file resources (resource objects belong to that library).
- Objects are released when the script no longer reaches them (finalizers), instead of by the earlier periodic scan.
- Web: `Fonts.Measure`/`Selection` return whole-pixel boxes (advance width, font ascent to descent) like the desktop
  instead of fractional Canvas metrics; `Window.ResolveClose("cancel")` turns on the browser's leave confirmation,
  `"accept"` turns it off, `"defer"` fails as Unsupported (as before); `Save`/`saveFrame` download the PNG;
  `WGPU.Window.Poll()` and a hand-driven `Processing.endFrame()` wait for the next animation frame.
- Android: text through `android.graphics.Paint` per line; installed faces are listed as `<file>:<ttc index>` (as the
  earlier Android runtime named them) next to `sans-serif`, `serif` and `monospace`.
