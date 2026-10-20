# Platform build backends

`AquariusBuild` owns deployment targets. `AquariusPackaging` owns portable
WebAssembly applications, and `AquariusAppHost` executes a contained desktop program.
The CLI compiles sources to Wasm or dispatches a `WasmBuildRequest` through
`BuildTargets`. Web, Windows and Linux share the same dispatch path.

To add a platform, implement `IWasmBuildTarget` and register it in
the CLI's `CompilerBuildTargets.Default` composition root. Its backend owns output layout and platform validation;
the CLI continues to validate one wasm, output and optional entry. A platform
with a compatible desktop runtime can reuse `SelfContainedExecutableTarget`
with its own runtime identifier and executable extension. Browser/mobile or
other deployment models can implement their own backend. Adding a runtime
identifier alone does not establish native-library support.

The current Windows backend produces one Windows x64 console-subsystem `.exe`
with full desktop libraries. It validates the wasm and selected entry, checks
an installed runtime pack, writes a sibling staging file, and replaces only the
specified output file after completion. It does not compile source, execute
application code, invoke an SDK, or download dependencies.

## Runtime packs

Linux x86_64 uses `--target linux -o app`, `scripts/publish-apphost.sh` and
`build-targets/linux-x64/{host,runtime.json}`. ELF exports receive Unix execute
permissions before atomic replacement. See [Linux builds and tests](../docs/linux.md).

`scripts/publish-apphost.ps1` publishes the host with .NET self-contained
single-file deployment, all content/native libraries included, and trimming
disabled. Examples and debug symbols are omitted; licenses are included.
Publishing requires the SDK, a built native graphics bridge, and Visual Studio
C++ release redistributable DLLs. The bridge/GLFW use the static CRT; wgpu-native's
VC runtime dependencies are bundled from the VS redist directory, avoiding an
end-user redistributable installer. `-VCRuntimeDirectory` overrides automatic
VS discovery. The script
rejects publish output with external files, queries format versions from the
published host, records the template checksum, then commits the pack directory.
Compiler distributions copy these packs alongside `aqua`.

Each pack contains `host.exe` and `runtime.json`. Windows and Linux x64 also have an optional
NVIDIA CUDA flavour, `build-targets/<rid>-cuda`, published with `-OnnxCuda` / `--onnx-cuda`
(ONNX Runtime's CUDA/TensorRT providers plus pinned NVIDIA CUDA/cuBLAS/NVRTC/cuDNN
inference libraries and license notices). Exports select it only when the
project sets `[onnx] cuda = true` or the CLI passes `--onnx-cuda true`; `SelfContainedExecutableTarget`
checks that the pack's `onnxCuda` matches the request.

```json
{
  "format": "aquarius-runtime-pack",
  "version": 1,
  "runtimeIdentifier": "win-x64",
  "bundleVersion": 2,
  "wasmAbiVersion": 1,
  "onnxCuda": false,
  "templateSha256": "<SHA-256 of host.exe>"
}
```

Packs must be rebuilt with runtime changes; version checks detect incompatible
formats, while checksums detect corrupted templates. Checksums are integrity
checks, not publisher authentication. Only the raw, unbundled host accepts
`--runtime-info`; packaged apps treat all arguments as launch files.

## Executable overlay version 2

`ExecutableBundle` in the host-independent packaging library writes:

```text
[unmodified host template][original wasm Wasm][UTF-8 entry JSON][64-byte footer]
```

The JSON is `{"EntryPoint":"canonical/module.aqua"}`. Footer integers are
little-endian:

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 4 | Overlay version, currently 1 |
| 4 | 4 | Entry JSON length |
| 8 | 8 | Original wasm Wasm length |
| 16 | 32 | SHA-256 of wasm bytes followed by entry JSON |
| 48 | 16 | ASCII `AQUARIUS-APP-V2!` |

The reader derives the payload offset from the file length, bounds all sizes,
verifies the checksum, and loads a seekable view whose Wasm payload offsets start at
zero. It then applies normal Wasm metadata and engine validation and resolves the entry.
No embedded program is executed during validation. Metadata is limited to
64 KiB; compressed wasms to 272 MiB, allowing Wasm overhead beyond the existing
256 MiB uncompressed package limit. The overlay version is independent of the
Wasm ABI and .NET single-file formats.

`AquariusAppHost` reads its own process executable and runs the validated package
through `ScriptRunner.RunPackage`. Its virtual module root is the executable's
location, and runtime resources retain existing temporary extraction and cleanup.
The .NET runtime manages its own dependency extraction cache. Windows executables
can be renamed or moved without modifying the embedded entry or imports.

Tests cover overlays and failures without a native pack, and run real relocated
executables when a pack is installed. `test-windows.yml` prepares the pack and
executes these checks in CI.
