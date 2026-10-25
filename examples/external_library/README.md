# External library example / 外部函式庫範例

`sparkle/` is an external library (a Bottle package, `manifest_version = 2`) with both parts a library can have:

- a **native part** in C (`native/`, built with the SDK's `aqua_add_library`): the module `SparkleNative` / `閃耀原生`
  with `checksum` / `指紋` (FNV-1a of UTF-8 text) and `repeat` / `重複`;
- an **Aquarius-source module** (`aqua/Sparkle.aqua`), imported as `Sparkle` / `閃耀`, built on the native module.

`app/` is an application that depends on it. See [libraries and external libraries](../../docs/external-libraries.md).

```powershell
# 1. Build the native part with the pinned toolchain (scripts/install-toolchain.ps1), CMake and Ninja.
#    Binaries land in sparkle/native/<target>/ (aqua_sparkle.dll, libaqua_sparkle.so, sparkle.wasm).
aqua library build sparkle --targets windows-x64,web,android-arm64-v8a,android-x86_64
# On Linux or WSL: aqua library build sparkle --targets linux-x64

# 2. The library's own tests run as plain programs that import it.
aqua run sparkle/tests/basic.aqua            # prints sparkle/tests/basic.expected

# 3. The application: run it, or export it with the library's binaries beside it.
aqua run app
aqua build app --target windows -o dist/sparkle.exe     # dist/libraries/aqua_sparkle.dll
aqua build app --target web -o dist/web                 # dist/web/runtime/libraries/sparkle.wasm
aqua build app --target android -o dist/sparkle.apk     # lib/<abi>/libaqua_sparkle.so
```

Expected output of the application:

```text
✦ 星泉星泉 ✦
fingerprint: cb1b3689
SPARKLE_APP_OK
```

外部函式庫與預設函式庫的資料夾形狀相同：`aqua.toml`（`[library]`、`[native]`、`[[modules]]`）、C 原生部分與星泉模組。
`aqua library build` 以固定版本的工具鏈建置各平台的二進位檔；安裝與匯出時隨應用程式一起出貨。
