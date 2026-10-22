# Unified compiler CLI

`aqua` compiles Aquarius to WebAssembly and runs compiled applications with Wasmtime.
It also supports optional `aqua.toml` projects and the [Bottle library manager](../AquariusBottle/README.md).

```powershell
aqua build main.aqua lib/tools.aqua --root . --assets assets -o app.wasm
aqua run app.wasm
aqua build main.aqua lib/tools.aqua --root . --assets assets --target web -o web
aqua build main.aqua lib/tools.aqua --root . --assets assets --target windows -o app.exe
aqua build app.wasm --target web -o web
aqua build app.wasm --target windows -o app.exe --entry lib/tools.aqua
aqua build app.wasm --target linux -o app
aqua build app.wasm --target windows -o app.exe --onnx-cuda true
aqua build app.wasm --target windows -o app.exe --hide-terminal true
aqua new MyApp
aqua new MyLibrary --type library
aqua bottle install ../MyLibrary --project MyApp --version ">= 0.1, < 0.2"
aqua build MyApp --locked --offline
aqua build MyApp --platform windows -o dist/MyApp.wasm
aqua bottle uninstall MyLibrary --project MyApp
aqua repl
```

The default target is `wasm`. `--root` and repeatable `--assets` apply to source
builds; compiled inputs already contain their modules and resources. `--entry`
selects a relative `.aqua` module identity. List every source module, including
dynamic imports. Use `--` before filenames beginning with a dash. Platform targets
require `-o`; the retired `.bottle` archive format is unrelated to `aqua bottle`.

Project builds accept a directory, a `.toml` path, or no input inside an
`aqua.toml` project. Their entry/sources/assets come from TOML; source-file inputs
inside a project also use its manifest. Non-project source builds retain their
existing behavior. `--locked` prevents lock changes; `--offline` disables network
restore. Third-party libraries require explicit versions and supported platforms.
`--platform` selects conditional assets and compatibility metadata for intermediate
Wasm output. It requires a TOML project and cannot be combined with an export
target such as `--target web`. See [Terminal](../docs/terminal.md) for the default
process-launch API.

Builds do not execute application code. Failed compilation or export preserves
previous artifacts. Web output needs localhost/HTTPS, with WebGPU for graphics.
Windows output uses the prepublished self-contained runtime pack; rebuild it with
`scripts/publish-apphost.ps1` after runtime or ABI changes. NVIDIA CUDA for ONNX is opt-in:
`[onnx] cuda = true` in `aqua.toml` (or `--onnx-cuda true`, which also overrides the manifest)
selects the `build-targets/<rid>-cuda` pack published with `-OnnxCuda`; see
[the ONNX guide](../docs/onnx.md#nvidia-cuda-opt-in).

Windows desktop apps show a terminal by default. To build a GUI executable that
opens without a terminal, pass `--hide-terminal true`, or add this to `aqua.toml`:

```toml
[desktop]
hide_terminal = true
```

Build that project with `aqua build . --target windows -o app.exe`. An explicit
`--hide-terminal false` overrides the manifest for console debugging. Omitted or
false settings preserve console behavior for existing projects. The option applies
to Windows exports from sources, Wasm, or TOML projects; only the root project's
desktop settings apply. Other targets ignore the TOML setting. Linux executables
do not allocate a terminal window themselves and do not accept this Windows option.
GUI exports require an updated Windows runtime pack; when building from source,
republish it with `scripts/publish-apphost.ps1`. Old packs remain usable for console
exports and produce a clear rebuild instruction if terminal hiding is requested.

Exit codes: 0 success, 1 compiler/runtime/IO failure, 2 invalid command/options.

CUDA-enabled executable packs include the NVIDIA inference runtime libraries and
license notices inside the EXE. `[onnx] cuda = true` is sufficient for project
exports; no project-specific copy script, Python, or CUDA toolkit is required.
Recipients need compatible NVIDIA hardware/drivers. Old provider-only CUDA packs
are rejected and must be republished; standard CPU packs remain compatible.
REPL compiles each input into Wasm and preserves the session environment.

Build with the .NET 8 SDK after preparing core vendor assets:

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
dotnet build AquariusLang.sln -m:1
dotnet test AquariusTests -m:1
npm test --prefix AquariusWebCompiler
```

See [Wasm applications](../AquariusPackaging/README.md),
[platform targets](../AquariusBuild/README.md) and [architecture](../ARCHITECTURE.md).
