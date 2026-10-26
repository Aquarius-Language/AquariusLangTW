# 原生 Android 平台 / Native Android

Android 執行與桌面相同的 C 主機核心與預設函式庫：`libaquarius.so` 以 Wasmtime 48.0.2 執行應用程式的 Wasm，
平台差異只在 `runtime/platform/android`。Java 只有 `AquariusActivity`（`NativeActivity` 子類別），只轉交 Android
僅交給 Java 的事件：activity 結果、權限結果、視窗 insets 與輸入法文字。沒有 WebView、JavaScript 引擎或 Gradle 專案。

Android runs the same C host core and default libraries as the desktop: `libaquarius.so` runs the application's Wasm
on Wasmtime, with OS differences only in `runtime/platform/android`. Its only Java is `AquariusActivity`, a
`NativeActivity` subclass that forwards what Android delivers to Java alone.

## 輸出 APK / Exporting

```powershell
aqua build main.aqua tools.aqua --root . --assets assets -o app.wasm
aqua build app.wasm --target android -o dist/demo.apk --application-id tw.example.demo --app-name "星泉範例"
```

輸出是同時包含 `arm64-v8a` 與 `x86_64` 的已簽章 APK：`lib/<abi>/libaquarius.so` 與執行環境需要的其他共用函式庫、
外部函式庫的 `libaqua_<name>.so`、`classes.dex`、宣告所需函式庫權限（各函式庫 `aqua.toml` 的 `[android] permissions`）
的 manifest，以及 `assets/aquarius/` 下的 `program.wasm` 與資源。預設以 `~/.aquarius/android/debug.keystore` 簽章；
正式發佈請用 `--keystore` / `--key-alias`（密碼來自 `AQUARIUS_KEYSTORE_PASSWORD` / `AQUARIUS_KEY_PASSWORD`）。

建置需要 `ANDROID_HOME`（Android SDK Platform 36、Build Tools 36.0.0）與 JDK（`scripts/install-toolchain.ps1` 安裝鎖定的
Temurin 21）。最低 Android 10（API 29），需要 Vulkan 裝置才能使用圖學。

## 執行環境建置 / Building the runtime packs

```powershell
scripts/install-toolchain.ps1 -Targets android-arm64-v8a,android-x86_64
scripts/build-runtime.ps1 -Targets android-arm64-v8a,android-x86_64   # 需要 ANDROID_HOME 與鎖定的 NDK 28.2
```

C/C++ 以 NDK 的 Clang 編譯，Wasmtime 使用官方預先建置的 C API。

## 函式庫 / Libraries

| 函式庫 | Android |
| --- | --- |
| graphics（Processing、WGPU、GLM、PBR、Models、GPUMath） | wgpu-native over Vulkan；文字使用 `android.graphics` |
| audio | miniaudio（AAudio / OpenSL ES） |
| camera（`Camera` / `相機`） | NDK Camera2；`攝影機` 模組在 Android 回報不支援 |
| input（`AndroidInput` / `Android輸入`） | 觸控、筆壓、insets、方向、鍵盤、震動、返回協商 |
| application | 私有儲存、`content://` 文件、Storage Access Framework 選擇器 |
| network | 與桌面相同的 C 實作（含伺服器與串流）；遵守 app 的明文流量政策 |
| onnx | ONNX Runtime 1.30 Android 套件：WebGPU（Vulkan）、NNAPI、XNNPACK、CPU |
| terminal | `/system/bin/sh` 與直接執行，受 Android 沙箱限制 |
| opengl | 僅桌面 |

應用程式應查詢個別能力（`是否支援`、`Capabilities()`），不要假定每個平台都有印表機、相機或檔案關聯。

## 測試 / Testing

```powershell
scripts/test-libraries.ps1 -Targets android-x86_64 -Devices      # 每個函式庫的 tests/ 在模擬器或裝置上執行
scripts/android-run.ps1 app.wasm -Runtime .native-build/install/android-x86_64 -Run
```

`android-run.ps1` 封裝除錯 APK、安裝、執行並印出 logcat 的 `Aquarius` 輸出。模擬器的 GFXStream Vulkan 驅動會在混合
binary / timeline semaphore 的提交上當機；graphics 函式庫只在模擬器上補齊 timeline 值陣列（見 graphics README）。
