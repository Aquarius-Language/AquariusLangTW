# Aquarius Bottle

Bottle provides optional TOML projects and local/GitHub third-party libraries for
the `aqua` CLI. It is a portable .NET library shared by the CLI and LSP, without a
registry service or a runtime dependency on .NET in generated applications.
TOML 1.0 is parsed with the pinned [Tomlyn](https://github.com/xoofx/Tomlyn) parser.
The old `.bottle` application archive format is unrelated to this package manager;
applications continue to compile into standard `.wasm`.

## Getting started

```powershell
aqua new MyApp
aqua new MyLibrary --type library
aqua bottle install ../MyLibrary --project MyApp --version ">= 0.1, < 0.2"
aqua build MyApp
aqua run MyApp/dist/MyApp.wasm

# Add a manifest to an existing directory; existing main.aqua is preserved.
aqua bottle init --name MyApp --project .
aqua bottle init --name MyLibrary --type library --project ./library

# Local sources may be folders or manifest files, relative or absolute.
aqua bottle install C:/Libraries/MyLibrary/library.toml --project ./app

# Git must be installed. Only HTTPS GitHub repository URLs are supported.
aqua bottle install https://github.com/OWNER/REPOSITORY.git --rev v0.18.0 --version "> 0.17, <= 0.21" --project ./app
# Add --subdirectory libraries/math when the manifest is below the repository root.
```

Open [the runnable example](../examples/bottle_showcase/README.md) for nested local
dependencies, text resources and a real dynamically loaded Wasm library.

## Manifest version 1

The conventional root filename is `aqua.toml`. An explicit `.toml` path can use a
different filename. Directory inputs and source-file discovery use `aqua.toml`.

```toml
manifest_version = 1

[project]
name = "MyApp"
type = "executable" # or "library"
version = "1.0.0"   # mandatory for libraries, optional for executables
entry = "src/main.aqua"
sources = ["src"]  # files and/or directories, relative to this manifest
platforms = ["windows", "linux", "web", "android"]
description = "An example application"
license = "MIT"

[dependencies]
# Every dependency needs an explicit version requirement.
greeting = { path = "libraries/greeting/aqua.toml", version = "> 0.17, <= 0.21" }
math = { git = "https://github.com/OWNER/REPOSITORY.git", rev = "v1.0.0", subdirectory = "math", version = "1.0.0" }
# A version string resolves an already cached library by name.
helpers = ">= 1.2.0, < 2.0.0"

[[assets]]
source = "assets" # entire directory, including subdirectories

[[assets]]
source = "native/windows/helper.dll"
target = "native/helper.dll" # explicit application-root destination
platforms = ["windows"]

[tool.my_tool]
enabled = true
```

Library manifests use the same format, including `[dependencies]` and `[[assets]]`.
Project names are at most 100 characters, begin with a Unicode letter or digit,
and contain only letters, digits, `_` and `-`. Whitespace and portable reserved
device names are rejected by both the compiler and LSP. Dependency names are case
insensitive and must match the source library's declared name. Self dependencies,
duplicate names and unknown manifest keys are errors. `[tool.*]` is reserved for
other tools and does not affect compilation.

Library versions require `major.minor.patch`, with optional SemVer prerelease and
build metadata. Supported requirement operators are `=`, `==`, `>`, `>=`, `<`,
`<=`; a bare version means equality. Commas mean **AND**. One- and two-component
versions in requirements expand with zeros: `0.21` means `0.21.0`. Prereleases are
excluded unless a clause explicitly refers to a prerelease of that core version.
Wildcard, caret, tilde and OR requirements are rejected rather than guessed.

Libraries must declare at least one supported platform: `web`, `android`,
`windows`, `linux`, `macos`, `ios`, `freebsd`. Executables may omit the list.
These are compatibility declarations; currently available packaging backends
remain `web`, `windows`, `linux`, `android`. A concrete build target must be
supported by the executable and all dependencies. Generic `wasm` builds require
every declared executable platform to be supported by all dependencies, and at
least one common platform overall. Compatibility survives intermediate `.wasm`
files, so a later export cannot bypass the check.

`entry` defaults to `main.aqua`. `sources` defaults to `["."]`; recursive discovery
skips nested `aqua.toml` projects and generated/version-control directories.
Explicit source lists are recommended. The entry must be among the declared
sources. Paths for sources and assets must stay within the owning manifest root;
dependency `path` may point outside it, including to siblings or absolute paths.
Symlinks and junctions in package inputs are rejected.

## Imports and assets

Installed modules use stable application-root virtual paths:

```aquarius
變數 工具 = 匯入("/bottles/greeting/main.aqua");
印出(工具.message());
```

The path after the library name is its declared source path, including the entry's
`src/` directory if present. English `import` works identically. Library-internal
relative imports continue to work, and transitive libraries use their own
`/bottles/NAME/...` paths. All resolved libraries are available in this namespace.
The application `bottles/` namespace is reserved; dependency modules cannot be
overwritten by application modules/assets. A library's own `entry` is validated
when building or installing; imports still name the actual `.aqua` file explicitly.

Asset `source` is always relative to **that asset's owning manifest**, even for a
library nested several levels deep. Assets with no `target` keep their source
path under `/bottles/NAME/` for a dependency, or under the application root for the
executable. A library can access these using `目前工作目錄` / `currWorkingDir`
relative to its module (for example `currWorkingDir + "/assets/message.txt"` when
its module is in the library root). An explicit `target` places the file/directory
at that application-root path; directories preserve their child structure.

For simple declarations, `assets = ["assets", "models/model.onnx"]` may appear
**before** `[project]` instead of `[[assets]]` tables. Glob syntax is not supported;
declare a directory or individual files. All platforms' declared assets are
cached; `platforms` on an asset filters deployment. Such assets require a concrete
build platform because a generic `.wasm` cannot choose an OS-specific binary.
Use `aqua build . --platform windows -o app.wasm` to select Windows assets and
compatibility metadata without exporting an EXE, or use `--target windows` to
export one. `--platform` applies only to TOML Wasm builds; it cannot override an
export target. Different platform alternatives may use the same destination
provided only one alternative applies to the selected platform. All alternatives
are still cached, so later locked/offline builds can select another platform.
Duplicate destinations, case-only collisions, source/asset overlap, traversal,
unsafe portable filenames, missing files and package size/count overflows fail
the build. Assets cannot be `.aqua` source files.

DLL, SO, JavaScript and dynamic Wasm files use the existing `DynamicLibrary`
runtime ABI. Bottle ships the bytes at their declared destinations; it does not
invent binding interfaces, execute install scripts or modify the OS DLL search
path. Desktop hosts extract application assets together with their relative
structure. Web builds map them through the existing virtual filesystem; Android
bundles them through its existing packaging path.

## Resolution, locks and cache policy

```powershell
aqua bottle restore --project .
aqua bottle restore --project . --locked --offline
aqua bottle update --project .
aqua bottle list --project .
aqua bottle uninstall greeting --project .

aqua bottle install ../MyLibrary --global
aqua bottle list --global
aqua bottle cache path
aqua bottle cache list
aqua bottle cache remove MyLibrary 0.1.0

aqua build . --locked --offline -o dist/app.wasm
aqua build . --target web -o dist/web
```

The per-user global cache defaults to `~/.aquarius/bottles`. Set
`AQUA_BOTTLE_HOME` to relocate it (also useful for CI/tests). Each immutable
`packages/NAME/VERSION` snapshot contains a canonical `aqua.toml`, declared source
and asset files, plus a SHA-256 descriptor recording direct dependency pins and
source provenance. First use caches the complete dependency graph. New versions
coexist with old versions. Reusing a name/version with changed source, assets or
manifest declarations is an error; bump the library version. Compatible transitive
versions are pinned per project, so the same cached library source can serve
projects with different child versions. Files omitted from the
manifest are not copied. Limits are 10,000 files / 256 MiB per snapshot/application,
1 MiB per manifest, and 1,000 libraries / 128 dependency levels per graph.

`aqua.lock.json` records the manifest hash, exact dependency versions, content
hashes, transitive pins and Git commit provenance. Commit it for applications.
Ordinary builds/restores keep matching locks and use cached bytes even when local
library source files have changed or disappeared. `update` deliberately re-reads
sources and refreshes the lock. Cache-only requirements choose the highest matching
cached version; source declarations use the version actually declared there.
One version per library name is allowed in an application. Incompatible graphs
fail with an explicit conflict; there is no registry-backed version discovery or
backtracking solver. Adjust constraints/sources to agree on a shared version.

`--locked` rejects absent/stale locks. A valid checked-in lock can hydrate an empty
cache from local sources or its pinned Git commit, and verifies content hashes.
`--offline` prevents Git/network access and requires sources or cached versions
to be available locally. Missing/corrupt caches fail clearly; to recover, remove
the affected cache version and restore from a trusted source. Cache deletion is
refused while another cached library depends on that version. Projects elsewhere
on disk are not tracked globally; deleting a version still used by a project
requires that project to restore again. Uninstall removes a **direct manifest
dependency**, prunes the project's lock, and preserves shared cached versions.

GitHub sources are cloned into an OS temporary directory without submodules or
install hooks. Only HTTPS GitHub repository URLs are accepted; `--rev` can select
a tag, branch or commit and `--subdirectory` selects a library in a monorepo.
Temporary checkouts are removed after snapshotting. Downloaded manifests' local
path dependencies must remain inside that Git checkout; local user projects may
still reference sibling/absolute library paths. Git failures/timeouts leave the
consuming manifest unchanged. Manifest edits and lock writes use temporary
files and atomic replacement; failed install/uninstall restores the original
manifest and lock. A cache-wide writer lock prevents concurrent cache mutations.
Downloaded code is never executed by restore or build.

## Editor and source-only compatibility

The language server shares this parser and resolver. It diagnoses TOML syntax,
required version/platform declarations, whitespace in names, invalid constraints,
missing sources/assets, unavailable dependencies, conflicts and cache corruption.
It offers manifest key completion and library member completion, hover and
definitions through local sources or locked snapshots. Unsaved manifest edits
refresh project diagnostics; the VS Code client watches manifests, locks and
source files. Editor analysis does not clone, install, execute code or write cache
files. Use the same `AQUA_BOTTLE_HOME` for CLI and editor when overriding the cache.

`aqua build` inside a project discovers the nearest `aqua.toml`; directory and
explicit manifest inputs also work. Building `.aqua` files belonging to a TOML
project validates and builds the manifest project, so invalid names/versions
cannot bypass validation by naming its entry source. Project root, sources, entry
and assets come from TOML; do not combine project builds with `--root`, `--entry`
or `--assets`. Without a manifest, existing explicit source-file builds still work
and have no Bottle dependency resolution or manifest-only features. The legacy
`AquariusDesktop` executable remains a source runner; use unified `aqua` for TOML
projects.
