# 星泉（AquariusLang）

<p><strong>繁體中文</strong> | <a href="README.en.md">English</a></p>

史上第一個**繁體中文 + 英文雙語編譯式程式語言**。
可使用全繁體中文、全英文語法，或在同一份 `.aqua` 檔案中自由混用；不需切換模式或設定。
原始碼編譯後產生標準 **WebAssembly（`.wasm`）**，由同一個以 C 撰寫的原生執行環境執行：Windows、Linux 與 Android
使用 **Wasmtime**，網頁使用瀏覽器內建的 WebAssembly 引擎；同一份編譯成果可輸出網站、獨立執行檔與 APK。
C# 只作為編譯器，執行時不需要 .NET。

作者：林天牧 / Temple Lin

- **統一入口**：`aqua` 提供編譯、執行、平台輸出與互動模式（REPL）。
- **雙語語法**：`變數`／`var`、`函式`／`function`、`回傳`／`return` 等關鍵字完全等價，中文與英文腳本可互相匯入。
- **可攜編譯成果**：將模組、圖片與著色器等資源一起封裝，執行與平台輸出不需原始碼。
- **專案與第三方套件**：[Bottle / TOML 專案管理](compiler/AquariusBottle/README.md)提供 `aqua new`、`aqua bottle install/restore/uninstall`、本機／GitHub 安裝、版本鎖定、全域快取與相依資產封裝；見 [可執行範例](examples/bottle_showcase/README.md)。
- **外部函式庫**：以星泉、C、C++ 或 Rust 撰寫，用[函式庫 SDK](runtime/sdk/README.md) 從同一份原始碼建置到所有平台，與預設函式庫完全相同；見[外部函式庫](docs/external-libraries.md)。
- **多執行緒**：[Thread／執行緒](docs/threading.md)以函式啟動獨立工作，提供逾時等待、結果、取消與安全關閉，支援桌面、網頁及 Android；見[雙語範例](examples/threading/README.md)。
- **跨平台網路**：[Network／網路](libraries/network/docs/networking.md)提供雙語 HTTP(S)、Cookie、可取消請求、串流回應、WebSocket、原生 TCP／TLS／UDP，以及 Windows、Linux、Android 的 HTTP(S) 伺服器（路由、靜態檔案、SSE、工作階段）；見[繁體中文前後端範例](examples/network_lab/README.md)。
- **圖學**：[WebGPU、Processing 繪圖、文字輸入與 GPU 數學](libraries/graphics/README.md)使用 wgpu-native 或瀏覽器的 WebGPU；桌面另有 [OpenGL](libraries/opengl/README.md)。
- **PBR 與模型匯入**：[Models／PBR](libraries/graphics/docs/pbr-models.md)在 Wasm 解析 OBJ／MTL，並以共用 WGSL 提供金屬度／粗糙度即時渲染；另有[具名 shader 紋理、cube mip chains 與線性浮點 render targets](libraries/graphics/docs/wgpu-textures.md)。
- **跨平台音訊**：[音訊函式庫](libraries/audio/README.md)以 miniaudio 解碼與混音，支援音檔、音調、循環、聲像、音高及無硬體的離線樣本輸出。
- **模型推論**：[ONNX Runtime 與 MediaPipe 姿態追蹤](libraries/onnx/README.md)依平台排序執行提供者，可選用 CUDA／TensorRT（於 `aqua.toml` 設定 `[onnx] cuda = true`），網頁使用以 C++ 原始碼建置的 ONNX Runtime；附 [繁體中文姿態範例](examples/mediapipe/README.md)。

## 目錄

