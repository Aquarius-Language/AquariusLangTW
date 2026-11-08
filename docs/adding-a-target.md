# Adding a target

A target is one runtime pack: `windows-x64`, `linux-x64`, `android-arm64-v8a`, `android-x86_64` and `web` today.
`macos-arm64` and `ios-arm64` are reserved as *planned*: every tool knows their names and reports them as "planned but
not built yet" instead of "unknown", and nothing builds them. This page lists what adding one takes and, for macOS and
iOS, what is already in place and what is still Linux- or Windows-only.

## Where targets are defined

| Place | What it says |
| --- | --- |
| `scripts/toolchain.json` `targets.list` | The table: `name`, `platform` (the OS as `aqua.toml`, application metadata and `AQUA_OS` name it), `kind` (`desktop`, `mobile`, `web`), `hosts` (who can build it; default: itself for desktop, any host for mobile and web), `dotnet` (a desktop host's .NET runtime identifier) and `planned`. |
| `compiler/AquariusCompiler/RuntimeTarget.cs` | The compiler's copy of the same table, plus file naming (`NativeFile`, `SharedLibrarySuffix`, `ExecutableSuffix`) and `BuildsOn`. `ArchitectureRulesTest.OneTargetTableDescribesEveryTarget` fails when the two differ. |
| `scripts/toolchain.ps1` | Derives `$AquaTargets`, `$AquaDesktopTargets`, `$AquaPlannedTargets` and `Get-AquaTarget` from the table. |
| `runtime/sdk/cmake/toolchains/AquariusToolchain.cmake` | Reads the toolchain file's `AQUA_TARGET` row and sets `AQUA_OS` and `AQUA_KIND` for the runtime and every library. |
| `runtime/sdk/cmake/toolchains/<target>.cmake` | The compiler, sysroot and flags. It sets `AQUA_TARGET` and then includes `AquariusToolchain.cmake`. |
| `runtime/platform/<platform>/platform.cmake` | What the runtime builds for that OS. `runtime/CMakeLists.txt` includes it by `AQUA_OS` and knows no target names. Desktop OSes set their sources and libraries and include `platform/desktop/desktop.cmake` (GLFW and the two launchers). |
| `compiler/AquariusBuild/BuildTargets.cs` | The `aqua build --target <platform>` backends. |

Nothing else should list targets: `ArchitectureRulesTest.TargetListsAreNotRepeated` fails on a literal list of three or
more target names outside the table.

## Steps

1. **Table.** Add the row to `targets.list` and to `RuntimeTarget.All`, or remove `planned` from both for a reserved
   target.
2. **Pins.** In `scripts/toolchain.json`, add the target's prebuilt SDKs (`sdks.wasmtime.<target>`). If the target can
   also be a build host, add host archives for `llvm`, `cmake`, `ninja` and `jdk` too. Libraries pin their own
   downloads in their `CMakeLists.txt` (wgpu-native in `libraries/graphics`, ONNX Runtime in `libraries/onnx`).
3. **Toolchain file.** Add `runtime/sdk/cmake/toolchains/<target>.cmake` with the pinned Clang. It must set
   `AQUA_TARGET` before `include(.../AquariusToolchain.cmake)`.
4. **Platform layer.** Add `runtime/platform/<platform>/`: `platform.cmake` and the C layer (process entry, files,
   clocks, dynamic loading, and the window and input implementation of `<aqua/window.h>`). Reuse `posix/` and
   `desktop/` where they fit.
5. **Libraries.** Each library with platform code needs a branch or a `src/platform/<platform>.c` for the new OS (rule
   3). Some `CMakeLists.txt` files still compare whole target names (`AQUA_TARGET STREQUAL "windows-x64"`); new
   branches use `AQUA_OS`. `aqua_add_library(PLATFORMS ...)` and `[library] platforms` in `aqua.toml` say where a library builds and runs.
6. **Build target.** Register a backend in `BuildTargets.Default` (for a desktop OS this is
   `new ExecutableTarget("<platform>", "<target>")`).
7. **Scripts and CI.** `build-runtime.ps1`, `test-libraries.ps1`, `install-toolchain.ps1` and `build-release.ps1`
   take the target from the table. Add a workflow under `.github/workflows/` that builds the pack and runs
   `dotnet test` and `test-libraries.ps1` on that host.
8. **Docs.** Add a `docs/<platform>.md` like [linux.md](linux.md), and update the target lists in
   [ARCHITECTURE.md](../ARCHITECTURE.md) and the libraries' READMEs.

## macOS (`macos-arm64`)

Already in place:

- Host pins for Apple Silicon: LLVM, CMake and Ninja in `scripts/toolchain.json`. `toolchain.ps1`,
  `install-toolchain.sh`, `AquariusToolchain.cmake` and `AquariusInstallation.HostPlatform` recognise a macOS arm64
  host.
- `<aqua/aqua.h>` defines `AQUA_OS_MACOS`, `AQUA_DESKTOP` and `AQUA_PLATFORM_NAME "macos"`. The `terminal` library
  already treats macOS as POSIX.
- Naming: `RuntimeTarget.NativeFile` gives `libaqua_<name>.dylib`, which is what `aqua_add_library` builds by default
  on Apple platforms. The desktop launcher (`platform/desktop/main.c`) loads external libraries by that name, and
  `aquarius-aot` looks for `<app>.aot.dylib`, the file `aqua build --optimize` writes.
- `runtime/platform/desktop/metal_layer.m` makes a `CAMetalLayer` for a GLFW window's `NSWindow`. Nothing builds it
  yet.

Still to do:

- `runtime/sdk/cmake/toolchains/macos-arm64.cmake` (`arm64-apple-macos`, the Xcode or Command Line Tools SDK through
  `xcrun --show-sdk-path`, `ld64.lld` or the system linker). A Mac host needs this file even to install the toolchain,
  because `install-toolchain.ps1` and `.sh` build `wasm2c` with the host's own toolchain file.
- `runtime/platform/macos/platform.cmake` and `platform.c`. The Linux layer is the model, with Linux-only calls
  replaced: `/proc/self/exe` becomes `_NSGetExecutablePath`, and `_GNU_SOURCE` extensions need checking. Link
  `Cocoa`, `IOKit`, `QuartzCore` and `Metal` for GLFW and wgpu. The stack option becomes
  `-Wl,-stack_size,0x4000000`.
- Pins: `sdks.wasmtime.macos-arm64` (Wasmtime's `aarch64-macos` C API archive), and `jdk.macos-arm64` so a Mac can
  build Android packs.
- Libraries that pick Linux or have no macOS branch: `application` (its CMake `else()` compiles
  `src/platform/linux.c`), `graphics` (no `wgpu_macos-arm64` pin, no `src/platform/macos.c`, Metal surface),
  `onnx` (no macOS branch, so no `src/platform/*.c` and no ONNX Runtime download), `interaction` (needs
  `src/platform/macos.c`), `camera` (`camera.h` has no macOS backend; AVFoundation, or the unsupported fallback until
  then), `input`, `audio` (miniaudio supports Core Audio; check the link libraries), `opengl` and `imgui` (check their
  GL loaders).
- Build target: `new ExecutableTarget("macos", "macos-arm64")`. Every Apple Silicon binary must be code signed, at
  least ad hoc. An executable with the application appended after it (`ExecutableBundle`) may fail `codesign`
  verification and notarization. A `.app` bundle with the `.wasm` as a resource is the likely shape. Check this on a
  Mac before relying on the overlay.
- CI: a `macos-14` (arm64) workflow.

## iOS and iPadOS (`ios-arm64`)

Already in place: the reserved row (built only on `macos-arm64` hosts: `hosts` in the table, enforced by
`AquariusInstallation.RequireBuildableHere`), `AQUA_OS_IOS`, `AQUA_MOBILE` and `"ios"` in the manifests' platforms.

Still to do, beyond what macOS needs:

- **No JIT.** iOS does not let App Store apps generate machine code at run time, so Wasmtime's Cranelift compiler
  cannot run there. The two portable options are the ahead-of-time path that already exists (`aqua build --optimize`:
  `wasm2c` and Clang through `runtime/aot`, with `platform/desktop/engine_aot.c`), linked statically into the app, or
  Wasmtime's Pulley interpreter if the pinned Wasmtime publishes an iOS C API with it. The runtime JIT
  (`platform/desktop/jit.c`) stays desktop-only, which rule 11 allows because the portable path is the fallback.
- **No loose shared libraries.** External libraries have to be linked into the app or shipped as signed frameworks
  inside the bundle, so `NativeFile`, `ExecutableTarget`-style packaging and the runtime's library search need an iOS
  form.
- **Toolchain:** `arm64-apple-ios<version>` against the iPhoneOS SDK. The simulator (`arm64-apple-ios<version>-simulator`,
  iPhoneSimulator SDK) is a different target; add it as its own row (for example `ios-simulator-arm64`).
- **Platform layer:** a `UIApplication` entry and a window from a `UIView` with a `CAMetalLayer`, in the role
  Android's `AquariusActivity` and `ANativeWindow` play now; touch, keyboard and safe areas through `<aqua/window.h>`.
- **Build target:** an `.app` (and `.ipa`) with `Info.plist`, entitlements and signing, as `AndroidTarget` builds an
  APK.
