# AquariusLang（星泉）

<p><a href="README.md">繁體中文</a> | <strong>English</strong></p>

World's first **bilingual Traditional Chinese + English compiled programming language**. Write entirely in
Traditional Chinese, entirely in English, or mix both in the same `.aqua` file.
No language mode or configuration is needed.

Sources compile to standard **WebAssembly (`.wasm`)**, run by one native runtime written in C: **Wasmtime** on
Windows, Linux and Android, the browser's own WebAssembly engine on the web. The same compiled application exports as
a website, a standalone executable or an APK. C# is only the compiler; nothing at run time needs .NET.

Author: 林天牧 / Temple Lin.

- **Bilingual syntax:** `var` / `變數`, `function` / `函式`, `return` / `回傳` and
  all other keywords share the same semantics. Modules in either syntax interoperate.
- **One CLI:** `aqua` builds programs, runs Wasm, exports platform applications
  and provides an interactive REPL.
- **Portable applications:** compiled modules, images, shaders and other assets
  travel together; deployed applications do not need source files.
- **Projects and packages:** [Bottle](compiler/AquariusBottle/README.md) provides TOML
  manifests, local/GitHub dependencies, version locks, global caching and asset
  packaging. See the [showcase](examples/bottle_showcase/README.md).
- **External libraries:** [libraries in AquariusLang, C, C++ or Rust](docs/external-libraries.md) built from one
  source tree for every target with the [library SDK](runtime/sdk/README.md), exactly like the default ones.
- **Threads:** [Thread / 執行緒](docs/threading.md) provides isolated workers,
  timed joins, results, cancellation and safe closing on desktop, web and Android.
- **Networking:** [Network / 網路](libraries/network/README.md) provides HTTP(S), cookies,
  cancellable requests, streamed responses, WebSockets and native TCP/TLS/UDP, with an HTTP(S)
  server (routes, static files, SSE, sessions) on Windows, Linux and Android.
- **Graphics:** [WebGPU, Processing drawing, text input and GPUMath](libraries/graphics/README.md) on
  wgpu-native or the browser's WebGPU, plus desktop [OpenGL](libraries/opengl/README.md).
- **Models and PBR:** [OBJ/MTL parsing and shared WGSL rendering](libraries/graphics/docs/pbr-models.md),
  plus [named shader textures, cube mip chains and floating render targets](libraries/graphics/docs/wgpu-textures.md).
- **Audio:** [miniaudio decoding and mixing](libraries/audio/README.md),
  including files, tones, loops, panning, pitch and offline sample output.
- **Inference:** [ONNX Runtime and the MediaPipe pose tracker](libraries/onnx/README.md) with ranked execution
  providers per platform, optional CUDA/TensorRT through `[onnx] cuda = true` in `aqua.toml`, and ONNX Runtime built
  from C++ for the web. See the [pose example](examples/mediapipe/README.md).

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

The compiler package is self-contained: users do not need the .NET SDK or a runtime. Keep the whole folder together:
`runtime/<target>/` holds the runtime packs applications are built from, `web-glue/` the web glue, `libraries/` the
default libraries' manifests and Aquarius sources, `sdk/` the library SDK and `examples/` the examples.

## Build and package a release

`build-release.bat` produces the **Windows x64** compiler package. Prepare on Windows x64:

| Tool | Purpose |
| --- | --- |
| Windows PowerShell 5.1 or PowerShell 7 | Runs the scripts. |
| .NET 8 SDK | Builds, tests and publishes the compiler. |
| CMake 3.20+ and Ninja | Build the runtime packs. |
| The pinned toolchain | `scripts/install-toolchain.ps1 -Targets windows-x64,android-arm64-v8a,android-x86_64` installs LLVM Clang, the llvm-mingw sysroot, the WASI sysroot, Wasmtime and a JDK (never MSVC). |
| Android SDK (`ANDROID_HOME`) with NDK 28.2 | Only for the Android packs. |
| Node.js | Web tests in Chromium (`npm install` in `scripts/`). |

```powershell
.\build-release.bat
.\build-release.bat -Help
.\build-release.bat -Targets windows-x64,web         # fewer runtime packs; add linux-x64 to build it in WSL
.\build-release.bat -SkipRuntime                     # reuse the packs in .native-build/install
```

The script builds the runtime packs (`scripts/build-runtime.ps1`), builds and tests the compiler, publishes `dist/aqua/`
(`scripts/publish-cli.ps1`), checks the published `aqua.exe` without an installed .NET (help, compile and run, a
website and a standalone executable) and archives it:

```text
dist/releases/AquariusCompiler-win-x64-<build-id>.zip
dist/releases/AquariusCompiler-win-x64-<build-id>.zip.sha256
```

