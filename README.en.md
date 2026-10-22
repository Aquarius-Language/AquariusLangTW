# AquariusLang（星泉）

<p><a href="README.md">繁體中文</a> | <strong>English</strong></p>

World's first **bilingual Traditional Chinese + English compiled programming language**. Write entirely in
Traditional Chinese, entirely in English, or mix both in the same `.aqua` file.
No language mode or configuration is needed.

Sources compile to standard **WebAssembly (`.wasm`)**. Desktop and Android use
**Wasmtime**; browsers use their native WebAssembly engine. The same compiled
application can be packaged as a website, standalone executable or native APK.
Android uses a native Java capability host.

Author: 林天牧 / Temple Lin.

- **Bilingual syntax:** `var` / `變數`, `function` / `函式`, `return` / `回傳` and
  all other keywords share the same semantics. Modules in either syntax interoperate.
- **One CLI:** `aqua` builds programs, runs Wasm, exports platform applications
  and provides an interactive REPL.
- **Portable applications:** compiled modules, images, shaders and other assets
  travel together; deployed applications do not need source files.
- **Bilingual libraries:** Chinese and English function names share functions
  and captured closures. See the [library naming reference](AquariusDesktop/LIBRARY_NAMES.md).
- **Projects and packages:** [Bottle](AquariusBottle/README.md) provides TOML
  manifests, local/GitHub dependencies, version locks, global caching and asset
  packaging. See the [showcase](examples/bottle_showcase/README.md).
- **Threads:** [Thread / 執行緒](docs/threading.md) provides isolated workers,
  timed joins, results, cancellation and safe closing on desktop, web and Android.
- **Networking:** [Network / 網路](docs/networking.md) provides HTTP(S), cookies,
  cancellable requests, WebSockets and native TCP/TLS/UDP, with desktop HTTP(S)
  routing, static frontends, SSE and sessions.
- **Graphics:** portable WebGPU, Processing drawing and desktop OpenGL. Jolt
  physics is supplied by the independent `Aqua_JoltPhysics` Bottle library.
- **Models and PBR:** [OBJ/MTL parsing and shared WGSL rendering](docs/pbr-models.md),
  plus [named shader textures, cube mip chains and floating render targets](docs/wgpu-textures.md).
- **Audio:** [shared miniaudio Wasm decoding and mixing](AquariusCore/audio/README.md),
  including files, tones, loops, panning, pitch and offline sample output.
- **Inference:** [ONNX Runtime](docs/onnx.md) contracts and platform execution
  providers, including optional CUDA/TensorRT through `[onnx] cuda = true` in
  `aqua.toml`. See the [pose example](examples/mediapipe/README.md).
- **GPU math:** [GPUMath / GPU數學](docs/gpu-math.md) accepts WGSL expressions,
  compute functions and full shaders, with retained buffers and pipeline caching.
- **Dynamic linking:** [DynamicLibrary / 動態函式庫](docs/dynamic-linking.md)
  loads native libraries, JavaScript and portable Wasm.

## Contents

