# 星泉（AquariusLang）

以 C# 實作的程式語言，支援繁體中文關鍵字、Unicode 變數與函式名稱。
原始碼經詞法分析、語法分析與編譯後，交由堆疊虛擬機（VM）執行；
同一份 `.bottle` 位元碼套件可用於桌面執行與靜態網頁輸出。

作者：林天牧 / Temple Lin

- **統一入口**：`aqua` 提供編譯、桌面執行、網頁輸出與互動模式（REPL）。
- **可攜套件**：將模組、圖片與著色器等資源一起封裝，執行與網頁輸出不需原始碼。
- **雙語函式庫**：腳本與桌面原生函式支援繁體中文／英文名稱，共用函式與閉包。
- **圖學與物理**：桌面提供 wgpu、Processing、OpenGL 與 Jolt；瀏覽器使用 WebGPU 與 Jolt WebAssembly。

## 目錄

- [快速開始](#快速開始)
- [正式版編譯器：一鍵建置與打包](#正式版編譯器一鍵建置與打包)
- [編譯、執行與網頁輸出](#編譯執行與網頁輸出)
- [語法](#語法)
- [函式庫與範例](#函式庫與範例)
- [開發與測試](#開發與測試)
- [專案結構](#專案結構)
- [編輯器與延伸文件](#編輯器與延伸文件)
- [名稱與授權](#名稱與授權)

## 快速開始

使用下方批次腳本產生正式版，或從 [Releases](https://github.com/Aquarius-Language/AquariusLangTW/releases)
取得適用的發佈套件。新版統一 CLI 的執行檔為 **`aqua.exe`**；
完整解壓縮後，在含有該檔案的 `aqua` 資料夾開啟 PowerShell：

```powershell
# 查看指令
.\aqua.exe --help

# 編譯隨附範例，再執行位元碼套件
.\aqua.exe build .\examples\increment.aqua -o app.bottle
.\aqua.exe run .\app.bottle

# 進入互動模式
.\aqua.exe repl
```

在 `>>` 提示符號後輸入 `印出("你好，星泉！");`，按 Enter 執行；按 Ctrl+C 離開。
路徑含空白時請加上雙引號。正式版包含 .NET 執行階段，使用者不需安裝 .NET SDK 或 Runtime；
請保留整個資料夾中的 DLL、設定檔、`runtimes`、`licenses` 與 `examples`。

若下載的是較舊的 `Aqua_VM.zip`，入口仍是 `AquariusDesktopVMREPL.exe`，
請依 [桌面入口說明](AquariusDesktopVMREPL/README.md) 使用其相容指令。

## 正式版編譯器：一鍵建置與打包

### 建置環境

此批次流程產生 **Windows x64、自包含、Release** 編譯器套件，請在 Windows x64 上準備：

| 工具 | 用途 |
| --- | --- |
| Windows PowerShell 5.1 或 PowerShell 7 | 批次檔優先使用 PowerShell 7；找不到時自動使用 Windows 內建的 5.1。 |
| .NET 8 SDK | 建置、測試與發佈所有 `net8.0` 專案；僅安裝 Runtime 無法建置。 |
| Node.js 與 npm | 依 `package-lock.json` 還原瀏覽器相依套件、準備內嵌資源並執行測試。 |
| Python（`python`） | 執行既有的外部程式整合測試。 |
| CMake 3.20 以上、Visual Studio C++ 建置工具及 Windows SDK | 建置 GLFW／OpenGL／文字輸入原生橋接 DLL。 |

批次檔會從 PATH 與常見安裝位置尋找 PowerShell；不需另外安裝 PowerShell 7。
腳本也會尋找使用者目錄中的 `.dotnet` SDK、pyenv 與一般 Python 安裝；其餘工具請加入 PATH。
首次還原 NuGet、npm 與 GLFW 需要網路連線。
若使用 Ninja 等產生器，請先開啟已設定 x64 C++ 編譯器環境的終端機。

### 執行批次腳本

在專案根目錄執行：

```powershell
.\build-release.bat

# 顯示用法
.\build-release.bat -Help

# 自動偵測找不到工具時，可指定完整路徑
.\build-release.bat -Dotnet "C:\Users\你的帳號\.dotnet\dotnet.exe" -CMake "C:\工具\CMake\bin\cmake.exe"

# 指定 Python，避開尚未設定完成的 pyenv shim
.\build-release.bat -Python "C:\工具\Python\python.exe"

# 指定已安裝的 CMake 產生器（名稱須與本機安裝版本一致）
.\build-release.bat -Generator "Visual Studio 17 2022"

# 另外驗證正式 EXE 的 GPU 計算、繪圖、OpenGL 與 Processing
.\build-release.bat -VerifyGraphics
```

腳本會自動定位專案，不受啟動時的工作目錄影響；所有選項也可搭配使用。
未指定 CMake／產生器時，沿用 `native/build.ps1` 的快取偵測與建置規則。
`-VerifyGraphics` 需要可用的 GPU、驅動程式與桌面環境，會短暫開啟視窗。

流程依序完成：

1. 檢查作業系統、SDK 與必要工具。
2. 還原鎖定版本的 npm 套件，準備瀏覽器 VM 所需的程式、WASM 與授權。
3. 建置並安裝 Windows x64 原生圖學橋接。
4. 還原 NuGet，建置 Release 方案，執行 .NET 測試與瀏覽器 VM 單元測試。
5. 發佈 `AquariusCli` 為自包含 `aqua.exe`，加入原生函式庫、範例與授權。
6. 直接啟動正式 EXE，驗證編譯、移除測試原始碼後的執行與網頁輸出，以及原生 Jolt。
7. 建立完整資料夾的 ZIP 與 SHA-256 校驗檔。
8. 將成功結果移至正式輸出目錄，顯示 EXE 與 ZIP 的完整路徑。

預設 .NET 測試略過需主動啟用的 GPU 測試；瀏覽器實機 GPU／互動測試另見下方開發指令。
正式 EXE 的驗證會將 .NET 搜尋路徑指向不存在的目錄，確認自包含執行。

### 輸出與交付

每次建置使用新的時間戳記與識別碼，不覆寫先前成功的套件：

```text
dist/releases/AquariusCompiler-win-x64-<建置識別>/
├─ aqua/                         # 完整可執行套件
│  ├─ aqua.exe                   # 統一編譯器入口
│  ├─ runtimes/                  # 原生圖學橋接
│  ├─ licenses/                  # 第三方授權
│  └─ examples/                  # 隨附範例；另含 DLL、執行階段與設定檔
├─ AquariusCompiler-win-x64-<建置識別>.zip
├─ AquariusCompiler-win-x64-<建置識別>.zip.sha256
├─ reports/                      # .NET TRX 與正式 EXE 驗證結果
└─ build.log                     # 建置紀錄
```

交付 ZIP 與 `.sha256` 檔，接收端完整解壓縮後即可使用 `aqua\aqua.exe`。
可用 `Get-FileHash "套件.zip" -Algorithm SHA256` 比對校驗值。
此流程採資料夾發佈，明確啟用 `UseAppHost`，並關閉單檔打包與 trimming；請保留 EXE 旁的所有檔案。
圖學功能仍需相容的 GPU 驅動與原生執行階段相依套件，交付前應在目標電腦驗證。

任一步驟失敗會回傳非零結束碼。通過工具檢查後，建置紀錄與未完成產物會保留在
`dist/.package-<建置識別>/`，不會加入 `dist/releases/`。修正錯誤後重新執行即可。
此腳本打包編譯器；LSP 與 VSIX 的獨立建置流程見編輯器文件。

## 編譯、執行與網頁輸出

以下範例在自己的星泉專案目錄執行，假設已將 `aqua.exe` 所在資料夾加入 PATH：

```powershell
# 列出入口與所有匯入的腳本，並封裝資源
aqua build main.aqua lib\tools.aqua --root . --assets assets --entry main.aqua -o app.bottle

# 用桌面 VM 執行
aqua run app.bottle

# 從同一套件輸出靜態網站
aqua build app.bottle --target web -o dist\web

# 以 localhost 提供網站，再開啟 http://localhost:8080
python -m http.server 8080 --directory dist\web
```

| 選項 | 說明 |
| --- | --- |
| `--root` | 設定套件根目錄，模組與資源保留相對路徑。 |
| `--assets` | 封裝檔案或資料夾，可重複指定；不將原始碼當作資源封裝。 |
| `--entry` | 編譯時選擇 `.aqua` 入口；執行／網頁輸出時可選擇 `.rius` 入口或其 `.aqua` 別名。 |
| `-o` | 指定 `.bottle` 檔名或網頁輸出資料夾。 |
| `--target web` | 將單一 `.bottle` 輸出為靜態網站，須搭配 `-o`。 |

`build` 預設產生 `.bottle`，第一個原始碼檔案為預設入口。
必須列出所有匯入的腳本，包括動態匯入；資源須位於套件根目錄內。
模組匯入以定義該模組的目錄為基準，不能逃出套件。
新套件使用版本 2 manifest，包含模組與資源清單；版本 1 套件仍可載入。
編譯與網頁輸出不執行程式，失敗時保留既有輸出。

網頁輸出只讀取 `.bottle`，內含 JavaScript VM、虛擬檔案系統與本機副本的瀏覽器相依套件。
請透過 localhost 或 HTTPS 提供網站；圖學需要瀏覽器可用的 WebGPU adapter。
桌面外部程序與尚未實作的瀏覽器 API 無法在網頁中使用，呼叫時會回報錯誤。
完整指令與限制見 [統一 CLI](AquariusCli/README.md)、[套件格式](AquariusPackaging/README.md)
及 [網頁編譯器](AquariusWebCompiler/README.md)。

`aqua` 結束碼：**0** 成功、**1** 編譯／執行／檔案錯誤、**2** 指令或選項錯誤。

## 語法

| 常見關鍵字 | 星泉關鍵字 |
| --- | --- |
| `let` / `fn` | `變數` / `函式` |
| `if` / `elif` / `else` | `如果` / `否則如果` / `否則` |
| `return` | `回傳` |
| `for` / `break` | `迴圈` / `中斷` |
| `true` / `false` | `真` / `假` |

```text
變數 加法, add = 函式(甲, 乙) { 回傳 甲 + 乙; };
變數 計數 = 0;

迴圈 (變數 索引 = 0; 索引 < 5; 索引++) {
    計數++;
    如果 (計數 == 3) { 中斷; }
}
印出(計數, 加法(3, 5), add(3, 5));
```

變數名稱可使用 Unicode 字母、底線，以及後續的數字與結合標記，例如 `計數1`、`中文_變數`。
支援全域／區域變數、陣列、雜湊表、閉包、遞迴與模組；數值型別包含整數、
單精度（`f` 後綴）與倍精度（`d` 後綴）。`++值` 回傳新值，`值++` 回傳原值，皆保留原數值型別。

雙名稱宣告讓中文與英文名稱共用同一個函式與閉包，也可只宣告其中一個名稱。
`印出`／`print`、`長度`／`len`、`匯入`／`import` 等桌面內建函式同樣提供雙語別名。
模組名稱、常數、屬性與事件字串依各函式庫定義使用。
詳細語意見 [語言核心指南](AquariusLangVM/LANGUAGE.md)，名稱對照見
[函式庫雙語名稱](AquariusDesktopVMREPL/LIBRARY_NAMES.md)。

## 函式庫與範例

以 `變數 繪圖 = 匯入("Processing");` 匯入原生函式庫。
範例位於 `examples/` 與 `AquariusDesktopVMREPL/examples/`；桌面專案的範例會隨正式套件發佈。

| 功能 | 說明與文件 | 範例 |
| --- | --- | --- |
| WGPU／wgpu | [WGSL 繪圖、GPU 計算、緩衝區與像素讀回](AquariusDesktopVMREPL/graphics/WGPU.md)；桌面使用 Silk.NET／wgpu-native。 | [計算](examples/wgpu_compute/README.md)、[離屏三角形](examples/wgpu_triangle/README.md) |
| Processing | [wgpu 2D／3D 繪圖](AquariusDesktopVMREPL/graphics/Processing.md)、圖片、文字、動畫與事件、離屏畫布、自訂著色器。 | [六面板展示](examples/processing_showcase/README.md)、[色彩映射介面](examples/color_mapping/README.md) |
| OpenGL | [GLFW、GLAD、OpenGL 3.3 core、GLM 與 STBImage](native/README.md)；完整保留桌面 OpenGL 函式庫。 | [旋轉材質立方體](examples/opengl_cube/README.md) |
| Jolt | [世界、剛體、重力、力與衝量](AquariusDesktopVMREPL/physics/Jolt.md)；桌面為原生引擎，瀏覽器為 WASM。 | [物理驗證](examples/jolt_physics/README.md) |
| 文字輸入 | [Unicode 字素編輯、輸入事件與 Windows IME](AquariusDesktopVMREPL/graphics/TextInput.md)。 | [多語輸入](examples/multilingual_input/README.md) |
| 語言與雙語函式 | 閉包、遞迴、控制流程與中文／英文函式名稱。 | [星艦遠征](examples/starship_expedition/README.md)、[雙語函式庫](examples/bilingual_library/README.md) |

桌面視窗功能需先建置原生橋接；不開視窗的 WGPU 計算與 Jolt 由 NuGet 提供原生引擎。
瀏覽器支援範圍以目前範例與測試為準，各平台差異請見對應指南。

## 開發與測試

在專案根目錄準備瀏覽器資源後，建置與測試：

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser

# 使用桌面視窗功能時先建置原生橋接
.\native\build.ps1

dotnet build AquariusLang.sln -c Release -m:1
dotnet test AquariusLangVMTesting -c Release --no-build -m:1
npm test --prefix AquariusWebCompiler

# 開發時使用統一 CLI
dotnet run --project AquariusCli -- build examples/increment.aqua -o .web-build/increment.bottle
dotnet run --project AquariusCli -- run .web-build/increment.bottle
dotnet run --project AquariusCli -- repl
```

VM 以運算元堆疊與明確呼叫框架執行，星泉遞迴不累積 C# 呼叫堆疊；
閉包保留定義環境，REPL 在多次輸入間保留全域變數。
核心共用圖學／物理介面，桌面與瀏覽器各自提供後端實作，詳見 [架構說明](ARCHITECTURE.md)。

需要完整範例與瀏覽器實機回歸時：

```powershell
# 建置 Debug 方案並產生範例網站
.\AquariusWebCompiler\scripts\build-web.ps1

# 執行原始碼與移除原始碼後的 bottle 範例（需要 Python、GPU 與桌面）
.\scripts\test-examples.ps1 -SkipBuild

# 瀏覽器 WebGPU、Jolt 與互動測試（預設使用已安裝的 Chrome）
node AquariusWebCompiler/tests/browser-smoke.mjs
```

GPU 單元測試的啟用方式、測試報告與效能量測見 [VM 測試說明](AquariusLangVMTesting/README.md)。
瀏覽器測試也可設定 `$env:AQUARIUS_BROWSER = 'msedge'` 使用已安裝的 Edge。
Linux／macOS 的原生建置與發佈方式見 [原生指南](native/README.md)；
本 README 的批次打包流程固定使用 Windows x64。

## 專案結構

| 目錄／專案 | 職責 |
| --- | --- |
| `AquariusCli` | `aqua` 統一入口：build、run、web target 與 REPL。 |
| `AquariusLangVM` | 語言前端、位元碼編譯器、堆疊 VM 與共用圖學／物理介面。 |
| `AquariusPackaging` | `.bottle` 模組與資源封裝、版本驗證及路徑解析；只依賴核心。 |
| `AquariusDesktopVMREPL` | 桌面內建函式、原生後端與相容桌面入口。 |
| `AquariusWebCompiler` | 靜態網站輸出、JavaScript VM、WebGPU 與 Jolt WASM 後端。 |
| `AquariusLangVMTesting` | 語言、VM、封裝、CLI 與桌面功能測試。 |
| `AquariusLanguageServer`、`editors/vscode` | LSP 與 VS Code 擴充套件。 |
| `native`、`scripts` | 原生橋接建置、發佈及驗證腳本；根目錄 `build-release.bat` 為完整打包入口。 |
| `examples` | 語言、圖學、物理與互動範例。 |
| `website` | 星泉介紹網站與部署文件。 |

## 編輯器與延伸文件

[VS Code 擴充套件](https://marketplace.visualstudio.com/items?itemName=aquariuslang.aquariuslang-tw)
提供語法上色、片段、即時診斷、雙語補全、懸停說明、定義跳轉與符號列表。
安裝、設定、VSIX 打包及執行階段需求見 [VS Code 操作指南](editors/vscode/README.md)。
其他 LSP 編輯器可參考 [語言伺服器說明](AquariusLanguageServer/README.md)；伺服器分析語法，不執行程式。

[星泉介紹網站](https://aquarius-language.github.io/AquariusLangTW/) 的原始碼與本機操作位於
[website](website/README.md)，GitHub Pages 設定見 [網站部署指南](website/docs/github-pages.md)。

## 名稱與授權

**星泉**結合 Aquarius（水瓶座）的星空與水意象，寓意「靈感如星光閃耀，創意如泉水湧流」。

> 用熟悉的文字，讓靈感湧成作品。

本專案採用 [MIT License](LICENSE)，Copyright (c) 2026 Temple Lin。
第三方相依套件保留各自授權，原始授權位於 `native/vendor/` 與
`AquariusDesktopVMREPL/licenses/`；正式套件會一併附上授權檔。
