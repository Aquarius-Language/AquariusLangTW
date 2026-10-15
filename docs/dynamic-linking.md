# Dynamic libraries / 動態函式庫

Core defines `IDynamicLibraryBackend`, `IDynamicLibrary`, buffer ABI **1**, argument
validation, ownership, bilingual registration and editor metadata. Import
`DynamicLibrary` or `動態函式庫`; both names and every method alias share identity.

| Target | Platform key | Formats | Loader |
| --- | --- | --- | --- |
| Windows x64 | `windows` | `.dll`, `.wasm` | .NET NativeLibrary / Wasmtime |
| Linux x64 | `linux` | `.so`, `.wasm` | .NET NativeLibrary / Wasmtime |
| Android arm64-v8a, x86_64 | `android` | `.so`, `.wasm` | JNI dlopen/dlsym/dlclose / V8 WebAssembly |
| Frontend web | `web` | `.js`, `.mjs`, `.wasm` | ES module import / browser WebAssembly |

```text
變數 動態 = 匯入("動態函式庫");
變數 dynamic = 動態;
變數 函式庫 = dynamic.Load({
    "windows":"libraries/windows/showcase.dll",
    "linux":"libraries/linux/libshowcase.so",
    "android":"libshowcase.so",
    "web":"libraries/portable/showcase.wasm"
});
印出(函式庫.呼叫("echo", "你好 / Hello"));
變數 位元組 = 函式庫.Invoke("sum", [10,20,12]);
函式庫.釋放();
```

`Load` accepts a path or a hash of platform paths. A `wasm` key is used only when
the current platform key is absent. A failed selected load raises an error; there
is no silent fallback. Paths use package-root asset names. Desktop also accepts
explicit host paths. Android native paths must be packaged `lib*.so` basenames.
`Platform` / `平台` and `AbiVersion` / `介面版本` describe the active backend.

| English | 繁體中文 | Contract |
| --- | --- | --- |
| `IsSupported(kind)` | `是否支援(格式)` | `native`, `dll`, `so`, `wasm`, `js`, `mjs`; returns format support, not file availability |
| `Load(pathOrPlatforms)` | `載入(路徑或平台表)` | Returns an owned handle with `Path` / `路徑` |
| `handle.HasSymbol(name)` | `具有符號(名稱)` | Reports a callable public export |
| `handle.Invoke(name, bytes)` | `呼叫位元組(名稱, 位元組)` | Copies byte arrays in and out |
| `handle.Call(name, text)` | `呼叫(名稱, 文字)` | Strict UTF-8, including embedded NULs |
| `handle.Dispose()` | `釋放()` | Idempotent; later calls fail |

Each runtime owns at most 32 open handles and closes them when disposed, including
failed application execution. Inputs and outputs are limited to 1 MiB each. Symbol
names are ASCII C identifiers of at most 128 characters; `aqua_` is reserved.
Errors retain the platform engine's diagnostic and carry the bilingual library
name. Native code and JS modules are trusted application code, with the permissions
of their host; this ABI is not a native-code sandbox.

## Authoring a library

Use [the C header](../AquariusCore/linking/aquarius_library.h). Export
`int32_t aqua_library_abi(void)` returning 1, plus functions with this exact C ABI
(cdecl on Windows):

```c
int32_t echo(const uint8_t *input, int32_t input_length,
             uint8_t *output, int32_t output_capacity);
```

The caller supplies distinct buffers and invokes the function **once** with an
output capacity of 1 MiB. Return the bytes written, or a negative error code.
Never overrun or retain pointers; never throw across the boundary. Ordinary
third-party APIs with different signatures need a small ABI wrapper. This release
does not infer arbitrary C signatures, structures, variadics or callbacks.
The platform OS resolves native dependencies; bundle them with the selected
library or install its documented system dependencies. Native opens may share
OS-level module state, even for separate Aquarius handles.

A Wasm library must have no imports and export the same functions plus `memory`,
`aqua_alloc(i32 length)->i32` and `aqua_free(i32 pointer)->void`. Return nonzero,
nonoverlapping valid allocations; the host frees output then input even on errors.
Each load creates an independent Wasm instance. The host copies the result after
the call so memory growth is supported. These are explicit ABI modules, not
Emscripten `dylink.0` binaries, WASI components or arbitrary relocatable objects.