Verify with `Get-FileHash "package.zip" -Algorithm SHA256`. Failures return a nonzero exit code. For Linux see the
[Linux guide](docs/linux.md) (`scripts/publish-cli.sh`); for Android, the [Android guide](docs/android.md).

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
aqua build app.wasm --target linux -o dist\app
aqua build app.wasm --target android -o dist\app.apk
```

| Option | Meaning |
| --- | --- |
| `--root` | Source root; preserves relative module and asset paths. |
| `--assets` | Include a file or directory; may be repeated. |
| `--entry` | Entry `.aqua` module, defaulting to the first source input. |
| `-o` | Output `.wasm` / `.exe` / `.apk` path or website directory. |
| `--target wasm` | Default: standard Wasm containing the program, modules and assets. |
| `--target web` | A static site: `program.wasm`, the web runtime, the glue and assets; requires `-o`. |
| `--target windows`, `--target linux` | A standalone executable: the runtime with the application appended; requires `-o`. |
| `--target android` | A signed APK for arm64-v8a and x86_64; see [Android](docs/android.md). |

List every imported script, including dynamic imports. Assets must be under the
root. Module names retain their relative `.aqua` paths. Compilation and export
do not execute the program, and failed builds preserve existing output.

The `aquarius.application` Wasm custom section contains the binary ABI 4 metadata (modules, assets, the library
modules the application imports and the native libraries it needs); embedded assets are a raw `aquarius.assets`
section. Exported applications carry only the runtime files and libraries they use.

Serve websites over localhost or HTTPS. Graphics requires a WebGPU adapter.
Platform-specific services report unsupported capabilities when unavailable.
See the [CLI](compiler/AquariusCli/README.md), [application format](compiler/AquariusCompiler/README.md),
[build targets](compiler/AquariusBuild/README.md) and [web glue](web-glue/README.md).
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
number. Both preserve the variable's numeric type. Integers are 32-bit: `+ - *`
wrap around on overflow (like C# and Java), and integer `/` and `%` by zero are
runtime errors. Results are identical on every platform.

### Optional types

Variables, parameters and return values can carry `: type` after the name, in
either syntax. Untyped code keeps working unchanged:

```aquarius
var count: int = 0;
var fib = function(n: int): int {
    if (n < 2) { return n; }
    return fib(n - 1) + fib(n - 2);
};
變數 比例 = 函式(a: 整數, b: 整數): 雙精度 { 回傳 雙精度(a) / b; };
var flags: bool[] = Array.Make(10, true);
```

| English | 繁體中文 | Type |
| --- | --- | --- |
| `int` | `整數` | 32-bit integer |
| `float` | `浮點數` | 32-bit float |
| `double` | `雙精度` | 64-bit float |
| `bool` | `布林` | true / false |
| `string` | `字串` | text |
| `int[]`, `float[]`, `double[]`, `bool[]` | `整數[]` etc. | array with a fixed element type |
| `array` | `陣列` | array whose elements may be any values (nested arrays, for example) |
| struct name | 結構名稱 | a [struct](docs/structs.md) declared with `struct` / `結構` |

`struct Node { left: Node; right: Node; value: int }` declares a struct type; `Node(left: a, value: 3)` or
`Node(a, b, 3)` constructs one (fields left out are `null`) and `tree.value` reads or writes a field.
Type names only mean something after `:`; they are not reserved words. A mismatch
the compiler can see is a compile error, anything else is checked at run time.
Integers widen to float and double automatically; the other direction needs
`int(x)` / `整數(x)` (truncates toward zero, saturating at the integer range),
`float(x)` / `浮點數(x)` or `double(x)` / `雙精度(x)`.

Builtins such as `print` / `印出`, `len` / `長度` and `import` / `匯入` have
bilingual names. The portable [Array](docs/arrays.md), [Hash and String](docs/hash-and-string.md)
modules run in shared Wasm. Module names, properties, constants and event strings
follow their library's API.

`currentTime()` / `目前時間()` returns monotonic double milliseconds for elapsed
time measurements; subtract two readings and divide by `1000.0d` for seconds.
`currentDate()` / `目前日期()` returns the host's local date as `yyyy-MM-dd`.
`typeOf(value)` / `型別(值)` returns stable tags such as `integer`, `double`,
`string`, `array`, `hash` and `null`.
See the [language guide](compiler/AquariusCompiler/LANGUAGE.md) for detailed semantics.

## Libraries and examples

Import a library with `var drawing = import("Processing");` or
`變數 繪圖 = 匯入("Processing");`. Examples live in `examples/`, the single example tree;
release packages include it. Each default library is one folder of `libraries/` with its README:

| Library | Modules | Examples |
| --- | --- | --- |
| [graphics](libraries/graphics/README.md) | Processing, WGPU, GLM, TextInput, Window, Fonts, Models, PBR, GPUMath | [Processing showcase](examples/processing_showcase/README.md), [WGPU compute](examples/wgpu_compute/README.md), [triangle](examples/wgpu_triangle/README.md), [multilingual input](examples/multilingual_input/README.md) |
| [opengl](libraries/opengl/README.md) | GLFW, GLAD, GL, STBImage (desktop) | [Textured cube](examples/opengl_cube/README.md) |
| [application](libraries/application/README.md) | Files, Paths, Images, Serialization, Storage, Clipboard, Application, Tasks, TextEdit | [Application libraries](examples/application_libraries/main.aqua) |
| [audio](libraries/audio/README.md) | Audio | |
| [camera](libraries/camera/README.md) | Webcam, Camera | [Webcam](examples/webcam/README.md) |
| [network](libraries/network/README.md) | Network | [Network lab](examples/network_lab/README.md) |
| [onnx](libraries/onnx/README.md) | ONNX, MediaPipe | [Pose](examples/mediapipe/README.md) |
| [terminal](libraries/terminal/README.md) | Terminal | |
| [input](libraries/input/README.md) | AndroidInput | |

The language itself has the portable [Array](docs/arrays.md), [Hash and String](docs/hash-and-string.md) and
[Thread](docs/threading.md) modules. [Starship](examples/starship_expedition/README.md),
[bilingual functions](examples/bilingual_library/README.md) and [arrays in both syntaxes](examples/arrays/README.md)
show the language. Jolt physics is the independent `Aqua_JoltPhysics` Bottle library.

## Development and tests

From the repository root:

```powershell
scripts/install-toolchain.ps1 -Targets windows-x64                     # pinned LLVM, MinGW sysroot, Wasmtime
scripts/build-runtime.ps1 -Targets windows-x64,web                     # runtime packs in .native-build/install
dotnet build AquariusLang.sln -c Release
dotnet test compiler/AquariusTests -c Release
scripts/test-libraries.ps1 -Targets windows-x64,web                    # every library's tests (add -Devices)

