# 原生 Android 平台

`AquariusAndroid` 是獨立的建置後端，透過既有 `IWasmBuildTarget` 註冊於 CLI。
Android 的 Java Activity、Camera2、AudioTrack、文件選擇器及 JNI WebGPU 主機直接使用 Android API。
程式仍在 V8 的 WebAssembly 引擎執行，圖學使用 wgpu-native 的 Vulkan 後端；沒有 WebView。
Android 部署會保留相同的 Wasm 執行區段，將封裝資源外置為 APK assets，避免大型 ONNX 模型在 JSON / base64 間重複佔用 Java 堆積。

## 編譯與輸出

```powershell
# 封裝原始碼與資源，或直接用 --target android 從原始碼輸出
aqua build main.aqua tools.aqua --root . --assets assets -o app.wasm

# 產生可用 Android Studio 開啟的 Java / Gradle 專案
aqua build app.wasm --target android --application-id tw.example.demo --app-name "星泉範例" -o dist/android-project

# 直接產生可安裝的 debug APK
aqua build app.wasm --target android --application-id tw.example.demo -o dist/demo.apk
```

`--entry` 可選擇封裝內的其他入口。專案輸出必須是新目錄或空目錄，避免覆寫手動修改。
APK 輸出使用 Gradle 的 `assembleDebug`。正式發佈時，在產生的專案設定自己的版本號、簽章及 release 建置。
固定 `--application-id` 可以讓不同輸出路徑產生同一個應用程式身分。

APK 建置需 JDK 17 或 21、Android SDK Platform 36、Build Tools 35.0.0，以及網路存取 Maven / Gradle。
設定 `JAVA_HOME` 與 `ANDROID_HOME`。專案附 Gradle 8.13 wrapper；`AQUARIUS_GRADLE` 可指定現有 Gradle。
最低 Android 10（API 29），提供 `arm64-v8a` 與 `x86_64`，需要可用 Vulkan 裝置。
`AQUARIUS_ANDROID_COMPILE_SDK` 可指定已安裝的其他 SDK（例如 `android-37.0`）。

從原始碼製作編譯器時，先執行：

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
./scripts/prepare-android.ps1 -Ndk C:/Android/Sdk/ndk/28.2.13676358
dotnet build AquariusLang.sln -m:1
```

準備腳本驗證 wgpu-native 29.0.1.1 下載雜湊，使用 NDK r28c 編譯 JNI，並準備 Javet 6.0.2 / V8 15.2.124.17 對應的 ICU Unicode 資料。
這些產物不提交 Git。發行的編譯器將它們嵌入，使用者產生專案時不需要 Node、NDK 或原始碼。
若要修改 JNI，可設定 `AQUARIUS_ANDROID_BUILD_NATIVE=1`、`AQUARIUS_ANDROID_NDK`，並安裝 CMake 3.22.1。

## 能力與架構

`AquariusCore/hosting` 擁有共用 Wasm 傳輸、函式庫註冊、Processing、序列化、張量驗證、音訊與 MediaPipe 邏輯。
瀏覽器與 Android 分別注入畫面、輸入、影像、檔案及硬體能力。
Core 不參考 Android、桌面或瀏覽器部署專案；Android 建置專案也不參考桌面或網頁編譯器。
沒有第二套語言解譯器，也不將 Aquarius 控制流程翻譯成 Java。

| 函式庫 | Android 主機 |
|---|---|
| Processing、WGPU、GLM、PBR、Models | 原生 WebGPU / Vulkan、共用 WGSL / Wasm 邏輯 |
| Jolt | 共用 Jolt Wasm |
| Audio | 共用 miniaudio Wasm，原生 AudioTrack 輸出 |
| ONNX、MediaPipe | 原生 ONNX CPU 推論與共用 MediaPipe Wasm；以 `IsSupported` 查詢提供者 |
| Files、Paths、Storage | 封裝資源、私有持久儲存、Android Storage Access Framework 文件與目錄 |
| Images、Serialization | 共用 ImageMagick Wasm、JSON、二進位、壓縮及封裝 |
| Clipboard、Fonts、TextEdit、TextInput | 原生文字與影像剪貼簿、字型量測、Unicode 及 IME 組字 |
| Application、Window、Tasks | 原生文件選擇器、圖片取得、列印、視窗、返回協商及取消權杖 |
| Camera / 相機 | Camera2 行動相機，獨立於 Webcam 合約 |
| AndroidInput / Android輸入 | 觸控、筆壓、歷史樣本、安全邊界、方向、鍵盤、震動、返回事件 |
| Webcam / 網路攝影機 / 攝影機、OpenGL / GLFW、execFile | 平台特定能力；Android 使用 Camera、Processing 與 WGPU |

Android ONNX AAR 使用 CPU，WebGPU 推論能力回報 `false`；圖學與一般 GPU 計算仍使用原生 WebGPU。
應用程式應查詢個別 capability，而不是假定所有主機都有印表機、相機、游標或檔案關聯。
Android 的檔案存取以使用者選取的文件 URI 與私有儲存為邊界。

## 相機與輸入

```aquarius
變數 拍攝工具 = 0;
如果 (是Android()) { 拍攝工具 = 匯入("相機"); }
否則如果 (是網頁()) { 拍攝工具 = 匯入("網路攝影機"); }
否則 { 拍攝工具 = 匯入("網路攝影機"); }

