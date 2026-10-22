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

The suite covers Traditional Chinese, English and mixed syntax, numeric semantics, lexical closures, bilingual alias
identity, arrays/hashes, error propagation, deep recursion, native callbacks,
Wasm artifact validation/round trips, source-free module and asset deployment,
CLI usage and standalone executable overlays. Wasmtime is the test execution
engine; cross-engine tests execute identical bytes in Node WebAssembly.

`BilingualSyntaxTest.cs` exercises every keyword, Unicode identifier boundaries,
source ranges, all declaration/function/return spelling combinations, mixed
conditional branches, loops, closures, recursion, values, errors and REPL state.
It checks both compiled execution and reloaded Wasm. Run it with
`dotnet test AquariusTests --filter FullyQualifiedName~BilingualSyntaxTest`.
`packaging/UnifiedCliSmokeTest.cs` also compiles modules written in different
syntaxes and executes the result after removing the sources.
`AquariusWebCompiler/tests/bilingual-syntax.test.mjs` verifies the same language
features on Node's WebAssembly engine. LSP integration and VS Code grammar/snippet
tests cover bilingual diagnostics, completion, definitions and highlighting:

```powershell
node --test AquariusWebCompiler/tests/bilingual-syntax.test.mjs
dotnet build AquariusLanguageServer -c Release
node --test AquariusLanguageServer/tests/lsp.test.js
npm run --prefix editors/vscode check
```

`threading/ThreadTest.cs` and `AquariusWebCompiler/tests/threads.test.mjs` cover
function parameters, captured snapshots, isolated mutation, graph results, worker
failures, timed waits, cancellation and joined disposal. Run the real browser
check with `node AquariusWebCompiler/tests/threads-browser.mjs .web-build/threading`
after exporting `examples/threading/parallel.aqua` there. Android bridge tests live
in `AquariusAndroid/tests/threads.test.mjs`; `scripts/test-android.ps1 -CaseNames
threading -BuildApks -Emulator` runs the shared behavioral verification on a device.
See the [bilingual threading API](../docs/threading.md).

`bottle/BottleTest.cs` covers manifest validation, semantic version ranges,
transitive dependencies, immutable snapshots, offline/locked restores and empty
cache hydration, conflicts/cycles, platform preservation, asset placement and
CLI create/install/build/run/uninstall. Run `dotnet test AquariusTests --filter
FullyQualifiedName~AquariusTests.Bottle`. Bottle's LSP behavior is covered in
`AquariusLanguageServer/tests/lsp.test.js`.
After exporting the Bottle showcase to a website, verify its transitive imports,
cached text and dynamic Wasm in real Chromium with `node
AquariusWebCompiler/tests/bottle-browser.mjs <web-output-directory>`.
Third-party Jolt numerical tests live with `Aqua_JoltPhysics`. Core tests verify
that Jolt is absent from the default catalog/assets, and exercise generic async
ES-module factories, per-load state, disposal, Terminal argument boundaries,
stdout/stderr, timeouts, platform assets and cached offline project builds.

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
