# Unified compiler CLI

`aqua` compiles Aquarius to WebAssembly and runs compiled applications with Wasmtime.

```powershell
aqua build main.aqua lib/tools.aqua --root . --assets assets -o app.wasm
aqua run app.wasm
aqua build main.aqua lib/tools.aqua --root . --assets assets --target web -o web
aqua build main.aqua lib/tools.aqua --root . --assets assets --target windows -o app.exe
aqua build app.wasm --target web -o web
aqua build app.wasm --target windows -o app.exe --entry lib/tools.aqua
aqua repl
```

The default target is `wasm`. `--root` and repeatable `--assets` apply to source
builds; compiled inputs already contain their modules and resources. `--entry`
selects a relative `.aqua` module identity. List every source module, including
dynamic imports. Use `--` before filenames beginning with a dash. Platform targets
require `-o`; `.bottle` is no longer part of the pipeline.

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
