# Libraries and external libraries

Everything an AquariusLang program imports by name comes from a **library**: a folder with an `aqua.toml` manifest
(`manifest_version = 2`) and up to two parts.

- A **native part** in C or C++ that registers modules with the runtime through the SDK (`runtime/sdk`,
  `aqua_add_library`, `AQUA_LIBRARY(...)`).
- **Aquarius-source modules**: `.aqua` files the compiler bundles into the applications that import them.

The default libraries live in `libraries/<name>/`; their native parts are linked into every runtime pack. An
**external library** is the same folder shape shipped as a Bottle package: its native part is a prebuilt binary per
target that applications load at startup.

## The manifest

```toml
manifest_version = 2

[library]
name = "sqlite"                         # the library (and Bottle package) name
version = "1.0.0"
description = "SQLite for AquariusLang"
license = "MIT"
platforms = ["windows", "linux", "android", "web"]

[native]                                # optional: the C/C++ part
name = "sqlite"                         # file names below; [a-z0-9_], defaults to the library name
modules = ["SQLiteNative|資料庫原生"]    # every module it registers, exactly as passed to aqua_define_module
source = "native"                       # external libraries: the CMake folder that calls aqua_add_library(sqlite ...)

[[modules]]                             # optional: Aquarius-source modules, one table each
names = "SQLite|資料庫"                  # import names, English first, then Traditional Chinese
source = "aqua/SQLite.aqua"

[android]
permissions = ["android.permission.INTERNET"]   # requested by APKs of applications that need the library

[dependencies]                          # external libraries only: other Bottle libraries
[[assets]]                              # external libraries only: files the library reads at run time
source = "data"
```

`aqua new NAME --type library` creates this shape with one Aquarius-source module. Module names are unique across all
the libraries an application can see.

## How the compiler links libraries

A library is **needed** by an application when

1. the program, or a library module it bundles, imports one of the library's modules by a literal name
   (`匯入("SQLite")`, `import("Audio")`), or
2. the project declares it for imports by computed names: `libraries = ["sqlite"]` in `[project]`.

For every needed library the compiler bundles the imported Aquarius-source modules at
`.aquarius/<library>/<source>` (and the library's `.aqua` files they import by relative path), scans them the same
way, and writes application metadata ABI 4: the import names of the bundled modules and the native libraries the
application needs. Default libraries are listed too (the web loads only the side modules an application lists). The
supported platforms of an application are narrowed to those all its needed libraries support, so building a desktop-only
library's application for the web fails at build time.

## Native parts of external libraries

Prebuilt binaries live in the library folder, one per target:

```text
native/windows-x64/aqua_sqlite.dll
native/linux-x64/libaqua_sqlite.so
native/android-arm64-v8a/libaqua_sqlite.so
native/android-x86_64/libaqua_sqlite.so
native/web/sqlite.wasm                  # a side module sharing the application's memory
```

Build them with the pinned toolchain:

```powershell
aqua library build path/to/sqlite --targets windows-x64,web,android-arm64-v8a,android-x86_64
# on Linux (or in WSL): aqua library build path/to/sqlite --targets linux-x64
```

`aqua library build` configures `[native] source` with `sdk/cmake/toolchains/<target>.cmake` (CMake and Ninja from
`PATH`, or `AQUARIUS_CMAKE` / `AQUARIUS_NINJA`), builds it under `build/<target>/` and copies the binary to
`native/<target>/`. The toolchain is installed by `scripts/install-toolchain.ps1` (or `.sh`); a distribution keeps the
pins in `scripts/toolchain.json` and installs toolchains under `~/.aquarius/native-build` unless
`AQUARIUS_NATIVE_BUILD` says otherwise. The SDK guide is `runtime/sdk/README.md` (`sdk/README.md` in a distribution).

A minimal native part:

```cmake
# native/CMakeLists.txt
cmake_minimum_required(VERSION 3.20)
project(aqua_sqlite C)
include(AquariusLibrary)
aqua_add_library(sqlite SOURCES src/sqlite.c)
```

```c
// native/src/sqlite.c
#include <aqua/aqua.h>
static void version(aqua_call *call) { aqua_return_cstring(call, "3.54.1"); }
AQUA_LIBRARY(sqlite) {
    aqua_class *module = aqua_define_module(library, "SQLiteNative|資料庫原生");
    aqua_define_function(module, "version|版本", version, "");
}
```

A function that completes later calls `aqua_defer` and settles the pending call with `aqua_resolve` / `aqua_reject`;
the calling script waits while other tasks keep running. Registering the same C function a second time with
`aqua_define_async` (named `...Async` / `非同步...`) gives scripts a variant that returns a promise at once, settled
by the same result, for `await` / `等待` and `Promise.All` (see [async functions](async.md)). That needs SDK API version
2 (`AQUA_API_VERSION`).

```c
aqua_define_function(module, "Query|查詢", query, "s");
aqua_define_async(module, "QueryAsync|非同步查詢", query, "s");
```

## Installing and shipping

`aqua bottle install` snapshots a library into the per-user cache with its manifest, Aquarius sources, assets and the
`native/<target>/` binaries. Builds of applications that need it ship the binaries:

| Target | Where the binary goes |
| --- | --- |
| Windows, Linux executables | `libraries/` beside the executable (the runtime also looks beside the runtime) |
| Android APK | `lib/<abi>/libaqua_<name>.so` |
| Website | `runtime/libraries/<name>.wasm` |
| `aqua run` | passed as `aquarius --library <file>` |

Programs inside a library folder (its `tests/*.aqua`) compile as plain programs that can import the library:
`aqua build tests/case.aqua --root tests -o case.wasm` links the library itself, and `aqua run tests/case.aqua` loads
its native binary for the host.

## Version 1 projects

`manifest_version = 1` manifests (`[project]` with `type = "executable"` or `"library"`) still build. Their library
sources are imported by path from `/bottles/NAME/...`, as before. A version 1 library has no native part; the
`DynamicLibrary` / `動態函式庫` module that loaded application-supplied DLLs, ES modules and Wasm files at run time is
gone: a library with native code is now a version 2 library with a `[native]` part.
