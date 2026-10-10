# WebAssembly website exporter

The exporter accepts Aquarius source or a compiled `.wasm` application and emits a
self-contained static website. `program.wasm` is instantiated by native browser
WebAssembly. `program.json` contains entry/module mapping, application assets,
shared library catalog, WGSL ABI and external-library version/hash declarations.
There is no JavaScript Aquarius bytecode interpreter.

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
dotnet build AquariusLang.sln -m:1
dotnet AquariusWebCompiler/bin/Debug/net8.0/AquariusWebCompiler.dll examples web increment.aqua
# Or use the unified driver directly:
dotnet AquariusCli/bin/Debug/net8.0/aqua.dll build examples/increment.aqua --target web -o web
python -m http.server 8080 --directory web
```

Browser host services supply dynamic values and capabilities through `aquarius_v1`.
Compiled continuations await GPU readback, animation, codecs and module imports.
Checkpoints keep pure loops cancellable. WebGPU shaders and resource ownership
preserve core semantics. Jolt glue, binary and physics implementation come from
core; all dependencies and licenses are local to the export. Desktop-only APIs
fail explicitly. Use localhost or HTTPS and a browser with a WebGPU adapter.

The exporter stages output and replaces only compiler-owned directories, preserving
prior artifacts on failure. Sources need not exist when exporting compiled Wasm.

```powershell
npm test --prefix AquariusWebCompiler
./AquariusWebCompiler/scripts/build-web.ps1
node AquariusWebCompiler/tests/browser-smoke.mjs
node AquariusWebCompiler/tests/browser-resize.mjs
node AquariusWebCompiler/tests/application-browser.mjs
```

Node Wasm tests use the built `AquariusCli/bin/Debug/net8.0/aqua.dll`; set
`AQUARIUS_COMPILER_DLL` for another build. The .NET suite compares identical Wasm
binaries across Wasmtime and Node's browser-equivalent WebAssembly engine.
Real browser tests cover examples, WebGPU, Jolt, resizing and interactive behavior.
