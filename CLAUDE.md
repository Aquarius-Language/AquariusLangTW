# Rules for changing AquariusLangTW

Read this before every change. These rules are the project's architecture; a change that breaks one is wrong even if
it works. (繁中摘要見文末。)

## The rules

1. **C# is only the compiler.** C# lives in `compiler/` and nowhere else: lexer, parser, lowering, WebAssembly
   emission, packaging and the tools around them (CLI, build targets, Bottle, language server, tests). No target's
   runtime and no library contains C#, and nothing at run time loads .NET.
2. **One native runtime, thin per-OS layers.** Applications are `.wasm` modules run by `runtime/`: Wasmtime's C API on
   desktop and mobile, the browser's WebAssembly on the web. The host core (`runtime/host`) is shared; everything that
   differs per OS is a thin layer in `runtime/platform/<os>/` (Java on Android only in its activity shim, which only
   forwards what Android delivers to Java alone).
3. **Libraries are AquariusLang, C, C++ or Rust.** Write a library in AquariusLang when it needs no native code;
   otherwise in clean, safe, readable modern C (C17), C++ (C++20) or Rust (C ABI), without sacrificing performance. One
   source tree serves every target: preprocessor branches on `AQUA_OS_*` for the few differences, and a
   `src/platform/<os>.c` file when a branch grows beyond a few lines. Never generate code with scripts (no Python
   generators): use X-macro tables (`.def` files) for long lists.
4. **One root folder per library.** Everything a library is lives in `libraries/<name>/` (manifest `aqua.toml`,
   `CMakeLists.txt`, `src/`, `aqua/`, `third_party/` or pinned downloads, `tests/`, `README.md`), so the folder can
   become its own repository linked back as a submodule without changing anything else. A library's only outside
   dependencies are the SDK (`runtime/sdk`: `aqua/aqua.h`, `window.h`, `image.h`, `android.h`) and the web glue
   domains it calls. Its third-party code is pinned (version + SHA-256) inside its folder, compiled into it with
   private symbols, with licenses kept.
5. **JavaScript only in `web-glue/`, only to expose Web APIs.** A glue function translates arguments in memory, makes
   one Web API call (or starts one asynchronous operation) and hands the result back. Parsing, layout, math, protocol
   logic, validation, caching policy and every algorithm stay in C/C++/Rust so all targets run the same code. No
   library ships JavaScript, and no library may be written in JavaScript.
6. **One unified glue, one folder per Web API.** `web-glue/core` starts applications and links side modules;
   `web-glue/<domain>/` (window, webgpu, audio, network, storage, ...) each expose one Web API, register with
   `core/glue.mjs` and are imported once in `web-glue/index.mjs`. Every domain is usable by any internal or external
   library and is self-contained (it could move to its own repository).
7. **LLVM Clang everywhere.** Every C/C++ binary for every target is built with the pinned Clang through the CMake
   toolchain files in `runtime/sdk/cmake/toolchains/` (pins in `scripts/toolchain.json`). Windows is Clang + MinGW
   (llvm-mingw sysroot), never MSVC. Android uses the pinned NDK's Clang.
8. **External libraries look exactly like default ones.** A Bottle library is Aquarius sources plus, optionally, a
   native part built from the same kind of folder with `aqua_add_library`; its modules register through `aqua.h`
   (`AQUA_LIBRARY`) and bind to the runtime through the function table. No other mechanism (no buffer ABIs, no
   JavaScript libraries, no per-language bindings).
9. **Keep it simple.** No unnecessary layers, options or abstractions; remove what the architecture no longer needs
   instead of leaving it beside the new code. Adding the next library, glue domain or target must follow the existing
   pattern without touching unrelated code.
10. **More targets will come.** Never hard-code the list of targets outside the places that define them
    (`scripts/toolchain.json`, `runtime/sdk/cmake/toolchains/`, `runtime/platform/<os>/`, the compiler's build
    targets). Isolate OS code so a new target (macOS, iOS, ...) is a new toolchain file, a new platform layer, a new
    `src/platform/<os>.c` in the libraries that need one, and a new build target.
11. **Target-specific speed is optional and layered.** Every target runs the same portable artifact: the compiler
    and the guest runtime emit one `.wasm` with no target branches, and it must stay correct and complete on every
    target. A target may add optimizations on top of it (engine settings, host fast paths, SIMD or other CPU features
    chosen by capability detection) only in its engine or platform layer (`runtime/host/engine_*.c`,
    `runtime/platform/<os>/`) or in a library's `src/platform/<os>.c`, always with the portable path as the
    fallback. Such an optimization must give identical observable results (output, errors, numeric rules), never
    remove or limit what any target can compile or run, and be covered by a test that compares it with the portable
    path. Portable optimizations (in the compiler or the guest runtime) are preferred because every target gains. The
    desktop runtime JIT (`runtime/platform/desktop/jit.c`) is such a layer: it builds Wasm modules while the program runs
    from the typed variants (stencils) the compiler stores in the artifact, and is the only Wasm encoder outside the
    compiler's `WasmEncoding.cs`; `AQUARIUS_JIT=0` turns it off.

