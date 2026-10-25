# web-glue/webgpu — webgpu.h on the browser's WebGPU

Status: complete

The C API of wgpu-native 29.0.1.1's `webgpu.h` implemented on `navigator.gpu`, so C/C++ code written against
wgpu-native on Windows, Linux and Android runs unchanged on the web. The loader (`../core/loader.mjs`) binds a side
module's undefined `env` imports to glue functions of the same name: a library just includes `webgpu.h` and calls
`wgpuDeviceCreateBuffer`, `wgpuQueueSubmit`, ... with no declarations and no web branch.

| File | What it is |
| --- | --- |
| `webgpu.mjs` | the glue domain `webgpu`: all 202 functions of `webgpu.h`, by their standard names |
| `layout.mjs` | the webgpu.h structs on wasm32: field lists in declaration order, offsets computed by the C rules, read/write |
| `enums.mjs` | webgpu.h enum values and their WebGPU strings (texture/vertex formats, features, blend factors, ...) |
| `include/webgpu_web.h` | `WGPUEmscriptenSurfaceSourceCanvasHTMLSelector` (sType `0x00040000`, as in Emscripten's webgpu.h) |
| `tests/` | a test-only library (`WebGPUTest`) and the programs that run it on every target |

`wgpu.h` (wgpu-native's native-only extensions) is not implemented.

## Using it from a library

```cmake
# A library pins wgpu-native in its own folder (see libraries/graphics/CMakeLists.txt) and, on the web, takes only
# webgpu.h from the host's release plus this domain's include/ folder:
if(AQUA_TARGET STREQUAL "web")
    set(webgpu_includes "${wgpu_native}/include" "<repository>/web-glue/webgpu/include")
else()
    set(webgpu_includes "${wgpu_native}/include")   # and link "${wgpu_native}/lib/libwgpu_native.a"
endif()
```

A window's surface comes from `aqua_window_surface`; on the web its kind is `AQUA_SURFACE_CANVAS` and the handle is
the number in the canvas id `aqua-window-<n>`:

```c
#include <webgpu/webgpu.h>
#if defined(AQUA_OS_WEB)
#include <webgpu_web.h>
#endif
    case AQUA_SURFACE_CANVAS: {   /* inside #if defined(AQUA_OS_WEB) */
        char selector[32];
        snprintf(selector, sizeof selector, "#aqua-window-%u", (unsigned)(uintptr_t)surface.handle);
        WGPUEmscriptenSurfaceSourceCanvasHTMLSelector source = WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
        source.selector = (WGPUStringView){selector, WGPU_STRLEN};
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
```

`tests/src/webgpu_test.c` shows the whole pattern (surfaces of every platform, a frame driver, compute, readback).

### Asynchronous operations

Every asynchronous function takes its `WGPU*CallbackInfo` and returns a `WGPUFuture` (a BigInt id on the JavaScript
side). When the browser's promise settles, the callback is called according to its mode:

- `AllowSpontaneous`: at once, between runtime turns (the page has one thread, so never inside another wgpu call).
- `AllowProcessEvents`: queued until `wgpuInstanceProcessEvents` (or a `wgpuInstanceWaitAny` naming its future).
- `WaitAnyOnly`: only from `wgpuInstanceWaitAny`.

The glue then calls `context.wake()`, so the runtime takes a turn. The portable pattern is an `aqua_schedule` tick
that calls `wgpuInstanceProcessEvents` and checks what its callbacks recorded: wgpu-native polls its devices there,
the web delivers the settled callbacks there. **`wgpuInstanceWaitAny` only polls on the web**: with a timeout of 0 it
fires the completed futures' callbacks and returns `Success` or `TimedOut`; any other timeout returns `Error`
(there is no `TimedWaitAny` instance feature: a page cannot block its thread). Device lost and uncaptured error
callbacks come from the device descriptor; the lost future is `wgpuDeviceGetLostFuture`.

## Conventions

- **Handles**: every WebGPU object crosses as a glue handle (`context.retain`) of a `{gpu, refs, instance}` record.
  `AddRef`/`Release` count references; the last `Release` drops the handle and leaves the object to the garbage
  collector (WebGPU keeps it alive while bind groups or commands use it, as wgpu-native does). `wgpuDeviceGetQueue`
  returns the same handle each time, with one more reference.
- **Memory**: wasm32: pointers, `size_t`, enums and `WGPUBool` are 4 bytes, `uint64_t` and `double` 8-byte aligned.
  Structs passed by value (callback infos, `WGPUStringView` labels, `*FreeMembers` arguments) arrive as pointers;
  `uint64_t` arguments arrive as signed BigInts and `WGPUFuture`/`uint64_t` results return as BigInts. Callbacks
  receive their by-value `WGPUStringView` (and the device-lost/uncaptured-error `WGPUDevice const *`) in a temporary
  block freed after the call.
- **Sentinels and defaults**: `*_Undefined` enum values and `WGPU_*_UNDEFINED` fields become absent members, so
  WebGPU applies its defaults; `WGPU_WHOLE_SIZE`/`WGPU_WHOLE_MAP_SIZE` mean "to the end"; `WGPU_STRLEN` strings are
  NUL-terminated and `{NULL, WGPU_STRLEN}` is null (no label, the module's only entry point); a null pipeline layout
  is `'auto'`; holes (vertex buffers without step mode and attributes, color targets without format, color
  attachments without view) become `null`; `WGPUOptionalBool_Undefined` leaves `depthWriteEnabled` unset.
- **Mapping**: `wgpuBufferGetMappedRange`/`GetConstMappedRange` return malloc'd copies of the mapped bytes, valid
  until unmap; unmapping a buffer mapped for writing (or `mappedAtCreation`) writes the mutable ranges back first.
  The mapping is fetched once per map, so overlapping ranges work as webgpu.h allows. `wgpuBufferReadMappedRange` and
  `WriteMappedRange` copy directly and are the fast path.
- **Results written by the glue**: `wgpu*GetFeatures`, `GetInfo`, `GetLimits`, `wgpuSurfaceGetCapabilities` and
  `wgpuInstanceGetWGSLLanguageFeatures` allocate with `malloc`; the matching `*FreeMembers` frees.

## Differences from wgpu-native

The web side behaves as webgpu.h specifies; where WebGPU has no equivalent:

- `wgpuSurfacePresent` returns `Success` and does nothing: the browser presents the canvas after the frame.
  `wgpuSurfaceConfigure` sets the canvas's pixel size to the configured width and height. Capabilities: the preferred
  canvas format first, then `bgra8unorm`/`rgba8unorm`/`rgba16float`; usages CopySrc, CopyDst, TextureBinding,
  RenderAttachment; present mode Fifo; alpha modes Opaque and Premultiplied (Auto and Inherit are opaque).
- `wgpuGetProcAddress` returns NULL (JavaScript functions cannot become C function pointers). No instance features,
  `timedWaitAnyMaxCount` 0.
- Shader modules are WGSL only: a SPIR-V descriptor returns NULL (and logs an error).
- Adapter info: backend `WebGPU`, adapter type `CPU` for a fallback adapter and `Unknown` otherwise, vendor and
  device ids 0.
- Required limits the browser does not know (for example `maxImmediateSize`) and features without a WebGPU name
  (wgpu.h's native features) are left out of `requestDevice`. `GetLimits` writes 0 for limits the browser lacks.
- Compilation message positions are WebGPU's (UTF-16 code units); they equal webgpu.h's UTF-8 bytes for ASCII sources.
- Browsers deliver uncaptured errors once the device processes queue work (after a submit), not during the call.
- `wgpuCommandEncoderWriteTimestamp` and `wgpu*SetImmediates` call the browser's method where it has one.

wgpu-native 29.0.1.1 in turn leaves these webgpu.h functions unimplemented (they abort), so portable code avoids them
or calls them only on the web: `wgpuDeviceCreate{Compute,Render}PipelineAsync`, `wgpuShaderModuleGetCompilationInfo`,
`wgpuBufferGetMapState`, `wgpuBufferReadMappedRange`, `wgpuBufferWriteMappedRange`, `wgpuInstanceWaitAny`,
`wgpuDeviceGetLostFuture`, `wgpuDeviceGetAdapterInfo`, `wgpuInstance{Get,Has}WGSLLanguageFeature(s)`,
`wgpuGetInstanceFeatures`, `wgpuHasInstanceFeature`, `wgpuTextureGetTextureBindingViewDimension`, every `*SetLabel`,
and `wgpuExternalTexture*`. It also takes `WGPU_WHOLE_MAP_SIZE` in `wgpuBufferMapAsync` literally (pass the size),
and treats submitting commands that use an invalid object as fatal.

## Testing

`tests/` is a test-only external library (`aqua_add_library(webgpu_test ...)`, module `WebGPUTest`):

- `webgpu.aqua` (`WebGPUTest.run()`, every target): adapter, limits, features and info; a device; a WGSL compute
  shader doubling numbers, mapped back; an error scope catching a validation error; a window rendering a triangle
  for 3 frames driven by `aqua_schedule`, the last frame's surface texture copied and read back; the triangle
  rendered into a texture and read back. The same C code and the same output (`webgpu.expected`) on every target.
- `webgpu_extras.aqua` (`WebGPUTest.extras()`, web only): the functions wgpu-native leaves unimplemented,
  asynchronous pipelines, compilation info, overlapping mapped ranges, map state, WaitAny, uncaptured errors and
  device loss (`webgpu_extras.expected`).

```bash
cd /c/OfficialProjects/AquariusLangTW
dotnet .native-build/compiler/aqua.dll build web-glue/webgpu/tests/webgpu.aqua -o .native-build/lib-webgpu/apps/webgpu.wasm
# Windows (wgpu-native, linked statically)
cmake -S web-glue/webgpu/tests -B .native-build/lib-webgpu/windows-x64 -G Ninja -DCMAKE_MAKE_PROGRAM=C:/Strawberry/c/bin/ninja.exe \
  -DCMAKE_TOOLCHAIN_FILE=$PWD/runtime/sdk/cmake/toolchains/windows-x64.cmake -DCMAKE_BUILD_TYPE=Release \n  -DWGPU_NATIVE=<an unpacked wgpu-native 29.0.1.1 release, e.g. the one a graphics build extracts>
cmake --build .native-build/lib-webgpu/windows-x64
.native-build/runtime/windows-x64/aquarius.exe --library .native-build/lib-webgpu/windows-x64/aqua_webgpu_test.dll .native-build/lib-webgpu/apps/webgpu.wasm
# Web: the same with web.cmake gives webgpu_test.wasm; copy it into <runtime web install>/libraries/, then
node scripts/web-run.mjs <runtime web install> .native-build/lib-webgpu/apps/webgpu.wasm --libraries webgpu_test
```

Linux builds the same way with `linux-x64.cmake` (`libaqua_webgpu_test.so`); Android with the Android toolchain files.
