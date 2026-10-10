# Unified Aquarius compiler

`aqua` compiles Aquarius sources into portable bottles, executes bottles on the
desktop VM, and exports the same bottles as static browser applications or standalone Windows executables.

```cmd
aqua build main.aqua lib\tools.aqua --root . --assets assets -o app.bottle
aqua run app.bottle
aqua build app.bottle --target web -o dist\web
aqua build app.bottle --target windows -o dist\app.exe
aqua repl
```

During development, replace `aqua` with
`dotnet run --project AquariusCli --`. `build` defaults to `--target bottle`.
List every script module, including dynamically imported ones. The first input
is the default entry; `--entry lib/other.aqua` overrides it during compilation.
Execution and platform exports accept `--entry lib/other.rius` (or its `.aqua` alias).
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

Windows builds require one `.bottle` and an explicit `.exe` output filename.
`--target windows` currently selects Windows 10/11 x64. The single executable contains
the desktop VM, .NET runtime, native graphics/physics/image libraries, licenses,
and the bottle's compiled modules and assets. Recipients need neither Aquarius
nor .NET installed. Copy the `.exe` anywhere and run it directly. `--entry`
selects a packaged entry without modifying the input bottle. Program failures
return exit code 1; launch arguments are passed to `Application.LaunchFiles()`.
Console input/output and desktop window APIs work as in `aqua run`.

The bundled runtime extracts its dependencies to the user's .NET bundle cache
on first launch; it needs a writable temporary/cache directory. Packaged assets
use the existing private resource directory and cleanup. Virtual module paths
refer to the executable's directory even when launched from another working
directory. Operating-system services and GPU drivers are still provided by
Windows. Explicit calls to external programs (such as Python through
`execFile`) still require those programs; Aquarius does not embed arbitrary
external interpreters.

The compiler consumes a prebuilt runtime pack under
`build-targets/win-x64/`, so exporting requires no SDK or network access.
Both compiler release scripts include this pack automatically. For development:

```powershell
./native/build.ps1
./scripts/publish-apphost.ps1 -Dotnet dotnet
dotnet build AquariusLang.sln -m:1
dotnet run --project AquariusCli -- build app.bottle --target windows -o app.exe
```

Rebuild the runtime pack after changing desktop/core runtime code. Its
publisher also requires Visual Studio C++ build tools and bundles their release
CRT dependencies for wgpu-native. No redistributable installer is required by
the generated application. The pack's manifest checks the executable, bottle
and bytecode format versions and the
template's SHA-256 before export. `AQUARIUS_BUILD_TARGETS` can select another
runtime-pack root for development. See [deployment architecture](../AquariusBuild/README.md)
for adding platforms and the executable container contract.

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
libraries, licenses, `runtimes` and `build-targets`. Build the GLFW bridge with
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

Prepare the application runtime before building tests to enable
`WindowsExecutableTest`; otherwise these integration tests report a skip.
They relocate only the `.exe`, remove sources and bottles, disable external
.NET lookup paths, and verify entry selection, imports, Unicode assets,
native libraries, launch files, language errors and damaged payload diagnostics.

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
`.web-build/published-cli-results.json`. The external-process example requires a
working `python` executable on PATH. The packaged Python test also accepts
`AQUARIUS_PYTHON` set to a Python 3 executable path and detects Windows pyenv and
per-user Python installations.