# Run the CLI from source
dotnet run --project compiler/AquariusCli -- build examples/increment.aqua -o .native-build/increment.wasm
dotnet run --project compiler/AquariusCli -- run .native-build/increment.wasm
dotnet run --project compiler/AquariusCli -- repl
```

Read [CLAUDE.md](CLAUDE.md) before changing anything: it lists the architecture rules every change must follow, which
`dotnet test` checks. The [test guide](compiler/AquariusTests/README.md) describes the compiler tests; library tests
run on Windows, Linux (WSL), the web (Chromium) and Android (`-Targets android-x86_64 -Devices`). See the
[architecture](ARCHITECTURE.md), the [library SDK](runtime/sdk/README.md) and the [web glue](web-glue/README.md).

## Project structure

| Directory | Responsibility |
| --- | --- |
| `compiler/` | C#, compile time only: `AquariusCompiler` (front end, Wasm emission, packaging), `AquariusBuild` (build targets), `AquariusBottle`, `AquariusCli`, `AquariusLanguageServer`, `AquariusTests`. |
| `runtime/` | C, the run time of every target: `guest/` (the runtime linked into applications), `host/` (the host core), `platform/<os>/` (thin per-OS layers), `sdk/` (the library SDK and toolchain files). |
| `libraries/<name>/` | The default libraries, one self-contained folder each. |
| `web-glue/` | The only JavaScript: starts applications and exposes Web APIs, one folder per API. |
| `scripts/` | Toolchain pins and installers, runtime builds, library tests, web and Android runners, release packaging. |
| `examples/`, `tests/` | Examples and shared language test data. |
| `editors/vscode`, `website/` | The VS Code extension and the introduction website. |

## Editors and further documentation

The [VS Code extension](https://marketplace.visualstudio.com/items?itemName=aquariuslang.aquariuslang-tw)
provides bilingual highlighting, snippets, live diagnostics, completion, hover,
definitions and symbols. See the [VS Code guide](editors/vscode/README.md) for
installation, configuration and VSIX packaging. Other editors can use the
[language server](compiler/AquariusLanguageServer/README.md), which analyzes source without executing it.

The [AquariusLang introduction website](https://aquarius-language.github.io/AquariusLangTW/)
has [source and local instructions](website/README.md) and a
[GitHub Pages deployment guide](website/docs/github-pages.md).
Some detailed subsystem guides are written in Traditional Chinese.

## Name and license

**星泉** combines the starry sky of Aquarius with flowing spring water:
inspiration shines like stars, and creativity flows like a spring.

Licensed under the [MIT License](LICENSE), Copyright (c) 2026 Temple Lin.
Third-party dependencies keep their own licenses, pinned inside each library's folder and installed with the runtime
packs (`licenses/`); release packages include them.
