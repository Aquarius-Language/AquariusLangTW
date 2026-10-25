# AquariusLang native library SDK

Everything a library needs to extend AquariusLang on every target from one source tree. Default libraries
(`libraries/<name>/`) and external libraries (Bottle packages) use the same SDK; only the build mode differs.

| Target | What a library builds to | How the runtime uses it |
| --- | --- | --- |
| `windows-x64` | Clang + MinGW (never MSVC) | default: linked into `aquarius.exe`; external: `aqua_<name>.dll` |
| `linux-x64` | Clang + glibc | default: linked into `aquarius`; external: `libaqua_<name>.so` |
| `android-arm64-v8a`, `android-x86_64` | NDK Clang | default: linked into `libaquarius.so`; external: `libaqua_<name>.so` |
| `web` | Clang `wasm32-wasip1`, position independent | a side module `<name>.wasm` sharing the application's memory |

C, C++ (C++20, no exceptions on the web) and Rust (through a C ABI) are the only implementation languages. JavaScript
exists only in `web-glue/`, and only to expose Web APIs; a library never ships JavaScript.

## A library's folder

Everything a library is lives in its folder, so the folder can become its own repository and be linked back as a
submodule without changing anything else:

```text
libraries/<name>/
  aqua.toml          manifest: name, version, platforms, Aquarius-source modules, native module names
  CMakeLists.txt     aqua_add_library(<name> SOURCES ...)
  src/               C/C++ sources: one tree, AQUA_OS_* preprocessor branches where targets differ
  aqua/              Aquarius-source modules the compiler links into applications that import them
  third_party/       vendored dependencies with their licenses (built static, symbols kept private)
  tests/             .aqua programs with expected output
  README.md
```

The only things a library may include from outside its folder are this SDK's headers (`<aqua/aqua.h>`,
`<aqua/window.h>`, `<aqua/android.h>`) and the pinned SDKs the toolchain provides (`webgpu.h` from wgpu-native). Its web
counterpart is a domain of `web-glue/` used by any library that needs the same Web API.

## Writing functions

```c
#include <aqua/aqua.h>

static void add(aqua_call *call) {
    aqua_return_double(call, aqua_arg_number(call, 0) + aqua_arg_number(call, 1));
}

AQUA_LIBRARY(sample) {
    aqua_class *module = aqua_define_module(library, "Sample|範例");
    aqua_define_function(module, "add|相加", add, "nn");
    aqua_define_int(module, "VERSION|版本", 1);
}
```

- **Names** are `|`-separated aliases, English first, then Traditional Chinese: both spell the same function.
- **Signatures** make the runtime check arguments before the call, so a function only reads what it declared:
  `n` number, `i` integer, `b` boolean, `s` string, `a` array, `h` hash, `f` function, `o` native object, `v` any;
  `|` starts optional arguments (which may also be passed as null); `*` repeats the previous kind.
- **Results**: `aqua_return_*`; a function that returns nothing yields no value. **Errors**: `aqua_fail(call, ...)`
  fails the calling script with a language error; return right after it.
- **Text** is UTF-8 at this API (`aqua_arg_str`, `aqua_return_text`); guest strings are UTF-16 inside, converted here.
- **Arrays and hashes** are read and written in place (`aqua_array_*`, `aqua_hash_*`); use the bulk forms
  (`aqua_array_read_f32`, `aqua_array_write_u8`, ...) for numeric data.
- **Native objects**: `aqua_define_class(library, "Canvas", finalize)` plus methods and properties; create instances
  with `aqua_object_new`. The finalizer runs once no script and no `aqua_retain` reference reaches the object.
- **Properties** (`aqua_define_property`) are re-read on every access, for live values such as `mouseX`.
- **Per-runtime state**: `aqua_library_state(library, create, destroy)`; read it with `aqua_state(call)`.
- **Values that outlive a call** (callbacks, cached arrays) must be held with `aqua_retain` and released with
  `aqua_release`; anything else may be collected once the guest runs again.