- [Quick start](#quick-start)
- [Build and package a release](#build-and-package-a-release)
- [Compile, run and export](#compile-run-and-export)
- [Syntax](#syntax)
- [Libraries and examples](#libraries-and-examples)
- [Development and tests](#development-and-tests)
- [Project structure](#project-structure)
- [Editors and further documentation](#editors-and-further-documentation)
- [Name and license](#name-and-license)

## Quick start

Download a package from [Releases](https://github.com/Aquarius-Language/AquariusLangTW/releases)
or build one with the script below. Extract the whole archive, then open PowerShell
in its `aqua` directory:

```powershell
.\aqua.exe --help
.\aqua.exe build .\examples\increment.aqua -o app.wasm
.\aqua.exe run .\app.wasm
.\aqua.exe repl
```

At the `>>` prompt, enter `print("Hello, Aquarius!");` or `印出("你好，星泉！");`.
Press Ctrl+C to exit. Quote paths containing spaces.

Release packages include the .NET runtime. End users do not need the .NET SDK
or a separate runtime installation. Keep the DLLs, configuration, `runtimes`,
`licenses` and `examples` alongside the compiler executable.

## Build and package a release

The root script produces a **Windows x64, self-contained Release** compiler package.
Prepare these tools on Windows x64:

| Tool | Purpose |
| --- | --- |
| Windows PowerShell 5.1 or PowerShell 7 | The script prefers PowerShell 7 and falls back to Windows PowerShell. |
| .NET 8 SDK | Builds, tests and publishes the `net8.0` projects. |
| Node.js and npm | Restores pinned browser dependencies, prepares embedded assets and runs tests. |
| Python (`python`) | Runs existing external-process integration checks. |
| CMake 3.20+, Visual Studio C++ build tools and Windows SDK | Builds native graphics and text-input bridges. |

Initial NuGet, npm and GLFW restoration needs network access. The script searches
PATH and common installations, including the user's `.dotnet` directory and pyenv.
For Ninja and similar generators, use a terminal with the x64 C++ environment configured.

```powershell
.\build-release.bat
.\build-release.bat -Help

# Override tool detection when necessary
.\build-release.bat -Dotnet "C:\Tools\dotnet\dotnet.exe" -CMake "C:\Tools\CMake\bin\cmake.exe"
.\build-release.bat -Python "C:\Tools\Python\python.exe"
.\build-release.bat -Generator "Visual Studio 17 2022"

# Also exercise the published executable's GPU and graphics features
.\build-release.bat -VerifyGraphics
```

The script locates the repository regardless of the launch directory. It checks
tools, restores browser assets, builds native bridges, builds the Release solution,
runs .NET and browser-host unit tests, publishes `aqua.exe`, verifies the actual
published executable, and produces a ZIP and SHA-256 checksum. Published-executable
checks include source-free execution, website export and third-party library loading.
GPU/window checks require suitable hardware, drivers and a desktop session.

Each successful run creates a new directory:

```text
dist/releases/AquariusCompiler-win-x64-<build-id>/
├─ aqua/                         # Complete runnable compiler package
├─ AquariusCompiler-win-x64-<build-id>.zip
├─ AquariusCompiler-win-x64-<build-id>.zip.sha256
├─ reports/                      # .NET TRX and published CLI verification
└─ build.log
```

Distribute the ZIP and checksum; recipients extract the entire package and run
`aqua\aqua.exe`. Verify with `Get-FileHash "package.zip" -Algorithm SHA256`.
The compiler is published as a directory with its apphost and runtime files;
keep those files together. Graphics still needs compatible drivers and native dependencies.

Failures return a nonzero exit code. After tool checks, logs and partial output
remain in `dist/.package-<build-id>/`; successful releases are kept separately.
LSP and VSIX packaging follow the editor documentation.

For Linux x86_64 / WSL2 build, test and executable packaging instructions, see
the [Linux desktop guide](docs/linux.md).

## Compile, run and export

With `aqua` on PATH, run these commands in your application project:

```powershell
# Compile the entry point, imported modules and assets into one standard Wasm
aqua build main.aqua lib\tools.aqua --root . --assets assets --entry main.aqua -o app.wasm
aqua run app.wasm

# Export directly from source
aqua build main.aqua lib\tools.aqua --root . --assets assets --target web -o dist\web
aqua build main.aqua lib\tools.aqua --root . --assets assets --target windows -o dist\app.exe

# Export an existing compiled application
aqua build app.wasm --target web -o dist\web
aqua build app.wasm --target windows -o dist\app.exe

# Serve the generated website at http://localhost:8080
python -m http.server 8080 --directory dist\web
```

| Option | Meaning |
| --- | --- |
| `--root` | Source root; preserves relative module and asset paths. |
| `--assets` | Include a file or directory; may be repeated. Dynamic Wasm libraries can be assets. |
| `--entry` | Entry `.aqua` module, defaulting to the first source input. |
| `-o` | Output `.wasm` / `.exe` path or website directory. |
| `--target wasm` | Default: standard Wasm containing the program, modules and assets. |
| `--target web` | Writes `program.wasm`, browser host and assets; requires `-o`. |
| `--target android` | Exports a native Java / Gradle project or APK; see [Android](docs/android.md). |
| `--target windows` | Produces a self-contained Windows x64 executable; requires `-o`. |

List every imported script, including dynamic imports. Assets must be under the
root. Module names retain their relative `.aqua` paths. Compilation and export
do not execute the program, and failed builds preserve existing output.
Old `.bottle` / `.rius` artifacts must be rebuilt from source.

The `aquarius.application` Wasm custom section contains versioned ABI metadata,
constants, module entries and assets. Deployed code executes compiled Wasm functions.

Application EXEs include Wasmtime, .NET, host libraries and application resources;
their runtime unpacks into the user's cache on first launch. Building them from
source requires `native/build.ps1` and `scripts/publish-apphost.ps1`, followed by
a CLI rebuild. Release compiler packages include `build-targets/win-x64`.
External programs such as Python remain separate requirements when an application uses them.

Serve websites over localhost or HTTPS. Graphics requires a WebGPU adapter.
Platform-specific host services report unsupported capabilities when unavailable.
See the [CLI](AquariusCli/README.md), [application format](AquariusPackaging/README.md),
[build targets](AquariusBuild/README.md) and [web compiler](AquariusWebCompiler/README.md).
CLI exit codes are **0** for success, **1** for compilation/runtime/file failures,
and **2** for invalid commands or options.

## Syntax

Every keyword can independently use either spelling:

| Traditional Chinese | English | Meaning |
| --- | --- | --- |
| `變數` | `var` | Mutable variable declaration |
| `函式` | `function` | Function expression and closure |
| `回傳` | `return` | Return a result |
| `如果` | `if` | Conditional branch |
| `否則如果` | `else if` or `elif` | Additional conditional branch |
| `否則` | `else` | Fallback branch |
| `迴圈` | `for` | Loop |
| `中斷` | `break` | Exit a loop |
| `真` / `假` | `true` / `false` | Boolean literals |

Traditional Chinese:

```aquarius
變數 加法 = 函式(甲, 乙) { 回傳 甲 + 乙; };
印出(加法(3, 5));
```

English:

```aquarius
var add = function(a, b) { return a + b; };
print(add(3, 5));
```

Mixed syntax, including two names for one shared function:

```aquarius
var 加法, add = function(甲, b) { 回傳 甲 + b; };
變數 計數 = 0;

for (變數 索引 = 0; 索引 < 5; 索引++) {
    計數++;
    if (計數 == 3) { 中斷; }
}
印出(計數, 加法(3, 5), add(3, 5));
```

Branches can also mix: `if (假) { ... } else 如果 (true) { ... } 否則 { ... }`.
Keywords are case-sensitive reserved words in both languages. Longer names such
as `returnValue` and `如果成立` remain identifiers. Existing code that used an
English keyword as a name must rename it. `let` and `fn` remain ordinary identifiers;
English declarations use `var name = function(...) { ... };`.
`var` preserves Aquarius lexical scope and mutable-variable semantics.
Strings, comments and user-defined names keep their original text. Default
value inspection continues to display booleans as `真` / `假`.

Identifiers accept Unicode letters and underscores, followed by letters, digits
or combining marks. The language supports local/global variables, arrays, hashes,
closures, recursion and modules. Numeric values include integers, floats (`f`)
and doubles (`d`). Operators include `+ - * / %` and `+= -= *= /= %=`.
Prefix `++value` returns the updated number; postfix `value++` returns the old
number. Both preserve the variable's numeric type.

Builtins such as `print` / `印出`, `len` / `長度` and `import` / `匯入` have
bilingual names. The portable [Array](docs/arrays.md), [Hash and String](docs/hash-and-string.md)
modules run in shared Wasm. Module names, properties, constants and event strings
follow their library's API.

`currentTime()` / `目前時間()` returns monotonic double milliseconds for elapsed
time measurements; subtract two readings and divide by `1000.0d` for seconds.
`currentDate()` / `目前日期()` returns the host's local date as `yyyy-MM-dd`.
`typeOf(value)` / `型別(值)` returns stable tags such as `integer`, `double`,
`string`, `array`, `hash` and `null`.
See the [language guide](AquariusCore/LANGUAGE.md) for detailed semantics.

## Libraries and examples

Import a library with `var drawing = import("Processing");` or
`變數 繪圖 = 匯入("Processing");`. Examples live in `examples/` and
`AquariusDesktop/examples/`; desktop examples are included in release packages.

| Feature | Documentation | Examples |
| --- | --- | --- |
| WebGPU | [WGSL rendering, compute, buffers and pixel readback](AquariusDesktop/graphics/WGPU.md) | [Compute](examples/wgpu_compute/README.md), [triangle](examples/wgpu_triangle/README.md) |
| Processing | [2D/3D drawing, text, images, events and shaders](AquariusDesktop/graphics/Processing.md) | [Showcase](examples/processing_showcase/README.md), [color mapping](examples/color_mapping/README.md) |
| OpenGL | [GLFW, GLAD, OpenGL 3.3 core, GLM and STBImage](native/README.md) | [Textured cube](examples/opengl_cube/README.md) |
| Physics | Independent `Aqua_JoltPhysics` TOML package using DynamicLibrary | [Jolt](examples/jolt_physics/README.md) |
| Text input | [Unicode editing, events and Windows IME](AquariusDesktop/graphics/TextInput.md) | [Multilingual input](examples/multilingual_input/README.md) |
| Bilingual language | Chinese, English and mixed syntax, closures and recursion | [Starship](examples/starship_expedition/README.md), [bilingual functions](examples/bilingual_library/README.md), [arrays in both syntaxes](examples/arrays/README.md) |
| Camera | [Core contracts and platform implementations](docs/webcam.md) | [Webcam](examples/webcam/README.md) |

Desktop windows require native bridges. Install Jolt through Bottle. See each
library guide for browser support and platform differences.

## Development and tests

From the repository root:

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser

# Needed for desktop native window features
.\native\build.ps1

dotnet build AquariusLang.sln -c Release -m:1
dotnet test AquariusTests -c Release --no-build -m:1
npm test --prefix AquariusWebCompiler

# Run the CLI from source
dotnet run --project AquariusCli -- build examples/increment.aqua -o .web-build/increment.wasm
dotnet run --project AquariusCli -- run .web-build/increment.wasm
dotnet run --project AquariusCli -- repl
```

The [test guide](AquariusTests/README.md) describes syntax, Wasm, cross-engine,
CLI and packaging checks, plus opt-in GPU tests. Bilingual tests cover every
keyword, mixed declarations and branches, loops, closures, recursion, modules,
errors, source ranges and source-free Wasm execution.

For full examples and real-browser regression checks:

```powershell
.\AquariusWebCompiler\scripts\build-web.ps1
.\scripts\test-examples.ps1 -SkipBuild
node AquariusWebCompiler/tests/browser-smoke.mjs
```

These need the relevant desktop/GPU and Python dependencies. Browser tests use
installed Chrome by default; set `$env:AQUARIUS_BROWSER = 'msedge'` for Edge.
See the [Linux guide](docs/linux.md) and [native guide](native/README.md) for other
platforms. The root release batch script packages Windows x64.

Compiled Wasm functions use resumable call frames, so Aquarius recursion does not
accumulate C# call-stack frames. Closures retain their defining environment, and
the REPL preserves globals across submissions. See [architecture](ARCHITECTURE.md).

## Project structure

| Directory / project | Responsibility |
| --- | --- |
| `AquariusCli` | Unified `aqua` build/run/export/REPL entry point. |
| `AquariusCore` | Bilingual frontend, Wasm compiler, ABI and portable library contracts. |
| `AquariusPackaging` | Wasm application modules, assets, validation and paths. |
| `AquariusDesktop` | Desktop builtins, native backends and compatibility entry point. |
| `AquariusWebCompiler` | Website export and browser WebAssembly host. |
| `AquariusAndroid` | Native Android deployment, Java host and JNI WebGPU. |
| `AquariusBottle` | TOML project and dependency management. |
| `AquariusBuild` | Platform build targets. |
| `AquariusTests` | Language, Wasm, cross-engine, CLI and desktop tests. |
| `AquariusLanguageServer`, `editors/vscode` | LSP and VS Code extension. |
| `native`, `scripts` | Native bridges, publishing and verification scripts. |
| `examples` | Language, graphics, physics and interactive examples. |
| `website` | Introduction website and deployment documentation. |

## Editors and further documentation

The [VS Code extension](https://marketplace.visualstudio.com/items?itemName=aquariuslang.aquariuslang-tw)
provides bilingual highlighting, snippets, live diagnostics, completion, hover,
definitions and symbols. See the [VS Code guide](editors/vscode/README.md) for
installation, configuration and VSIX packaging. Other editors can use the
[language server](AquariusLanguageServer/README.md), which analyzes source without executing it.

The [AquariusLang introduction website](https://aquarius-language.github.io/AquariusLangTW/)
has [source and local instructions](website/README.md) and a
[GitHub Pages deployment guide](website/docs/github-pages.md).
Some detailed subsystem guides are written in Traditional Chinese.

## Name and license

**星泉** combines the starry sky of Aquarius with flowing spring water:
inspiration shines like stars, and creativity flows like a spring.

Licensed under the [MIT License](LICENSE), Copyright (c) 2026 Temple Lin.
Third-party dependencies retain their own licenses in `native/vendor/` and
`AquariusDesktop/licenses/`; release packages include the corresponding notices.
