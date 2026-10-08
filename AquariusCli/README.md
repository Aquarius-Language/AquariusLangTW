# Unified Aquarius compiler

`aqua` compiles Aquarius sources into portable bottles, executes bottles on the
desktop VM, and exports the same bottles as static browser applications.

```cmd
aqua build main.aqua lib\tools.aqua --root . --assets assets -o app.bottle
aqua run app.bottle
aqua build app.bottle --target web -o dist\web
aqua repl
```

During development, replace `aqua` with
`dotnet run --project AquariusCli --`. `build` defaults to `--target bottle`.
List every script module, including dynamically imported ones. The first input
is the default entry; `--entry lib/other.aqua` overrides it during compilation.
Execution and web export accept `--entry lib/other.rius` (or its `.aqua` alias).
Use `--assets path` repeatedly to include files or directories. Asset paths
remain relative to `--root`; source files and existing bottles are excluded.
Imports resolve relative to their defining packaged module, support internal
`..` traversal, and reject paths escaping the package. Package lookup preserves
canonical names and uses case-insensitive matching. Quote paths containing spaces;
`--` ends option parsing for filenames starting with a dash.

Web builds require an existing `.bottle` as their sole input. They load validated
bytecode and bundled resources without reading `.aqua` files, executing the
program, or scanning surrounding folders. Generated applications contain local
JavaScript runtime files, WebGPU and Jolt WASM adapters, dependencies and licenses.
Serve the output over localhost or HTTPS:

```cmd
python -m http.server 8080 --directory dist\web
```

WebGPU needs a supported browser and GPU adapter. Desktop processes and native
APIs outside the implemented browser library surface remain unsupported and
report runtime errors when called. Dynamic calls cannot generally be resolved
at export time. Browser execution yields periodically and supports Stop.
Each script import creates fresh module state on both hosts; native library
modules are shared within a run.

Desktop runs materialize packaged resources in a private temporary directory for
native image/shader readers. The directory is removed on completion or failure.
`currWorkingDir` / `目前工作目錄` still refers to the relocated bottle's virtual
module directory. Resource reads resolve into the private directory; output
writes retain their requested filesystem paths. Packaging is independent of
native graphics and physics libraries.

Exit codes: **0** success, **1** compilation, runtime, malformed package or IO
failure, **2** invalid commands/options. Failed builds preserve previous output.
Web output replacement is restricted to dedicated generated directories; a
directory containing unrelated files or links is rejected.

## Distribution

```powershell
./scripts/publish-cli.ps1
./dist/aqua/aqua.exe --help
```

The publish script prepares pinned browser dependencies and publishes a
self-contained Windows x64 folder. Keep the entire folder, including native
libraries, licenses and `runtimes`. Build the GLFW bridge with
`./native/build.ps1` before publishing graphics applications. Other platforms
can pass `-Runtime` when their native dependencies are available.

## Verification

```powershell
./AquariusWebCompiler/scripts/build-web.ps1
dotnet test AquariusLang.sln -m:1
npm test --prefix AquariusWebCompiler
./scripts/test-examples.ps1 -SkipBuild
node AquariusWebCompiler/tests/browser-smoke.mjs
./scripts/test-published-cli.ps1
```

`test-examples.ps1` runs every script in both example trees, all marble fixture
modules, and the portable fixture as source and as compiled bottles. It removes
disposable source/resource copies before bottle execution and web export. Native
graphics examples run for two frames (one for the dense color-mapping UI) and
verify captures. The marble smoke runs its complete eleven-stage sequence. The deliberately
invalid `generate_errors` example must fail with the expected language error.
Results are written to `.web-build/desktop-example-results.json`.
Use `-RetryFailed -SkipBuild` to retry failed executions while retaining previous
successful results, useful after resolving an environmental timeout.

Browser smoke tests cover legacy source websites, bottle websites, portable
imports/assets, real GPU compute/readback and rendered colors, Jolt gameplay,
text input and Stop. `AQUARIUS_WEB_PROJECTS` optionally selects comma-separated
website directories. Results are in `.web-build/browser-results.json`.

The published smoke runner executes the self-contained apphost with .NET runtime
search paths pointing at a nonexistent directory. It removes its disposable
sources before execution/export and checks packaged imports, assets, WebGPU,
native image output, Jolt and OpenGL. Its report is
`.web-build/published-cli-results.json`. The external-process example and packaged
Python tests require a working `python` executable on PATH.