### Callbacks, waiting and frames

- `aqua_invoke` runs a script function to completion. Use it only for code that cannot wait (comparators). On the web
  a callback that waits for a Web API cannot run synchronously.
- `aqua_invoke_async` runs a callback that may wait (event handlers, `draw`); `done` runs when it finishes.
- `aqua_defer(call)` makes the calling script wait: deliver the result later on the runtime's thread with
  `aqua_resolve` or `aqua_reject`. Every asynchronous Web API (fetch, GPU readback, pickers) is a deferred call on the
  web; the same function may complete immediately on native targets.
- `aqua_post` is the only API other threads may call: it runs a task on the runtime's thread.
- `aqua_schedule` registers a tick; frame drivers return the delay to their next frame (aligned with animation frames
  on the web). The application keeps running while ticks are scheduled.

### Windows and input

`<aqua/window.h>` gives every target one window, input and surface interface (GLFW codes everywhere). Graphics
libraries create surfaces from `aqua_window_surface` (HWND, X11/Wayland, ANativeWindow, or a canvas handle on the web)
and receive input through `aqua_window_listen`. Libraries never call GLFW, Win32 windowing or DOM APIs directly.

### Platform branches

Use `#if defined(AQUA_OS_WINDOWS)` / `AQUA_OS_LINUX` / `AQUA_OS_ANDROID` / `AQUA_OS_WEB` (and `AQUA_DESKTOP`,
`AQUA_MOBILE`) around the few calls that differ. On Android, `<aqua/android.h>` gives the JNI environment and activity
for services only Java exposes. On the web, declare each glue function the library calls:

```c
#if defined(AQUA_OS_WEB)
AQUA_WEB_IMPORT(net_fetch) int32_t web_fetch(const char *url, uint32_t length, void (*done)(void *, int32_t), void *data);
#endif
```

and implement it in the matching `web-glue/<domain>/` file (see `web-glue/README.md`). C APIs the glue implements by
their standard names (`webgpu.h`'s `wgpu*`, OpenGL ES's `gl*`) are simply called.

## Building

```powershell
scripts/install-toolchain.ps1 -Targets windows-x64,android-arm64-v8a,android-x86_64
cmake -S runtime -B .native-build/runtime/windows-x64 -G Ninja -DCMAKE_TOOLCHAIN_FILE=runtime/sdk/cmake/toolchains/windows-x64.cmake -DCMAKE_BUILD_TYPE=Release
cmake --build .native-build/runtime/windows-x64
# A subset of default libraries while developing one: -DAQUA_LIBRARIES=graphics
```

The same with `web.cmake` builds `aquarius.wasm` and every library's side module; `cmake --install <build> --prefix
<dir>` gathers a target's runtime files. Linux builds run under WSL with the Linux LLVM release (`AQUARIUS_LLVM`).

`aqua_add_library(<name> SOURCES ... [INCLUDE_DIRECTORIES ...] [LINK_LIBRARIES ...] [DEFINITIONS ...]
[NATIVE_SOURCES ...] [WEB_SOURCES ...])` builds the library for the configured target. Vendored C/C++ dependencies are
compiled into the library itself with their symbols private (`static` single-header libraries, or a `-D` prefix), so
two libraries vendoring the same code never clash when the runtime links them together.

## Testing

```powershell
dotnet compiler/AquariusCli/bin/Release/net8.0/aqua.dll build tests/sample.aqua -o .native-build/tests/sample.wasm
.native-build/runtime/windows-x64/aquarius.exe .native-build/tests/sample.wasm
node scripts/web-run.mjs .native-build/install/web .native-build/tests/sample.wasm --libraries sample
```

`AQUARIUS_GRAPHICS_FRAMES=N` stops frame drivers after N frames and `AQUARIUS_GRAPHICS_CAPTURE=file.png` saves the last
one (`--frames N` and `--screenshot` with `web-run.mjs`). `aquarius --catalog` prints every module the runtime
registers, which editor tooling reads.
