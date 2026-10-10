# AquariusDesktop

Desktop host for compiled Aquarius applications, using Wasmtime for all Aquarius
execution. It implements core WebGPU, application and external-library contracts.
Jolt uses the same core Emscripten Wasm and world implementation as browsers,
with ClearScript/V8 supplying its glue environment.

The preferred compiler driver is `aqua`. The desktop entry also supports:

```powershell
dotnet AquariusDesktop.dll main.aqua
dotnet AquariusDesktop.dll app.wasm
dotnet AquariusDesktop.dll -c main.aqua lib/tools.aqua -o app.wasm
dotnet AquariusDesktop.dll -cr main.aqua
dotnet AquariusDesktop.dll --entry lib/tools.aqua app.wasm
dotnet AquariusDesktop.dll --disassemble main.aqua
```

Source entry points compile to Wasm before execution. `--disassemble` prints
compiler IR for diagnostics. With no arguments, each REPL input is compiled and
run within a persistent environment. Packaged imports execute compiled modules,
while direct source imports compile the requested source before invoking it.

Native graphics assets are built by `native/build.ps1`. Published distributions
include the Wasmtime and V8 engines, graphics/codec dependencies and licenses.
See [the root guide](../README.md), [architecture](../ARCHITECTURE.md),
[WebGPU](graphics/WGPU.md) and [external libraries](../docs/external-libraries.md).
