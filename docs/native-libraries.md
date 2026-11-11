# Native library outputs (design, implementation spec)

Status: design approved 2026-11-11; phases 1 and 2 implemented 2026-11-11 (see "Status of the phases" and "Deviations"
below). Full design document (with rationale, diagrams and the complete test plan):
https://claude.ai/artifact/AWcNZdKyLjU3abUSrPv1uL . This file is the working spec kept in the repository; keep it in
step with the code as phases land. Follow CLAUDE.md throughout.

## Goal

Any `[project]` or `[library]` can also be built into a native library per platform (`.dll`, `.so`, `.dylib`, `.a`,
`.o`, framework/xcframework, `.aar`, web `.wasm`), exposing the functions marked `匯出` / `export` through a typed C
ABI, plus a first-class binding per platform language: C, C++ and Rust on Windows, Linux and macOS; Swift on iOS and
macOS; Java (serving Kotlin) on Android; JavaScript (with a `.d.ts`) on the web.

## The `匯出` / `export` keyword

```aquarius
# Area of a rectangle.            (the comment above an export, # lines or a ## ... ## block, documents it in bindings)
匯出 變數 面積, area = 函式(寬: 雙精度, 高: 雙精度): 雙精度 { 寬 * 高; };
匯出("distance") 變數 距離 = 函式(a: 點, b: 點): 雙精度 { ... };
匯出 結構 點, Point { x: 雙精度; y: 雙精度 }
export var scale = function(values: double[], factor: double): double[] { ... };
```

- Contextual keyword, handled like `結構` / `常數` in `Parser.cs`: only at the start of a statement, when followed by
  `變數`/`var`, `常數`/`const`, `結構`/`struct`, or `("name")` and then one of those. Elsewhere it is an ordinary name.
- AST: `LetStatement.Exported` + `ExportName`; `StructStatement` likewise. No new IR; codegen of programs without the
  keyword must stay byte-identical (codegen goldens).
- Rules (TypeChecker, bilingual error messages): top level of the entry module (or a `[library]` facade module) only;
  initializer is a function literal; every parameter and the result annotated with an exportable type; the binding is
  implicitly constant; not async (yet); a public ASCII name is required (the `("name")`, else the first ASCII alias);
  names must not collide after case conversion.
- Naming: C and Rust `snake_case` (C with the prefix: `geo_area`); Swift, Java/Kotlin, JavaScript `lowerCamelCase`.
- Exportable types: `整數/int` -> `int32_t`; `長整數/long` -> `int64_t`; `浮點數/float` -> `float`; `雙精度/double` ->
  `double`; `布林/bool` -> `bool`; `字串/string` -> `const char *` UTF-8 in, `char *` out (freed with `<prefix>free`);
  typed number/bool arrays -> pointer + `size_t` count in, `<prefix><type>_array {data, count}` out (freed with
  `<prefix>free`); exported structs with scalar/string fields -> C struct by value; no result annotation -> `void`.
  Everything is copied at the boundary. Invalid UTF-8 or NULL with a nonzero count fails the call.
- The export table is written into a custom section `aquarius.exports` (versioned) of the application `.wasm`. ABI 4
  metadata is unchanged.

## aqua.toml

```toml
[export]
name = "geometry"        # file base name; default: project name in lower case
prefix = "geo_"          # C symbol prefix; default: name + "_"
kinds = ["shared"]       # "shared", "static", "object"; default ["shared"]
module = "Geometry"      # [library] only: the facade [[modules]] entry

[export.<platform>]      # optional overrides, keyed by platform (KnownPlatforms), never by a hard-coded target list
kinds = [...]            # must be allowed on the platform
bindings = [...]         # "c", "cpp", "rust", "swift", "java", "javascript"
package = "..."          # folder | framework (macos) | xcframework (ios) | aar (android)
targets = [...]          # CPU targets of the platform, from toolchain.json
import_libraries = ["mingw", "msvc"]   # windows
module = "Geometry"                    # ios/macos Swift module name
java_package = "org.example.geometry"  # android (and desktop java binding); java_class defaults to the project name
```

Defaults: windows/linux: kinds shared|static|object, bindings c,cpp,rust, package folder. macos: + swift. ios: kinds
shared|static, bindings c,swift, package xcframework. android: bindings c,java, package aar. web: kind shared only,
binding javascript. Unknown keys/values are manifest errors with positions (LSP shows them). `[export]` without any
`匯出` in the entry module is an error. Only the root manifest's `[export]` applies. Dependencies are compiled in.

CLI: `aqua build . --target <platform> [--kind shared,static,object] -o dist`; `aqua export header . -o file.h`.
Without `--kind` and without `[export]` every target builds exactly what it builds today. Output layout:
`dist/<target>/` (desktop), `dist/android/`, `dist/web/`, `dist/ios/`, each with `include/`, bindings and `licenses/`.

## Runtime

