# AquariusTests

The tests of the compiler and its tools. Programs run on the native runtime pack of this machine, the same runtime
applications use: build it first (`scripts/build-runtime.ps1 -Targets windows-x64`, or `linux-x64` on Linux), then

```powershell
dotnet test compiler/AquariusTests
```

Tests that run programs are `[RuntimeFact]` / `[RuntimeTheory]` and are skipped when the runtime pack is missing;
`WasmTestHost` compiles and runs programs (`Eval` returns a program's final value with its `型別` tag, `Run` its
output and exit code).

| Folder | Covers |
| --- | --- |
| root, `compiler/` | language semantics in Chinese, English and mixed syntax (numbers, closures, loops, arrays, hashes, modules, the portable Array/Hash/String modules, time builtins, flood fill), the Wasm emitter's tiers (`EmitterTiers` compares optimized with unoptimized code), call conventions, deep recursion, guest memory |
| `lexer/`, `parser/`, `ast/`, `object/` | the front end and the IR's constant values |
| `threading/` | the Thread / 執行緒 module of the native host |
| `packaging/` | application metadata (ABI 4 import table and libraries; ABI 3 still loads; ABI 2 rejected), assets, library linking with the fixture libraries in `packaging/fixtures/`, executable overlays |
| `targets/` | the Windows/Linux executable, web site and Android APK targets (the APK test needs `ANDROID_HOME`, a JDK and the Android packs; the Chromium run of a site needs `npm install` in `scripts/`) |
| `cli/` | `aqua build/run/new/repl` and the examples |
| `bottle/` | Bottle projects and libraries (manifest_version 1 and 2), the cache, locks, external libraries' native binaries, `aqua library build` (needs CMake, Ninja and the pinned LLVM; set `AQUARIUS_CMAKE` / `AQUARIUS_NINJA` when they are not on `PATH`) |
| `languageserver/` | the language server over stdio |
| `architecture/` | the architecture rules (CLAUDE.md): C# only in compiler/, JavaScript only in web-glue/ and tools, self-contained libraries with valid manifests that match what the runtime registers, no code generators, no MSVC, the guest runtime contract and codegen goldens |

Library behavior is tested by each library's `tests/` (`scripts/test-libraries.ps1` runs them on the runtime packs).
`AQUARIUS_WASM_BENCHMARKS=1` enables the optional performance measurements; correctness never depends on timing.
After an intended change of the emitter or the guest runtime, update the codegen goldens with
`AQUARIUS_UPDATE_CODEGEN_GOLDENS=1 dotnet test compiler/AquariusTests --filter CompiledOutputMatchesGoldens`.

Tests skipped with a `RuntimeBug` reason (`WasmTestHost.cs`) found runtime bugs; they keep the correct expectation.