- [快速開始](#快速開始)
- [正式版編譯器：一鍵建置與打包](#正式版編譯器一鍵建置與打包)
- [編譯、執行與平台輸出](#編譯執行與平台輸出)
- [語法](#語法)
- [函式庫與範例](#函式庫與範例)
- [開發與測試](#開發與測試)
- [專案結構](#專案結構)
- [編輯器與延伸文件](#編輯器與延伸文件)
- [名稱與授權](#名稱與授權)

## 快速開始

使用下方批次腳本產生正式版，或從 [Releases](https://github.com/Aquarius-Language/AquariusLangTW/releases)
取得發佈套件。完整解壓縮後，在含有 **`aqua.exe`** 的 `aqua` 資料夾開啟 PowerShell：

```powershell
# 查看指令
.\aqua.exe --help

# 編譯隨附範例，再執行
.\aqua.exe build .\examples\increment.aqua -o app.wasm
.\aqua.exe run .\app.wasm

# 進入互動模式
.\aqua.exe repl
```

在 `>>` 提示符號後輸入 `印出("你好，星泉！");`，按 Enter 執行；按 Ctrl+C 離開。路徑含空白時請加上雙引號。
編譯器套件為自包含，使用者不需安裝 .NET。請保留整個資料夾：`runtime/<平台>/` 是建置應用程式所用的執行環境，
`web-glue/` 是網頁膠合程式，`libraries/` 是預設函式庫的清單與星泉原始碼，`sdk/` 是函式庫 SDK，`examples/` 是範例。

## 正式版編譯器：一鍵建置與打包

`build-release.ps1` 產生本機平台的編譯器套件與 VS Code 擴充功能（含語言伺服器）。目前在 **Windows x64** 上驗證；腳本以
PowerShell 7 的跨平台寫法撰寫，日後可在 Linux／macOS 以 `pwsh` 執行。請準備：

| 工具 | 用途 |
| --- | --- |
| PowerShell 7（或 Windows PowerShell 5.1） | 執行腳本。 |
| .NET 8 SDK | 建置、測試與發佈編譯器與語言伺服器。 |
| Node.js 20 以上與 npm | 打包 VS Code 擴充功能；以 Chromium 執行網頁測試（在 `scripts/` 執行 `npm install`）。 |
| Android SDK（`ANDROID_HOME`）與 NDK 28.2 | 僅建置 Android 執行環境時需要。 |

鎖定版本的工具鏈（LLVM Clang、llvm-mingw sysroot、WASI sysroot、CMake、Ninja、Wasmtime 與 JDK，不使用 MSVC）由腳本
自動檢查：`.native-build` 缺少的部分會依 `scripts/toolchain.json` 下載並驗證 SHA-256，無法下載時會說明原因後停止。
也可事先執行 `scripts/install-toolchain.ps1 -Targets windows-x64,android-arm64-v8a,android-x86_64`。

```powershell
.\build-release.ps1
.\build-release.ps1 -Help
.\build-release.ps1 -Targets windows-x64,web         # 只建置部分執行環境；在 Windows 加入 linux-x64 會在 WSL 建置
.\build-release.ps1 -SkipRuntime                     # 沿用 .native-build/install 中的執行環境
```

腳本依序建置執行環境（`scripts/build-runtime.ps1`）、建置並測試編譯器、發佈 `dist/aqua/`（`scripts/publish-cli.ps1`），
在沒有安裝 .NET 的條件下驗證 `aqua.exe`（說明、編譯與執行、網站與獨立執行檔輸出），再以 `editors/vscode` 的腳本發佈
語言伺服器並打包 VS Code 擴充功能（確認擴充功能內含伺服器，且伺服器能回應 LSP 初始化），最後打包：

```text
dist/releases/AquariusCompiler-win-x64-<建置識別>.zip（Linux／macOS 為 .tar.gz）
dist/releases/AquariusCompiler-win-x64-<建置識別>.zip.sha256
dist/releases/aquariuslang-tw-<版本>-<建置識別>.vsix
dist/releases/aquariuslang-tw-<版本>-<建置識別>.vsix.sha256
```

可用 `Get-FileHash "套件.zip" -Algorithm SHA256` 比對校驗值。任一步驟失敗會回傳非零結束碼。
Linux 見 [Linux 桌面指南](docs/linux.md)（`scripts/publish-cli.sh`），Android 見 [Android 平台](docs/android.md)。

## 編譯、執行與平台輸出

以下範例在自己的星泉專案目錄執行，假設已將 `aqua.exe` 所在資料夾加入 PATH：

```powershell
# 編譯入口、所有匯入模組與資源，產生單一標準 WebAssembly 檔案
aqua build main.aqua lib\tools.aqua --root . --assets assets --entry main.aqua -o app.wasm

# 執行，不需原始碼
aqua run app.wasm

# 直接由原始碼輸出靜態網站或獨立 Windows x64 執行檔
aqua build main.aqua lib\tools.aqua --root . --assets assets --target web -o dist\web
aqua build main.aqua lib\tools.aqua --root . --assets assets --target windows -o dist\app.exe

# 也可將既有 WebAssembly 編譯成果輸出到不同平台
aqua build app.wasm --target web -o dist\web
aqua build app.wasm --target linux -o dist\app
aqua build app.wasm --target android -o dist\app.apk
```

| 選項 | 說明 |
| --- | --- |
| `--root` | 設定原始碼根目錄，模組與資源保留相對路徑。 |
| `--assets` | 加入檔案或資料夾，可重複指定。 |
| `--entry` | 選擇編譯成果中的 `.aqua` 入口模組，預設為第一個輸入檔。 |
| `-o` | 指定 `.wasm`／`.exe`／`.apk` 檔名或網頁輸出資料夾。 |
| `--target wasm` | 預設目標；產生包含程式、模組與資源的標準 WebAssembly。 |
| `--target web` | 靜態網站：`program.wasm`、網頁執行環境、膠合程式與資源，須搭配 `-o`。 |
| `--target windows`、`--target linux` | 獨立執行檔：附加應用程式的執行環境，須搭配 `-o`。 |
| `--target android` | 支援 arm64-v8a 與 x86_64 的已簽章 APK；參見 [Android 平台](docs/android.md)。 |

必須列出所有匯入的腳本，包括動態匯入；資源須位於根目錄內。模組名稱保留 `.aqua` 相對路徑。
編譯成果的 `aquarius.application` 自訂區段以二進位格式記錄 ABI 4 資訊（模組、資源、匯入的函式庫模組與需要的原生函式庫），
資源位於 `aquarius.assets` 區段；輸出的應用程式只附帶用到的執行環境檔案與函式庫。
編譯與平台輸出不執行程式，失敗時保留既有輸出。

請透過 localhost 或 HTTPS 提供網站；圖學需要瀏覽器可用的 WebGPU adapter。平台不支援的功能會回報不支援。
完整指令見 [統一 CLI](compiler/AquariusCli/README.md)、[編譯成果格式](compiler/AquariusCompiler/README.md)、
[部署後端](compiler/AquariusBuild/README.md) 及 [網頁膠合程式](web-glue/README.md)。

`aqua` 結束碼：**0** 成功、**1** 編譯／執行／檔案錯誤、**2** 指令或選項錯誤。

## 語法

兩種語法使用相同的編譯器、作用域與執行語意，關鍵字可逐一選用繁體中文或英文：

| 繁體中文 | English | 用途 |
| --- | --- | --- |
| `變數` | `var` | 宣告可變變數 |
| `函式` | `function` | 函式運算式與閉包 |
| `回傳` | `return` | 回傳函式結果 |
| `如果` | `if` | 條件分支 |
| `否則如果` | `else if` 或 `elif` | 後續條件分支 |
| `否則` | `else` | 預設分支 |
| `迴圈` | `for` | 迴圈 |
| `中斷` | `break` | 離開迴圈 |
| `真` / `假` | `true` / `false` | 布林值 |

繁體中文：

```aquarius
變數 加法 = 函式(甲, 乙) { 回傳 甲 + 乙; };
印出(加法(3, 5));
```

English：

```aquarius
var add = function(a, b) { return a + b; };
print(add(3, 5));
```

同檔混用，也可讓同一個函式擁有雙語名稱：

```aquarius
var 加法, add = function(甲, b) { 回傳 甲 + b; };
變數 計數 = 0;

for (變數 索引 = 0; 索引 < 5; 索引++) {
    計數++;
    if (計數 == 3) { 中斷; }
}
印出(計數, 加法(3, 5), add(3, 5));
```

分支也能混用：`if (假) { ... } else 如果 (true) { ... } 否則 { ... }`。
英文關鍵字區分大小寫，以上中英文關鍵字都是保留字；例如 `returnValue`、`如果成立` 仍可作為識別字。
函式以 `var add = function(...) { ... };` 宣告；`var` 沿用星泉既有的詞法作用域與可變變數語意。
`let`、`fn` 不是語法關鍵字。字串、註解與自訂名稱保留原文；布林值的預設顯示仍為 `真`／`假`。

運算子包含 `+ - * / %`（餘數，整數向零截斷）及對應的複合指派 `+= -= *= /= %=`。
整數為 32 位元：`+ - *` 溢位時環繞（與 C#、Java 相同），整數 `/` 與 `%` 除以零是執行錯誤；
所有平台的結果都完全一致。

### 選用型別

變數、參數與回傳值可以在名稱後加上 `: 型別`，兩種語法通用；不加型別的程式照舊執行：

```aquarius
變數 次數: 整數 = 0;
變數 費氏 = 函式(n: 整數): 整數 {
    如果 (n < 2) { 回傳 n; }
    回傳 費氏(n - 1) + 費氏(n - 2);
};
var ratio = function(a: int, b: int): double { return double(a) / b; };
var flags: bool[] = Array.Make(10, true);
```

| 繁體中文 | English | 型別 |
| --- | --- | --- |
| `整數` | `int` | 32 位元整數 |
| `浮點數` | `float` | 單精度浮點數 |
| `雙精度` | `double` | 倍精度浮點數 |
| `布林` | `bool` | 真／假 |
| `字串` | `string` | 字串 |
| `整數[]` 等 | `int[]`、`float[]`、`double[]`、`bool[]` | 元素型別固定的陣列 |
| `陣列` | `array` | 元素可為任何值的陣列（例如巢狀陣列） |
| 結構名稱 | struct name | 以 `結構`／`struct` 宣告的[結構](docs/structs.md) |

`結構 節點 { 左: 節點; 右: 節點; 值: 整數 }` 宣告結構型別，`節點(左: 甲, 值: 3)` 或 `節點(甲, 乙, 3)` 建立值（省略的欄位為
`null`），`樹.值` 讀寫欄位。型別名稱只在 `:` 之後有意義，不是保留字。型別不符時，能在編譯時判斷的直接回報錯誤，其餘在執行時檢查；
整數可自動轉成浮點數與倍精度，反方向需用 `整數(x)`／`int(x)`（向零截斷，超出範圍時取最接近的整數值）、
`浮點數(x)`／`float(x)`、`雙精度(x)`／`double(x)` 明確轉換。

核心內建 [`陣列`、`雜湊`與`字串`函式庫](docs/hash-and-string.md)：例如 `雜湊.鍵列表(表)` 列舉雜湊表的鍵、
`字串.轉文字(98.5d)` 把數值轉成文字、`字串.分割("甲,乙", ",")` 分割字串，所有平台都在同一份 Wasm 執行。

變數名稱可使用 Unicode 字母、底線，以及後續的數字與結合標記，例如 `計數1`、`中文_變數`。
支援全域／區域變數、陣列、雜湊表、閉包、遞迴與模組；數值型別包含整數、
單精度（`f` 後綴）與倍精度（`d` 後綴）。`++值` 回傳新值，`值++` 回傳原值，皆保留原數值型別。

雙名稱宣告讓中文與英文名稱共用同一個函式與閉包，也可只宣告其中一個名稱。
`印出`／`print`、`長度`／`len`、`匯入`／`import` 等內建函式同樣提供雙語別名。
模組名稱、常數、屬性與事件字串依各函式庫定義使用。

核心內建 `目前時間()`／`currentTime()`，桌面與瀏覽器皆不需匯入函式庫即可使用。
回傳單調時鐘的倍精度毫秒數，保留小數；兩次讀值相減就是經過的毫秒數：

```aquarius
變數 開始 = 目前時間();
# 在這裡執行要計時的程式
印出("耗時（毫秒）：", 目前時間() - 開始);
```

除以 `1000.0d` 可換算成秒。時鐘起點由執行平台決定，不是日期或 Unix 時間戳記。
`型別(值)`／`typeOf(value)` 回傳 `integer`、`double`、`string`、`array`、`hash`、`null` 等型別標記，
方便處理 JSON 等動態資料，例如 `如果 (型別(資料["錯誤"]) == "hash") { ... }`。
詳細語意見 [語言核心指南](compiler/AquariusCompiler/LANGUAGE.md)，名稱對照見
[函式庫雙語名稱](docs/external-libraries.md)。

## 函式庫與範例

以 `變數 繪圖 = 匯入("Processing");` 匯入函式庫。範例統一位於 `examples/`；正式套件會一併發佈這些範例。
每個預設函式庫是 `libraries/` 下的一個資料夾，各附說明文件：

| 函式庫 | 模組 | 範例 |
| --- | --- | --- |
| [graphics](libraries/graphics/README.md) | Processing、WGPU、GLM、TextInput、Window、Fonts、Models、PBR、GPUMath | [六面板展示](examples/processing_showcase/README.md)、[GPU 計算](examples/wgpu_compute/README.md)、[離屏三角形](examples/wgpu_triangle/README.md)、[多語輸入](examples/multilingual_input/README.md) |
| [opengl](libraries/opengl/README.md) | GLFW、GLAD、GL、STBImage（桌面） | [旋轉材質立方體](examples/opengl_cube/README.md) |
| [application](libraries/application/README.md) | Files、Paths、Images、Serialization、Storage、Clipboard、Application、Tasks、TextEdit | [應用程式函式庫](examples/application_libraries/main.aqua) |
| [audio](libraries/audio/README.md) | Audio | |
| [camera](libraries/camera/README.md) | 攝影機／Webcam、相機／Camera | [攝影機](examples/webcam/README.md) |
| [network](libraries/network/README.md) | 網路／Network | [網路實驗室](examples/network_lab/README.md) |
| [onnx](libraries/onnx/README.md) | 推論引擎／ONNX、姿態追蹤／MediaPipe | [姿態](examples/mediapipe/README.md) |
| [terminal](libraries/terminal/README.md) | 終端機／Terminal | |
| [input](libraries/input/README.md) | Android輸入／AndroidInput | |

語言本身內建可攜的[陣列](docs/arrays.md)、[雜湊與字串](docs/hash-and-string.md)及[執行緒](docs/threading.md)模組。
[星艦遠征](examples/starship_expedition/README.md)與[雙語函式庫](examples/bilingual_library/README.md)示範語言功能。
Jolt 物理由獨立的 `Aqua_JoltPhysics` Bottle 函式庫提供。

## 開發與測試

在專案根目錄：

```powershell
scripts/install-toolchain.ps1 -Targets windows-x64                     # 鎖定版本的 LLVM、MinGW sysroot、Wasmtime
scripts/build-runtime.ps1 -Targets windows-x64,web                     # 執行環境建置到 .native-build/install
dotnet build AquariusLang.sln -c Release
dotnet test compiler/AquariusTests -c Release
scripts/test-libraries.ps1 -Targets windows-x64,web                    # 每個函式庫的測試（加上 -Devices 執行需要裝置的案例）

# 開發時使用統一 CLI
dotnet run --project compiler/AquariusCli -- build examples/increment.aqua -o .native-build/increment.wasm
dotnet run --project compiler/AquariusCli -- run .native-build/increment.wasm
dotnet run --project compiler/AquariusCli -- repl
```

修改專案前請先閱讀 [CLAUDE.md](CLAUDE.md)：其中列出每次修改都必須遵守的架構規則，`dotnet test` 會檢查這些規則。
編譯器測試見 [測試說明](compiler/AquariusTests/README.md)；函式庫測試可在 Windows、Linux（WSL）、網頁（Chromium）
與 Android（`-Targets android-x86_64 -Devices`）執行。另見 [架構說明](ARCHITECTURE.md)、[函式庫 SDK](runtime/sdk/README.md)
與 [網頁膠合程式](web-glue/README.md)。

## 專案結構

| 目錄 | 職責 |
| --- | --- |
| `compiler/` | C#，只在編譯時使用：`AquariusCompiler`（前端、Wasm 產生、封裝）、`AquariusBuild`（平台輸出）、`AquariusBottle`、`AquariusCli`、`AquariusLanguageServer`、`AquariusTests`。 |
| `runtime/` | C，所有平台的執行環境：`guest/`（連結進應用程式的客體執行環境）、`host/`（主機核心）、`platform/<os>/`（很薄的作業系統層）、`sdk/`（函式庫 SDK 與工具鏈檔案）。 |
| `libraries/<名稱>/` | 預設函式庫，每個一個自成一體的資料夾。 |
| `web-glue/` | 唯一的 JavaScript：啟動應用程式並提供網頁 API，每個 API 一個資料夾。 |
| `scripts/` | 工具鏈版本與安裝、執行環境建置、函式庫測試、網頁與 Android 執行、正式版打包。 |
| `examples/`、`tests/` | 範例與共用的語言測試資料。 |
| `editors/vscode`、`website/` | VS Code 擴充套件與介紹網站。 |

## 編輯器與延伸文件

[VS Code 擴充套件](https://marketplace.visualstudio.com/items?itemName=aquariuslang.aquariuslang-tw)
提供語法上色、片段、即時診斷、雙語補全、懸停說明、定義跳轉與符號列表。
安裝、設定、VSIX 打包及執行階段需求見 [VS Code 操作指南](editors/vscode/README.md)。
其他 LSP 編輯器可參考 [語言伺服器說明](compiler/AquariusLanguageServer/README.md)；伺服器分析語法，不執行程式。

[星泉介紹網站](https://aquarius-language.github.io/AquariusLangTW/) 的原始碼與本機操作位於
[website](website/README.md)，GitHub Pages 設定見 [網站部署指南](website/docs/github-pages.md)。

## 名稱與授權

**星泉**結合 Aquarius（水瓶座）的星空與水意象，寓意「靈感如星光閃耀，創意如泉水湧流」。

> 用熟悉的文字，讓靈感湧成作品。

本專案採用 [MIT License](LICENSE)，Copyright (c) 2026 Temple Lin。
第三方相依套件保留各自授權，鎖定在各函式庫的資料夾中，並隨執行環境安裝到 `licenses/`；正式套件會一併附上授權檔。
