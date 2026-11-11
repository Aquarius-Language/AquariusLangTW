# Adding a target

A target is one runtime pack; `scripts/toolchain.json` `targets.list` names them.
A target can be reserved as *planned*: every tool knows its name and reports it as "planned but not built yet" instead
of "unknown", and nothing builds it. This page lists what adding one takes.

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
   `desktop/` where they fit. A desktop layer keeps its process entry in its own file (`platform_entry`, like
   `windows/entry.c`), so native library outputs link the rest of the layer.
5. **Libraries.** Each library with platform code needs a branch or a `src/platform/<platform>.c` for the new OS (rule
   3). Some `CMakeLists.txt` files still compare whole target names (`AQUA_TARGET STREQUAL "windows-x64"`); new
   branches use `AQUA_OS`. `aqua_add_library(PLATFORMS ...)` and `[library] platforms` in `aqua.toml` say where a library builds and runs.
6. **Build target.** Register a backend in `BuildTargets.Default` (for a desktop OS this is
   `new ExecutableTarget("<platform>", "<target>")`).
7. **Scripts and CI.** `build-runtime.ps1`, `test-libraries.ps1`, `install-toolchain.ps1` and `build-release.ps1`
   take the target from the table. Add a workflow under `.github/workflows/` that builds the pack and runs
   `dotnet test` and `test-libraries.ps1` on that host.
8. **Native library outputs.** Add the platform's `INativeLibraryPlatform` in `compiler/AquariusBuild/Libraries/Platforms/`
   and, once it builds them, its link rules in `runtime/platform/<platform>/embed.cmake` (`NativeLibraryRulesTest`
   checks both; see [native libraries](native-libraries.md)).
9. **Docs.** Add a `docs/<platform>.md` like [linux.md](linux.md), and update the target lists in
   [ARCHITECTURE.md](../ARCHITECTURE.md) and the libraries' READMEs.

## macOS and iOS

`macos-arm64` ([macos.md](macos.md)), `ios-arm64` and `ios-simulator-arm64` ([ios.md](ios.md)) were added this way and
are the latest worked examples: their rows, `runtime/sdk/cmake/toolchains/macos-arm64.cmake` and `ios*.cmake`,
`runtime/platform/macos` and `runtime/platform/ios`, the libraries' `AQUA_OS` branches and `src/platform/apple.c`,
`macos.c` and `ios.c` files, `ExecutableTarget("macos", ...)` (app bundles) and `IosTarget`, and
`.github/workflows/test-macos.yml`. Two answers they settled for later Apple targets:

- **Code signing.** Bytes appended to a Mach-O break its signature, so Apple exports are app bundles whose launcher stays
  unmodified and whose overlay is a file of its own (`ExecutableBundle.WritePayload`), signed ad hoc inside out.
- **No JIT.** iOS runs every application through the ahead-of-time engine (`host/engine_aot.c`, wasm2c and the pinned
  Clang) linked statically with the pack's `libaquarius.a`; Wasmtime publishes no iOS C API.