A standalone ES module exports `aqua_library_abi()` returning 1 and
`aqua_create_library()` returning a fresh object with own callable export names.
Functions accept and return `Uint8Array`, synchronously or asynchronously. An
optional `aqua_dispose()` method on the object releases owned resources. Module
code is cached by the browser; disposing releases the factory object, and each
load creates a fresh object. Bundle JS dependencies into the module before export;
relative module imports are not rewritten. Computational implementations should
use Wasm; JS is available for browser integrations and transport.

## Deployment and validation

Pass the library directories to `aqua build --assets`. Compiled `.wasm`
applications carry their library assets, and desktop hosts materialize them before
loading. Web export writes JS assets as `.mjs` with a hashed filename and loads
only declared package assets. Serve the website over localhost or HTTPS with a
JavaScript MIME type. Android export installs assets named
`native/android/{arm64-v8a|x86_64}/libNAME.so` into the matching `jniLibs` folder,
rejecting collisions with the runtime; Android's installer and linker own the
executable code. Match ABI, minimum SDK and native dependency architecture.

The [showcase](../examples/dynamic_linking/README.md) builds real DLL/ELF/Android
libraries from one C source and one import-free Wasm module, and demonstrates JS
loading in the browser. Rebuild Android's bridge with `scripts/prepare-android.ps1`,
and both desktop runtime packs before exporting standalone applications. Rebuild
the CLI after preparing native assets so its embedded Android template is current.

Tests: `dotnet test AquariusTests --filter FullyQualifiedName~DynamicLibrary` and
`node --test AquariusWebCompiler/tests/dynamic-library.test.mjs`. Set
`AQUARIUS_DYNAMIC_LIBRARY_NATIVE` to a freshly compiled showcase DLL/SO to run
native and compiled Aquarius integration checks. Regenerate the checked-in Wasm
fixture with `examples/dynamic_linking/build.ps1 -LibrariesOnly`, then copy
`libraries/portable/showcase.wasm` to `AquariusTests/linking/fixtures/showcase.wasm`.
The browser integration script also executes the exported application and checks
both JS and Wasm load/dispose behavior.

## 繁體中文說明

核心提供統一的動態函式庫契約與雙語介面；Windows 使用 DLL，Linux 與 Android
使用各自架構的 SO，前端網頁支援 JavaScript 模組及 WebAssembly。相同的星泉程式
以平台路徑表選擇檔案，或以 `wasm` 欄位提供可攜版本。選定的函式庫載入失敗時會
明確回報錯誤，不會自動改用另一個實作。

原生函式庫須遵循第一版 C 緩衝區 ABI；既有第三方 DLL／SO 若使用其他函式簽章，
需加上薄包裝。每次呼叫的輸入與輸出各限一 MiB，函式回傳輸出長度或負值錯誤碼。
`呼叫` 使用嚴格 UTF-8；`呼叫位元組` 可處理任意二進位資料。函式庫不得保留指標或
寫出緩衝區，原生程式具有主機權限，必須由應用程式開發者信任。

Wasm 函式庫不得匯入主機函式，並須提供版本、記憶體、配置、釋放及呼叫函式；每次
載入建立獨立實例。JavaScript 模組須提供版本函式與建立新物件的工廠函式，呼叫可
同步或非同步，並可提供 `aqua_dispose` 清理資源。瀏覽器會快取模組程式碼；釋放
指的是關閉此工廠物件，後續呼叫將失敗。計算邏輯應放在 Wasm 中。

建置時使用 `--assets` 包含函式庫。Android 原生檔案放在
`native/android/arm64-v8a/` 與 `native/android/x86_64/`，載入時只用 `libNAME.so`
檔名。匯出器將檔案放進 APK 的對應原生目錄，不會從可寫入的資料目錄執行程式碼。
桌面獨立執行檔須先重建 runtime pack；Android 須先重建 JNI 橋接及 CLI。