- `runtime/embed/`: `embed.c` (lifecycle, one instance per process, internal lock, thread-local last error),
  `exports.c` (C thunks expanded from the compiler-written X-macro table `exports.def`), `jni.c` (JNI stubs from the
  same table, registered with `RegisterNatives` in `JNI_OnLoad`), public `aqua/embed.h`, `CMakeLists.txt` installed
  into each runtime pack as `embed/` (like `aot/` today). Per-OS link rules in `runtime/platform/<os>/embed.cmake`.
- A thunk converts arguments to Aquarius values, calls the function through the guest's `invoke` export (the
  `aqua_invoke` path), runs the scheduler to completion, converts the result. Errors return zero and set the message.
- Every library exports `<prefix>initialize`, `<prefix>shutdown`, `<prefix>last_error`, `<prefix>free`,
  `<prefix>abi_version`. First call initializes lazily and runs the entry module's top-level code once.
- Engine: ahead of time (wasm2c + pinned Clang, `AheadOfTime.Compile`) on native targets; the browser's engine on web
  through a new `loadLibrary()` in `web-glue/core` that builds the JS functions from the export table at load time
  (no generated JavaScript, rule 5).
- Linking: shared outputs hide everything but `<prefix>*` (Windows `.def` + static libc++/libunwind/winpthread, MinGW
  and MSVC import libraries via llvm-dlltool; Linux version script, `-Bsymbolic`, SONAME from the version major;
  Android per ABI, 16 KB pages; macOS exported symbols list, `@rpath` install name, ad hoc codesign). Static/object
  outputs are one prelinked relocatable object with non-exported symbols localized (ELF/Mach-O); Windows static keeps
  prefixed internals (no `-r` in lld COFF).
- Projects importing window libraries cannot be exported yet (compile error).

## Compiler structure (extension model)

- `ExportTable` (AquariusCompiler): the single description every consumer reads.
- `INativeLibraryPlatform`, one class per platform in `compiler/AquariusBuild/Libraries/Platforms/`: allowed kinds,
  default bindings, packages, manifest keys. File naming stays in `RuntimeTarget` (`StaticLibraryName`,
  `ObjectSuffix`, `ImportLibraryNames`).
- `IBindingGenerator`, one class per language in `compiler/AquariusBuild/Bindings/`.
- `NativeLibraryOutput`: the step each `IWasmBuildTarget` calls when a kind is requested.
- Architecture tests: every non-planned platform has a descriptor and an `embed.cmake`; every generator has a golden;
  no target list repeated.

## Tests (all must pass on Windows, Linux/WSL, web/Chromium and Android before a phase is done)

Parser/lexer; one type-checker test per rule; manifest + LSP diagnostics; export section round trip; codegen goldens
unchanged; binding goldens for every generator including Swift/modulemap/plists; `tests/exports/` consumer programs
in C, C++, Rust, Java (desktop JVM and Android), Kotlin (Android) and JavaScript all printing the same lines against
one `.expected`; parity with `aqua run`; binary audit (exported symbols == table, only system dependencies, SONAME,
16 KB alignment, import libraries); two libraries in one process (shared, static, object); lifecycle x1000, 8 calling
threads, reentrancy error; ASan/LSan/UBSan consumers on Linux; full existing suites. Runner:
`scripts/test-exports.ps1`, called by `build-release.ps1`. Rust and Kotlin compilers are pinned in
`scripts/toolchain.json` for tests only.

## macOS and iOS

Implemented in full but unverified until a Mac or the macOS CI runner runs `test-exports.ps1`. Until then
`aqua build --target macos|ios --kind ...` prints a one-line "not verified yet" notice, and docs say the same. Goldens
and stub-pack target tests cover what a PC can check.

## Phases

1. Language and manifest: keyword, type rules, `[export]`, export table, C header, LSP.
2. Embed runtime and desktop shared libraries (Windows, Linux), C and C++ bindings, `test-exports.ps1`.
3. Rust binding, static and object outputs, isolation and sanitizer tests.
4. Web (`loadLibrary`, `.d.ts`) and Android (`.so`, JNI, Java class, `.aar`).
5. Apple outputs and Swift, behind the unverified notice.
6. Showcase project (separate folder) with a build script and one demo per platform language.
7. Later: window attach/pump, async exports, opaque value handles, Wasmtime engine option, shared runtime.

## Status of the phases

Phases 1 and 2 are implemented on branch `native-library-exports`:

- Language: `Parser.parseExportStatement` (contextual like `常數`; `Lexer.Peek(n)` looks past `("name")`),
  `LetStatement.Export` / `StructStatement.Export` (`ExportMark`), struct aliases, the rules in
  `TypeChecker.Exports.cs`, `ExportTable` (built in `LoweringCompiler.Compile`, written by `WasmCompiler` for the entry
  module), `ExportTable.Read(WasmProgram)`.