變數 拍攝 = 拍攝工具.開啟("", 640, 480, 30);
變數 影格 = 拍攝.擷取影格();
拍攝.釋放();
```

列出裝置不請求權限；開啟時才請求。影格是獨立、不透明、方向已校正的 RGBA8 快照，保留序號與單調時間戳記。
前鏡頭不自動鏡像推論資料。進入背景、離開應用程式或發生錯誤會關閉相機；恢復後可重新開啟。

```aquarius
如果 (是Android()) {
    變數 輸入 = 匯入("Android輸入");
    變數 狀態 = 輸入.讀取輸入();
    印出(狀態.orientation, 狀態.insets, 狀態.touches);
    輸入.顯示鍵盤(真);
    輸入.震動(30);
    # 返回決策：Accept、Cancel 或 Defer
    輸入.處理返回("Cancel");
}
```

座標使用 density-independent logical units，壓力範圍依裝置回報。輸入每個畫面更新快照；重複讀取不消耗同一畫面的資料。
Processing 同時提供既有滑鼠與文字事件，讓桌面、網頁與 Android 共用響應式畫面。

## 驗證

```powershell
dotnet test AquariusTests -m:1
node --test AquariusAndroid/tests/*.test.mjs
./scripts/test-android.ps1 -BuildApks -Emulator -Adb C:/Android/Sdk/platform-tools/adb.exe
# 另以測試 APK 驗證相機權限、前後鏡頭、觸控、鍵盤及返回協商
./scripts/test-android.ps1 -Emulator -DeviceServices -CaseNames camera,input -Adb C:/Android/Sdk/platform-tools/adb.exe
```

`test-android.ps1` 支援 `-ProjectRoots`，額外驗證範例專案；`-Emulator` 會實際安裝、執行並檢查成功、錯誤及原生崩潰紀錄。
預設案例涵蓋語言、共用能力、Processing、響應式色彩及文字輸入、文件與影像、Jolt、WGPU 繪圖與計算，以及模組與路徑匯入。
APK 冒煙測試也執行 Java 的相機平面、旋轉、模組快取回歸測試；`-DeviceServices` 僅在可供自動化的測試模擬器使用，會授予測試 APK 相機權限並注入輸入事件。
`-CaseNames` 可選擇個別案例。原生 ONNX CPU 的姿態與臉部驗證也可使用 `--entry 姿態驗證.aqua` / `臉部驗證.aqua` 建置執行。
以 Vulkan 啟用的 API 35 系統映像測試 GPU；GFXStream 在模擬器上需要接受不完整 conformance 的測試裝置，實機仍使用標準裝置篩選。
API 37 預覽映像的 GFXStream 驅動在 GPU readback 發生原生崩潰，因此不作為此版本的 GPU 驗證基準。

範例專案的 `建置.ps1 -Android` 產生新專案目錄，`-APK` 直接建置 APK。
Marble、Snake、Painter、Kanban、MediaPipe、PBR 與 Audio 使用視窗寬度重排控制項，行動網頁也套用相同版面。
Processing showcase 以觸控切換六個範例；Color mapping 與多語輸入有獨立行動版面。
Processing / wgpu 在直向螢幕改為上下排列，點擊或空白鍵可暫停及恢復。
`node AquariusWebCompiler/tests/mobile-smoke.mjs` 在 412 × 915 與 915 × 412 驗證畫面、可見內容及瀏覽器錯誤，並儲存截圖。
以 `AQUARIUS_MOBILE_PROJECT_ROOT` 指定包含建置後網頁的範例專案目錄，`AQUARIUS_MOBILE_CASES` 可指定逗號分隔的專案名稱。

本次建置、裝置及行動網頁的結果與七個原始專案 APK 路徑，見[驗證紀錄](android-validation.md)。