## Layout

```text
compiler/            C#: AquariusCompiler, AquariusBuild, AquariusBottle, AquariusCli, AquariusLanguageServer, AquariusTests
runtime/
  guest/             runtime.c: the guest runtime linked into every application (values, GC, scheduler), layout.def
  host/              the shared C host: application metadata, imports, registry, scheduler, calls, core library
  platform/<os>/     windows, linux, posix, desktop (Windows+Linux), android (+ java/ activity shim), web
  sdk/               the library SDK: include/aqua/*.h, api.def, cmake/ (AquariusLibrary.cmake, toolchains/), example/
libraries/<name>/    default libraries (application, audio, camera, graphics, input, network, onnx, opengl, terminal)
web-glue/            core/, page/, index.mjs and one folder per Web API domain
scripts/             toolchain pins and installers, runtime/library builds, web-run.mjs, android-run.ps1, release
```

## How to add things

- **A default library:** copy the shape of an existing one (start from `runtime/sdk/example/`): `aqua.toml`
  (`manifest_version = 2`, `[library]`, `[native] modules`, `[[modules]]` for Aquarius sources, `[android]
  permissions`), `CMakeLists.txt` with `aqua_add_library`, `src/`, `tests/<case>.aqua` + `<case>.expected`, README.
  Nothing outside the folder changes, except an import line in `web-glue/index.mjs` if it adds a glue domain.
- **A glue domain:** `web-glue/<domain>/<domain>.mjs` registering with `registerGlue`; functions named
  `<domain>_<operation>` (or a standard C API's own names, like `webgpu.h`'s); asynchronous results through
  `context.callPointer` + `context.wake()`. See `web-glue/README.md`.
- **An SDK function:** declare it in `aqua/*.h`, append it to `api.def` before the platform-specific block, implement
  it in `runtime/host` or the platform layers, raise `AQUA_API_VERSION` once released.
- **A target:** a toolchain file, a `runtime/platform/<os>` layer, pins, the compiler's build target, and the
  libraries' platform branches. Nothing else should need to know.

## Building and testing

```powershell
scripts/install-toolchain.ps1 -Targets windows-x64,android-x86_64     # pinned LLVM, MinGW sysroot, CMake, Ninja, SDKs, JDK
scripts/build-runtime.ps1 -Targets windows-x64,web,android-x86_64     # runtime packs into .native-build/install/<target>
dotnet build AquariusLang.sln; dotnet test compiler/AquariusTests
node scripts/web-run.mjs <runtime pack> app.wasm --libraries <name>    # web tests in Chromium (npm install in scripts/)
scripts/android-run.ps1 app.wasm -Runtime <android pack> -Run          # Android tests on a device or emulator
./build-release.ps1                                                    # the release: packs, tests, dist/aqua, VSIX
```

Every library's `tests/` must pass on every target it supports before a change is done. Windows builds use Clang +
MinGW only; Linux builds run in WSL (`AQUARIUS_LLVM`, see `runtime/sdk/README.md`).

## Before you finish a change, check

- No `.cs` outside `compiler/`; no JavaScript in `runtime/` or `libraries/` (only `web-glue/`, plus development tools
  that never ship with applications: `scripts/`, `editors/vscode`, `website/`); no Python or code generators.
- The library's folder is self-contained; its dependencies are pinned in it; its symbols stay private.
- Glue functions only call Web APIs; the logic is in C/C++/Rust.
- No MSVC, no target list hard-coded in a new place, no leftover code of an old design.
- Target-specific optimizations live only in engine/platform layers, keep the portable path and have a comparison test.
- Docs and tests updated with the change.

## 繁中摘要

C# 只是編譯器；各平台執行環境是原生的 C（桌面與行動用 Wasmtime，網頁用瀏覽器的 WebAssembly），平台差異放在很薄的
`runtime/platform/<os>`。預設與外部函式庫只能用星泉、C、C++、Rust 撰寫，每個函式庫一個根資料夾、一份原始碼以前置處理
器跨平台編譯。JavaScript 只在 `web-glue/`，只用來把網頁 API 交給 WebAssembly 使用，不得撰寫函式庫邏輯。所有平台都用
LLVM Clang（Windows 為 Clang + MinGW，不用 MSVC）。保持簡單、不留舊架構的東西，並為未來的新平台預留清楚的擴充點。
各平台可在自己的引擎或平台層（`runtime/host/engine_*.c`、`runtime/platform/<os>`）加入選用的效能最佳化（引擎設定、SIMD 等，依能力偵測選用），但所有平台執行同一份可攜 `.wasm`，最佳化必須保留可攜路徑作為後備、結果完全相同，並有比對測試；不得削弱任何平台的編譯與執行能力。
