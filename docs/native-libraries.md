# Native library outputs (design, implementation spec)

Status: design approved 2026-11-11; phases 1 to 4 implemented 2026-11-11 (see "Status of the phases" and "Deviations"
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

Phases 1 to 4 are implemented on branch `native-library-exports`; phase 5 (Apple outputs and Swift) and phase 6 (the
showcase) remain.

- Language: `Parser.parseExportStatement` (contextual like `常數`; `Lexer.Peek(n)` looks past `("name")`),
  `LetStatement.Export` / `StructStatement.Export` (`ExportMark`), struct aliases, the rules in
  `TypeChecker.Exports.cs`, `ExportTable` (built in `LoweringCompiler.Compile`, written by `WasmCompiler` for the entry
  module), `ExportTable.Read(WasmProgram)`.
- Manifest: `ProjectManifest.Export` parsed by `AquariusBottle/ExportSection.cs` against
  `AquariusBuild/Libraries/INativeLibraryPlatform` descriptors (`Platforms/`), into `NativeLibrarySettings`. A `[library]`
  builds through `[export] module` (`ProjectBuilder.CompileExportedLibrary`: the facade module is the entry).
- Kinds: `--kind shared,static,object` (or `[export] kinds`) selects library outputs; `--kind executable` builds a
  target's usual artifact, also from a project with `[export]` (a native library is an additional output: without
  `--kind`, a project with `[export]` builds its libraries, one without builds what it always did).
