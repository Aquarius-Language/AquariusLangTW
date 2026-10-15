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
`scripts/publish-apphost.ps1` after runtime or ABI changes.

Exit codes: 0 success, 1 compiler/runtime/IO failure, 2 invalid command/options.
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
