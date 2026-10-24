# Bottle showcase / 第三方函式庫管理展示

This executable TOML project uses a local third-party library under
`libraries/greeting`. That library declares its own TOML dependency under
`dependencies/numbers`, plus a text asset and an actual dynamically loaded Wasm
library. Both dependency levels and all declared assets enter the per-user Bottle
cache. The application runs from its single `.wasm` after installation even if the
original library directories are unavailable.

```powershell
# From a source checkout or the sibling C:/OfficialProjects showcase:
./build.ps1
./build.ps1 -Target web

# Use the aqua executable from your freshly built/published Aquarius distribution.
aqua bottle restore --project .
aqua bottle list --project .
aqua build . --locked --offline -o dist/showcase.wasm
aqua run dist/showcase.wasm
aqua build . --target web -o dist/web

# Equivalent explicit installation (updates [dependencies] and the lock):
aqua bottle install ./libraries/greeting/aqua.toml --project . --version "> 0.17, <= 0.21"

# Build from the repository without publishing:
# dotnet ../../AquariusCli/bin/Debug/net8.0/aqua.dll build .
```

Expected output contains `20 + 22 = 42`, `BOTTLE_DYNAMIC_LINK_OK` and
`BOTTLE_SHOWCASE_OK`. Serve `dist/web` over HTTP to run in a browser.
Windows/Linux executable packaging uses `--target windows -o dist/showcase.exe`
or `--target linux -o dist/showcase` with the usual installed runtime packs.
Android project generation uses `--target android -o dist/android-project`.

To try offline installation, restore first, temporarily move `libraries` outside
this project, and rebuild with `--locked --offline`. Move it back before editing
or updating a library. Bump the library's declared version when changing published
source or assets; cached versions are immutable. Commit `aqua.lock.json` alongside
`aqua.toml` for applications. The generated `dist` directory can be discarded.

For a GitHub library the equivalent command is:

```powershell
aqua bottle install https://github.com/OWNER/REPOSITORY.git --rev v0.18.0 --version ">= 0.18, < 0.19" --project .
# If its aqua.toml is below the repository root, add --subdirectory path/to/library.
```

`--rev` accepts a release tag, branch or commit. Bottle clones into an operating
system temporary directory, records the exact commit in the lock, caches only
declared files, and removes the checkout. No registry service is needed. The URL
above is illustrative; this showcase uses local sources and makes no GitHub calls.

The included `native/showcase.wasm` is the repository's small dynamic-linking test
fixture, built from the accompanying `library.c` and `aquarius_library.h` (MIT).
Its `echo` export uses Aquarius's documented dynamic library ABI. Native DLL/SO
assets use the same manifest declarations, optionally with `platforms` and `target`
to place files at a specific application path; those builds require an explicit
platform target.

專案與函式庫名稱不可有空白。函式庫必須宣告版本與支援平台；所有相依套件都必須
指定版本條件。文字資產與動態函式庫均以函式庫根目錄為相對路徑宣告，快取與封裝後
仍可由函式庫的 `目前工作目錄` 找到。LSP 支援 TOML 診斷與套件匯入的成員補全。
