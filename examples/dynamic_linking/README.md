# Dynamic linking showcase / 動態連結展示

One bilingual Aquarius program loads a real Windows DLL, Linux SO, Android SO
(arm64-v8a and x86_64), a portable Wasm library on every platform, and an ES module
in the browser. It verifies UTF-8 text, binary calls, symbol lookup and explicit
release. Success ends with `DYNAMIC_LINKING_OK / 動態連結成功`.

在同一份星泉程式中載入各平台的動態函式庫，展示繁體中文／英文介面、UTF-8、
二進位資料、符號查詢及資源釋放。可攜 Wasm 版本也會在每個平台另外載入。

```powershell
# From this project directory. Source builds require .NET 8 SDK, Zig 0.15.1,
# NDK r28c, prepared Aquarius native bridges, and WSL Linux build dependencies.
.\build.ps1

# Reuse already rebuilt Windows/Linux runtime packs:
.\build.ps1 -SkipRuntimePacks

# Also build an installable Android APK (JDK, SDK and Gradle required):
.\build.ps1 -SkipRuntimePacks -Apk

# Only compile the native libraries and Wasm; does not need .NET or runtime packs:
.\build.ps1 -LibrariesOnly
```

`-Repository`, `-Dotnet`, `-Zig` and `-Ndk` accept tool locations. Defaults target
`C:/OfficialProjects/AquariusLangTW` and its existing tool caches. Before the first
full build, run `scripts/prepare-android.ps1 -Ndk <NDK-r28c>` in the language repo.
Set `JAVA_HOME`, `ANDROID_HOME`, `AQUARIUS_GRADLE` and, if necessary,
`AQUARIUS_ANDROID_COMPILE_SDK` when building an APK. For source-only iteration,
`dotnet <repo>/AquariusCli/bin/Debug/net8.0/aqua.dll run dist/showcase.wasm`.
Rebuilding preserves an existing Android project in `dist/android-project-previous-*`
before generating the new project, retaining any Java/Gradle edits.

| Output | Usage |
| --- | --- |
| `dist/showcase.wasm` | `aqua run dist/showcase.wasm` |
| `dist/windows/Showcase.exe` | Run directly on Windows x64 |
| `dist/linux/showcase` | `chmod +x` then run on Linux x64 |
| `dist/web/` | Serve with `python -m http.server 8080 --directory dist/web` |
| `dist/android-project/` | Editable Gradle project for both Android ABIs |
| `dist/android/Showcase.apk` | Installable debug APK when `-Apk` is specified |

The C source and public ABI header produce every native/Wasm library. The JS
module is standalone and only returns static text or transports bytes. Build
scripts and sources stay separate from generated assets. Runtime tests live in
the language repo. See [the complete bilingual ABI guide](../../docs/dynamic-linking.md)
in the source repository, or `C:/OfficialProjects/AquariusLangTW/docs/dynamic-linking.md`
when using the sibling showcase project.

所有原生及 Wasm 函式庫都由 `library.c` 建置。Windows／Linux 獨立程式內含
runtime，執行不需安裝 .NET 或星泉；Android 安裝檔包含兩種 ABI 的 SO。
網頁需透過 HTTP／HTTPS 提供。`-SkipRuntimePacks` 適用於已重建 runtime pack 的環境。