- Manifest: `ProjectManifest.Export` parsed by `AquariusBottle/ExportSection.cs` against
  `AquariusBuild/Libraries/INativeLibraryPlatform` descriptors (`Platforms/`), into `NativeLibrarySettings`.
- Bindings: `AquariusBuild/Bindings/` (`CBinding`, `CppBinding`, shared names in `CNames`); `aqua export header`.
- Build: `NativeLibraryOutput` (called by `ExecutableTarget` when `--kind` is given or the project has `[export]`),
  `EmbedTable` (exports.def and the symbol list), `RuntimeTarget.SharedLibraryName` / `VersionedSharedLibraryName` /
  `SharedLibraryFolder` / `ImportLibraryNames`, `AheadOfTime.Compile` with the pack's `embed/` project.
- Runtime: `runtime/embed/` (`embed.c`, `exports.c`, `aqua/embed.h`, `no_window.c`, `runtime.cmake` for the pack,
  `CMakeLists.txt` for each library), `runtime/platform/desktop/embed.c` (the desktop hooks in place of the launcher),
  `runtime/platform/windows|linux/embed.cmake` (link rules). The process entries moved to
  `runtime/platform/windows/entry.c` and `runtime/platform/posix/entry.c` so libraries link the layers without them.
- Output: `<output>/<target>/` with `include/<name>.h` and `<name>.hpp`, `licenses/`, and on Windows
  `bin/<name>.dll`, `lib/lib<name>.dll.a` (MinGW), `lib/<name>.lib` (MSVC, llvm-dlltool); on Linux
  `lib/lib<name>.so.<major>` (the SONAME) and the link `lib/lib<name>.so`.
- Tests: `compiler/AquariusTests/exports/ExportTest.cs` (keyword, one case per rule, table and section, binding and
  exports.def goldens in `exports/goldens/`, manifest errors with positions), `architecture/NativeLibraryRulesTest.cs`,
  a language server test, and `scripts/test-exports.ps1` with `tests/exports/` (geometry and counter libraries, C and
  C++ consumers against `geometry.expected`, parity through `geometry/parity.aqua`, binary audit, two libraries,
  lifecycle x1000, 8 threads, reentrancy, sanitizers on Linux), run by `build-release.ps1`.

Phase 3 remains as planned (Rust binding, static and object outputs with localized symbols, their isolation tests),
plus what the deviations below defer: `[library]` exports through `[export] module`, and default libraries inside
outputs.

## Deviations from the design

- Documentation comments are the language's own: `#` lines or a `## ... ##` block directly above the export.
- `匯出("name")` sets only the public name; the Aquarius binding keeps its own names.
- Only the entry module's exports form the table. `匯出` in an imported module is not an error: it is an ordinary
  declaration there, so a program (the parity test) can import an exporting module and call it under `aqua run`.
- Struct arguments are built by a hidden constructor function the compiler adds for each exported struct
  (`export@<Name>`, a name no source can write), because the guest's `invoke` export calls closures only. Programs
  without exported structs are unchanged.
- exports.def rows are `AQUA_STRUCT/FIELD/STRUCT_END` and `AQUA_FUNCTION/PARAMETER (or NO_PARAMETERS)/CALL/ARGUMENT/
  RETURN`: a C definition needs its parameter list, so each function is a sequence of rows that exports.c expands in
  four passes (struct arguments, struct results, functions, the name table); types are tokens (`DOUBLE`,
  `INT_ARRAY`, `STRUCT(point)`, `VOID`).
- The embed runtime links the host core without default libraries yet: an application whose metadata needs any native
  library is rejected at build time (the design rejected only window libraries). Windows cannot be opened
  (`no_window.c`).
- A library's linear memory uses malloc and explicit bounds checks (`WASM_RT_USE_MMAP=0`,
  `WASM_RT_MEMCHECK_BOUNDS_CHECK=1`): a library installs no signal handlers in its host process.
- C structs and functions share one namespace (`<prefix><snake_case>`), so the collision rule covers both; exported
  struct fields need ASCII names; non-ASCII parameter names become `arg<N>` in bindings (keywords get a `_`).
- The last error is NULL after a successful call; a reentrant call's refusal does not replace the running call's own
  outcome. Reentrancy is tested on POSIX only (a timer signal calls the library during a call); Windows has no way to
  interrupt a thread with a call, and the check is the same C code.
- `AquariusBottle` references `AquariusBuild` so the manifest can check `[export.<platform>]` against the descriptors.
- With `[export]`, `aqua build --target windows|linux` builds the libraries instead of the executable.
- `licenses/` holds AquariusLang's license and a THIRD-PARTY notice (WABT's wasm2c runtime, and on Windows the
  statically linked MinGW-w64 and compiler-rt pieces): the pinned WABT archive ships no license file.
- `scripts/test-exports.ps1` tests the host's own desktop target (Windows on Windows; Linux on Linux or in WSL), since
  desktop outputs build only on their own host.
