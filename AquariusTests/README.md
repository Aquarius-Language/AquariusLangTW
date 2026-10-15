# Compiler and runtime verification

For Ubuntu/WSL2, `bash scripts/test-linux.sh` builds the native bridges and runtime
pack before running the full desktop/GPU/packaging and browser suite. See
[the Linux guide](../docs/linux.md) for prerequisites and sibling-project checks.

Prepare the pinned core/browser assets, build with the .NET 8 SDK, then run:

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
dotnet build AquariusLang.sln -m:1
dotnet test AquariusTests -m:1
npm test --prefix AquariusWebCompiler
```

The suite covers syntax, numeric semantics, lexical closures, bilingual alias
identity, arrays/hashes, error propagation, deep recursion, native callbacks,
Wasm artifact validation/round trips, source-free module and asset deployment,
CLI usage and standalone executable overlays. Wasmtime is the test execution
engine; cross-engine tests execute identical bytes in Node WebAssembly.

`bottle/BottleTest.cs` covers manifest validation, semantic version ranges,
transitive dependencies, immutable snapshots, offline/locked restores and empty
cache hydration, conflicts/cycles, platform preservation, asset placement and
CLI create/install/build/run/uninstall. Run `dotnet test AquariusTests --filter
FullyQualifiedName~AquariusTests.Bottle`. Bottle's LSP behavior is covered in
`AquariusLanguageServer/tests/lsp.test.js`.
After exporting the Bottle showcase to a website, verify its transitive imports,
cached text and dynamic Wasm in real Chromium with `node
AquariusWebCompiler/tests/bottle-browser.mjs <web-output-directory>`.
Jolt tests use the actual shared Emscripten Wasm build, including motion, forces,
collisions, ownership, capacity and engine/world disposal.

Compiler IR diagnostics remain tested where they belong to front-end correctness.
Old RIUS serialization, bottle ZIP layout and interpreted-function AST expectations
are replaced by native Wasm and deployed behavior assertions.

GPU/window tests are opt-in:

```powershell
$env:AQUARIUS_OPENGL_TESTS='1'
$env:AQUARIUS_WGPU_TESTS='1'
$env:AQUARIUS_APPLICATION_NATIVE_TESTS='1'
dotnet test AquariusTests -m:1
./scripts/test-examples.ps1 -SkipBuild
node AquariusWebCompiler/tests/browser-smoke.mjs
```

A standalone EXE test requires the current runtime pack, built with
`native/build.ps1` then `scripts/publish-apphost.ps1`. Rebuild tests to copy the pack.
Python is needed for the existing external-process example. Node is required for
browser parity. Set `AQUARIUS_COMPILER_DLL` for Node tests using a Release compiler.
`AQUARIUS_WASM_BENCHMARKS=1` enables optional performance measurements; correctness
never depends on timings.

To verify the real MarbleRun, Painter and Snake sibling projects against current
source (override paths with `-Marble`, `-Painter`, `-Snake`):

```powershell
./scripts/test-projects.ps1 -WindowsExecutables
node AquariusWebCompiler/tests/project-smoke.mjs
```

This compiles every project, runs source-free Wasm on native GPU/Wasmtime, checks
MarbleRun gameplay and Snake's 236 rules, exports websites and optionally runs
standalone Windows EXEs. Browser checks use real WebGPU, inspect rendered pixels,
exercise MarbleRun input/resize and verify shutdown. JSON reports and screenshots
are written to `.web-build/projects`. Painter's full 24-check suite and Snake's
16-check browser suite remain in their example repositories and use the official
`host.runtime` API.