- Bindings (`AquariusBuild/Bindings/`): `CBinding` (the ABI), `CppBinding`, `RustBinding` (a crate with `build.rs`
  and safe wrappers returning `Result`), `JavaBinding` (one final class, static natives, nested struct classes,
  `AquariusException`, `NonNull`/`Nullable`), `TypeScriptBinding` (the web's `.d.ts`); `aqua export header`.
- Build: `NativeLibraryOutput` (desktop targets, `BuildAndroid` for every Android ABI, `BuildWeb`), `EmbedTable`
  (exports.def, symbols; the Java rows with the java binding), `CMakePackage` (`lib/cmake/<name>/<name>-config.cmake`
  with `<name>::shared|static|object`), `JavaClasses` (the binding's jar), `AndroidLibraryPackage` (the .aar).
- Runtime: `runtime/embed/` (`embed.c`, `exports.c`, `jni.c`, `aqua/embed.h`, `no_window.c`, `runtime.cmake` for
  native packs, `CMakeLists.txt` for each library; `table.c` and `web.c` in the web's host side module),
  `runtime/platform/desktop/embed.c` and `runtime/platform/android/embed.c` (the layers' library halves), and each
  platform's link rules `runtime/platform/<os>/embed.cmake` (Windows also `embed-archive.cmake`). Default libraries
  without windows link in: each pack installs their link interface (`embed/libraries/<name>/link.cmake`, archives,
  runtime files, licenses), and `exports.c` registers them. `web-glue/core/library.mjs` is `loadLibrary()`.
- Outputs: `<output>/<target>/` on the desktops (`include/`, `bin/` or `lib/`, `lib/cmake/`, `rust/`, and with the
  java binding `java/` and `lib/<name>.jar`; the linked libraries' runtime files such as ONNX Runtime beside the
  library; `licenses/`); `<output>/android/` (`lib/<abi>/lib<name>.so`, `include/`, `java/`, `lib/<name>.jar`,
  `<name>.aar`, `licenses/`); `<output>/web/` (`<name>.wasm`, `<name>.d.ts`, `runtime/`, `glue/`, `licenses/`).
- Tests: `compiler/AquariusTests/exports/ExportTest.cs` (keyword, one case per rule, table and section, goldens of
  every generator and of exports.def in `exports/goldens/`, manifest errors with positions),
  `architecture/NativeLibraryRulesTest.cs`, language server and CLI tests, and `scripts/test-exports.ps1 [-Android]`
  with `tests/exports/`: the geometry, counter, services (default libraries) and shapes (`[library]`) libraries;
  consumers in C, C++, Rust, Java (desktop JVM and Android), Kotlin (Android) and JavaScript (Chromium) printing
  `geometry.expected`; parity with `aqua run`; binary audits (exports, dependencies, SONAME, import libraries, static
  and object globals, Android 16 KB pages, the .aar); two libraries as shared, static and object; lifecycle x1000;
  8 threads; reentrancy; sanitizers on Linux. Rust, Kotlin and the JDK are pinned in `scripts/toolchain.json` for
  tests only (`install-toolchain.ps1 -Tools rust,kotlin,jdk`).

## Deviations from the design

- Documentation comments are the language's own: `#` lines or a `## ... ##` block directly above the export.
- `匯出("name")` sets only the public name; the Aquarius binding keeps its own names.
- Only the entry module's exports form the table. `匯出` in an imported module is not an error: it is an ordinary
  declaration there, so a program (the parity test) can import an exporting module and call it under `aqua run`.
- Struct arguments are built by a hidden constructor function the compiler adds for each exported struct
  (`export@<Name>`, a name no source can write), because the guest's `invoke` export calls closures only. Programs
  without exported structs are unchanged.
- exports.def rows are `AQUA_STRUCT/FIELD/STRUCT_END`, `AQUA_FUNCTION/PARAMETER (or NO_PARAMETERS)/CALL/ARGUMENT/
  RETURN` and, with the java binding, `AQUA_JAVA_STRUCT/AQUA_JAVA_FUNCTION`: a C definition needs its parameter list,
  so each function is a sequence of rows that exports.c and jni.c expand in passes; types are tokens (`DOUBLE`,
  `INT_ARRAY`, `STRUCT(point)`, `VOID`).
- Window-dependent libraries are marked by their own manifest (`[native] window = true`: graphics, imgui, input,
  interaction, opengl, remotewindow, webframe) and rejected; every other default library links in. External libraries'
  native parts are not included yet. Windows cannot be opened (`no_window.c`).
- A library's linear memory uses malloc and explicit bounds checks (`WASM_RT_USE_MMAP=0`,
  `WASM_RT_MEMCHECK_BOUNDS_CHECK=1`): a library installs no signal handlers in its host process.
- Windows has no object kind (lld cannot prelink COFF objects); its static archive renames every internal symbol to
  `<prefix>aqua_<name>` (`embed-archive.cmake`). Linux static and object outputs are one `ld.lld -r` object with
  internals made local (Android static likewise; Android has no object kind).
- C structs and functions share one namespace (`<prefix><snake_case>`), so the collision rule covers both; exported
  struct fields need ASCII names; non-ASCII parameter names become `arg<N>` in bindings (keywords get a `_`).
- The last error is NULL after a successful call; a reentrant call's refusal does not replace the running call's own
  outcome. Reentrancy is tested on POSIX only (a timer signal calls the library during a call); Windows has no way to
  interrupt a thread with a call, and the check is the same C code.
- Consumers print the lines of `geometry.expected` they can cause: `c-abi` lines (invalid UTF-8, a NULL array with a
  count) only C, C++ and Rust (through `ffi`); `native` lines every native consumer; `aqua run` neither.
- The web's `shutdown()` returns a Promise: a page holds one WebAssembly instance, so starting again links a fresh one.
  The web parses the export table in C (`runtime/embed/table.c`), and its host side module carries the embed runtime.
- The Java binding's nullability annotations are its own nested `NonNull`/`Nullable` (no annotation library is a
  dependency), so Kotlin sees platform types; its exception is the nested `AquariusException`. Android libraries write
  their output to logcat and to standard output.
- On Linux, LeakSanitizer ignores allocations of the prebuilt ONNX Runtime (`tests/exports/lsan.supp`): it keeps 14
  bytes from its environment setup until the process exits; nothing of AquariusLang's own is suppressed.
- On Windows, the pinned Rust's host tools are windows-gnu (no gnullvm host release exists), building for
  `x86_64-pc-windows-gnullvm` with that target's standard library through the pinned clang, statically.
- Clang without `#embed` (the Android NDK's) includes the application module with the assembler's `.incbin`.
- `AquariusBottle` references `AquariusBuild` so the manifest can check `[export.<platform>]` against the descriptors.
- `licenses/` holds AquariusLang's license, a THIRD-PARTY notice (WABT's wasm2c runtime, and on Windows the
  statically linked MinGW-w64 and compiler-rt pieces; the pinned WABT archive ships no license file) and the linked
  libraries' license folders.
- `scripts/test-exports.ps1` tests the host's own desktop target (Windows on Windows; Linux on Linux or in WSL), the
  web, and with `-Android` the connected device, since desktop outputs build only on their own host.
