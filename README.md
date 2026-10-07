# 星泉（AquariusLang）

以 C# 實作的程式語言，將原始碼編譯為位元碼，再由堆疊虛擬機（VM）執行，
支援中文變數、函式與參數名稱。

作者：林天牧 / Temple Lin

VSCode 擴充套件: https://marketplace.visualstudio.com/items?itemName=aquariuslang.aquariuslang-tw

## 專案結構

| 目錄／專案 | 職責 |
| --- | --- |
| `AquariusLangVM` | 詞法分析、語法分析、AST、物件、環境與內建函式介面，以及位元碼編譯器和堆疊 VM。 |
| `AquariusDesktopVMREPL` | VM 桌面入口、REPL、桌面內建函式、腳本匯入、圖學支援與可執行範例。 |
| `AquariusLangVMTesting` | 語言核心、VM 與桌面功能的 .NET 單元及整合測試，另含可選的效能量測。 |
| `AquariusLanguageServer` | LSP 語言伺服器；協定整合測試位於其 `tests/` 目錄。 |
| `native` | 原生圖學橋接、第三方原始碼、建置腳本與 OpenGL 綁定產生器。 |
| `editors/vscode` | VS Code 擴充套件及其建置、打包腳本與測試。 |
| `website` | 介紹網站與部署文件。 |

桌面入口與語言伺服器皆參考 `AquariusLangVM`；語言伺服器只使用詞法與語法分析，不執行程式。
範例位於 `AquariusDesktopVMREPL/examples`，原生函式庫安裝至該專案的 `runtimes/`；
兩者會複製到桌面入口的建置、發佈與測試輸出目錄。
測試指令與環境需求請見 [VM 測試](AquariusLangVMTesting/README.md)。

## VM 執行流程

執行流程為原始碼 → token → AST → 位元碼 → VM。
`VmCompiler` 編譯語法樹及函式本體，`VirtualMachine` 以運算元堆疊和明確的呼叫框架執行，
遞迴不累積 C# 呼叫堆疊。閉包保留定義時的環境；REPL 在多次輸入之間保留全域變數。
完整方案的 VM 核心、桌面入口、測試與語言伺服器皆以 .NET 8（`net8.0`）為目標框架。

`AquariusDesktopVMREPL` 提供桌面內建函式、腳本匯入與擴充函式庫，包含 OpenGL 與 Processing；
匯入的腳本及 Processing 回呼也透過 VM 執行。
詳細架構請見 [VM 核心](AquariusLangVM/README.md) 與 [VM 桌面入口](AquariusDesktopVMREPL/README.md)。

在專案根目錄執行（需要 .NET 8 SDK）：

```powershell
# 不帶參數進入 VM REPL
dotnet run --project AquariusDesktopVMREPL

# 執行中文程式
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/increment.aqua

# 只顯示編譯後的指令，不執行程式
dotnet run --project AquariusDesktopVMREPL -- --disassemble AquariusDesktopVMREPL/examples/increment.aqua
```

`--disassemble` 的輸出是可閱讀的位元碼指令列表，目前不提供序列化的位元碼執行檔格式。
語言語意與開發注意事項請見 [語言核心指南](AquariusLangVM/LANGUAGE.md)，
回歸測試及效能量測請見 [VM 測試說明](AquariusLangVMTesting/README.md)。

## 名稱與寓意

**星泉**是 AquariusLang 的中文名稱，希望讓人以熟悉的文字表達想法，
從程式邏輯到圖形與互動創作，將靈感化為能執行的作品。

- **星**：象徵星光、探索與靈感，呼應 Aquarius（水瓶座）的星空意象，寓意對未知的好奇與創作的可能。
- **泉**：象徵泉水湧出、生生不息，呼應 Aquarius 的水意象，寓意知識流動與創造力源源不絕。

「星泉」寄託的是：**靈感如星光閃耀，創意如泉水湧流。**

> 用熟悉的文字，讓靈感湧成作品。

## 語法

| 其它程式語言關鍵字 | 此語言對應關鍵字 |
| --- | --- |
| `let` | `變數` |
| `fn`（函式） | `函式` |
| `if` | `如果` |
| `elif` | `否則如果` |
| `else` | `否則` |
| `return` | `回傳` |
| `for` | `迴圈` |
| `break` | `中斷` |
| `true` / `false` | `真` / `假` |

運算符號、括號、分號以及數值的 `f` / `d` 後綴維持原有寫法。
變數名稱可使用 Unicode 字母與底線，後續字元也可包含數字及結合標記，例如 `計數1`、`中文_變數`。

```text
變數 計數 = 0;
變數 原值 = 計數++;  # 原值為 0，計數變成 1
變數 新值 = ++計數;  # 新值為 2，計數變成 2

變數 加法 = 函式(甲, 乙) {
    如果 (甲 > 乙) {
        回傳 甲 + 乙;
    } 否則如果 (甲 == 乙) {
        回傳 甲 * 2;
    } 否則 {
        回傳 乙 + 甲;
    }
};

迴圈 (變數 索引 = 0; 索引 < 5; 索引++) {
    計數++;
    如果 (計數 == 5) { 中斷; }
}
印出(計數, 加法(3, 5));
```

`++` 僅適用於已宣告的數值變數，保留整數、單精度與倍精度型別。
`++計數` 回傳新值，`計數++` 回傳原值；支援算式、函式引數及迴圈更新式。

## 桌面內建函式

| 原名稱 | 中文名稱 |
| --- | --- |
| `len` | `長度` |
| `last` | `最後一個` |
| `rest` | `其餘` |
| `push` | `加入` |
| `print` | `印出` |
| `import` | `匯入` |
| `isOSWindows` / `isOSLinux` / `isOSMacOS` | `是Windows` / `是Linux` / `是MacOS` |
| `execFile` | `執行檔案` |
| `currWorkingDir` | `目前工作目錄` |

其他功能包含全域及區域變數、閉包、函式呼叫、陣列、雜湊表與模組。
完整中文範例位於 `AquariusDesktopVMREPL/examples`。
綜合範例 [星艦遠征](AquariusDesktopVMREPL/examples/starship_expedition/README.md)
以繁體中文名稱展示全部現有語法，印出質數航點、費氏航線與能量報告，並納入單元測試。

## OpenGL 電腦圖學

桌面 VM 提供 `匯入("GLFW")`、`匯入("GLAD")`、`匯入("GL")`、
`匯入("GLM")` 與 `匯入("STBImage")` 原生模組。
`GL` 包含完整的 **344 個 OpenGL 3.3 core 函式**與常數；`GLM` 是以
System.Numerics 實作的 GLM 風格向量／矩陣模組，`STBImage` 提供圖片載入。

首次執行前需建置原生函式庫（Windows 需要 CMake 與 Visual Studio C++ 工具）：

```powershell
./native/build.ps1
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/opengl_cube/main.aqua
```

[旋轉材質立方體](AquariusDesktopVMREPL/examples/opengl_cube/README.md)
以 Aquarius 編寫頂點、索引、GLSL 著色器、紋理、材質、光源與動畫迴圈。
按 **Esc** 離開、**空白鍵**暫停／繼續旋轉；支援調整視窗大小。
建置、平台限制、API 與測試說明請見 [圖學支援指南](native/README.md)。

## Processing 風格繪圖函式庫

桌面 VM 的 `匯入("Processing")` 提供以 OpenGL 3.3 實作的創意程式設計 API：
2D 圖形、曲線、多邊形與孔洞、顏色、矩陣與樣式堆疊、圖片與像素、文字、
PGraphics 離屏畫布、PShape 可重用圖形、3D 立方體／球體、光源與材質、
GLSL 著色器、滑鼠／鍵盤事件、動畫迴圈、亂數、Perlin noise 與 PVector。

```powershell
./native/build.ps1
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/processing_showcase/main.aqua
```

[六面板展示](AquariusDesktopVMREPL/examples/processing_showcase/README.md)
支援空白鍵暫停、滑鼠吸引粒子、S 儲存 PNG、Esc 離開。
完整 API 與和 Java Processing 的差異請見
[Processing 使用指南](AquariusDesktopVMREPL/graphics/Processing.md)。

## Visual Studio Code 與 Language Server Protocol

已提供獨立的 .NET 8 LSP 語言伺服器與 VS Code 擴充套件，支援 `.aqua` 語法上色、
程式碼片段、即時語法錯誤、補完、滑鼠提示、同檔案定義跳轉與大綱。

安裝 .NET 8 Runtime（或更新版本）後，在 VS Code 選擇 **Extensions → … → Install from VSIX…**，
安裝 `editors/vscode/aquariuslang-tw-0.1.0.vsix`，再開啟可信任資料夾中的 `.aqua` 檔案。
擴充套件已包含伺服器，使用者不需要另外設定伺服器路徑。

完整的安裝、從原始碼打包、執行程式、設定與疑難排解請見
[VS Code 操作指南](editors/vscode/README.md)。其他支援 LSP 的編輯器可參考
[語言伺服器說明](AquariusLanguageServer/README.md)。

## 執行與測試

建置、執行與測試完整方案只需 .NET 8 SDK（已包含 .NET 8 Runtime）。

```powershell
dotnet build AquariusLang.sln -c Release
dotnet test AquariusLang.sln -c Release --no-build

# 直接執行 VM 測試專案
dotnet test AquariusLangVMTesting -c Release -m:1

# 語言伺服器與編輯器的 Node.js 測試
node --test AquariusLanguageServer/tests/lsp.test.js
npm --prefix editors/vscode run check
```

外部程式測試需要 PATH 上有 `python`；GPU 測試的啟用方式請見 [VM 測試說明](AquariusLangVMTesting/README.md)。

## 正式發佈：.NET 自包含部署（Self-contained）

自包含部署會將 .NET 執行階段與程式一起發佈，使用者可直接執行，無需另外安裝 .NET。
請在專案根目錄操作，先建置完整方案，再分別發佈桌面 VM 與語言伺服器；
`AquariusLangVM` 核心函式庫會隨各入口的專案參考一起建置並包含在發佈結果中。
測試專案用於驗證，不需要另外發佈。

### 建置環境

- .NET 8 SDK（已包含建置、執行與測試所需的 .NET 8 Runtime；僅安裝 Runtime 無法建置）。
- 完整圖學功能需要 CMake 3.20 以上與 C 編譯器；Windows 請安裝 Visual Studio C++ 建置工具。
- 首次還原 NuGet 套件、下載自包含執行階段與建置 GLFW 需要網路連線。
- 全部 .NET 專案皆以 `net8.0` 為目標框架；桌面 VM 與語言伺服器的自包含發佈皆包含 .NET 8 執行階段。

### Windows x64 完整建置與發佈

先建置原生圖學函式庫，再建置與測試 Release 方案，每一步成功後才執行下一步。
一般測試不會開啟圖學視窗。

```powershell
./native/build.ps1 -Runtime win-x64
dotnet build AquariusLang.sln -c Release
dotnet test AquariusLang.sln -c Release --no-build

# 桌面 VM：含語言核心、桌面函式、原生圖學函式庫及範例
dotnet publish AquariusDesktopVMREPL/AquariusDesktopVMREPL.csproj -c Release -f net8.0 -r win-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/win-x64/vm

# 語言伺服器：供支援 LSP 的編輯器直接啟動
dotnet publish AquariusLanguageServer/AquariusLanguageServer.csproj -c Release -f net8.0 -r win-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/win-x64/language-server

# VM 套件自動包含範例；另附原生相依套件授權
New-Item -ItemType Directory -Path dist/win-x64/vm/licenses -Force | Out-Null
Copy-Item native/vendor/GLFW-LICENSE.md dist/win-x64/vm/licenses/GLFW-LICENSE.md
Copy-Item native/vendor/glad/LICENSE dist/win-x64/vm/licenses/GLAD-LICENSE.txt
Copy-Item native/vendor/stb/LICENSE dist/win-x64/vm/licenses/STB-LICENSE.txt
```

再次打包時請使用新的輸出目錄，避免混入前次發佈的檔案。
桌面 VM 會將 `runtimes/**/*` 複製到發佈目錄，Windows x64 圖學函式庫應位於
`dist/win-x64/vm/runtimes/win-x64/native/aquarius_graphics.dll`。
`--self-contained true` 明確包含 .NET 執行階段；`UseAppHost=true` 產生可直接啟動的執行檔。
此流程採用資料夾發佈，保留原生函式庫的路徑，並關閉尚未驗證相容性的單檔打包與 trimming。
CLI 部署選項請見 [Microsoft 自包含發佈文件](https://learn.microsoft.com/en-us/dotnet/core/deploying/#publish-self-contained)。

### 執行與交付

從專案根目錄驗證發佈結果，直接啟動執行檔：

```powershell
# 不帶參數進入 REPL
./dist/win-x64/vm/AquariusDesktopVMREPL.exe

# 執行隨附的中文程式與圖學範例（圖學視窗按 Esc 關閉）
./dist/win-x64/vm/AquariusDesktopVMREPL.exe ./dist/win-x64/vm/examples/increment.aqua
./dist/win-x64/vm/AquariusDesktopVMREPL.exe ./dist/win-x64/vm/examples/opengl_cube/main.aqua
./dist/win-x64/vm/AquariusDesktopVMREPL.exe ./dist/win-x64/vm/examples/processing_showcase/main.aqua
```

交付時打包整個 `vm` 目錄，包含執行檔、DLL、設定檔、`runtimes`、範例與授權；
需要 LSP 功能時另附完整的 `language-server` 目錄。
在沒有安裝 .NET 的目標機器上再次驗證執行結果。
自包含部署包含 .NET，但圖學功能仍需要作業系統原生相依套件與支援 OpenGL 3.3 的驅動程式；
Windows 原生 DLL 的 MSVC 執行階段相依性也需在目標機器上確認。
更新 .NET 執行階段時需重新發佈並交付套件。

LSP 編輯器可將啟動命令設為 `language-server/AquariusLanguageServer.exe` 的絕對路徑，
使用標準輸入／輸出通訊。現有 VS Code 擴充套件仍透過 `dotnet` 啟動伺服器 DLL，
其 VSIX 打包流程為 framework-dependent；上述自包含伺服器不會自動改變擴充套件的啟動方式，
現有 VSIX 仍需安裝 .NET Runtime。詳見 [VS Code 操作指南](editors/vscode/README.md)。

### 其他平台與架構

每個作業系統與 CPU 架構需各自發佈，例如 `win-arm64`、`linux-x64`、`linux-arm64`、
`osx-x64` 或 `osx-arm64`；原生圖學函式庫的架構必須與 `-r` 指定的 RID 相符。
Windows 的 `build.ps1 -Runtime` 決定安裝路徑，仍需自行選用符合目標架構的 CMake／C 編譯器設定。

Linux x64 範例（先安裝 GLFW 所需的 X11／Wayland 開發套件）：

```sh
cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --config Release --parallel
cmake --install native/build --config Release --prefix AquariusDesktopVMREPL/runtimes/linux-x64/native
dotnet build AquariusLang.sln -c Release
dotnet test AquariusLang.sln -c Release --no-build
dotnet publish AquariusDesktopVMREPL/AquariusDesktopVMREPL.csproj -c Release -f net8.0 -r linux-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/linux-x64/vm
dotnet publish AquariusLanguageServer/AquariusLanguageServer.csproj -c Release -f net8.0 -r linux-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/linux-x64/language-server
./dist/linux-x64/vm/AquariusDesktopVMREPL ./dist/linux-x64/vm/examples/increment.aqua
```

Linux／macOS 的執行檔沒有 `.exe` 副檔名；交付前同樣需附上授權，範例會自動複製。
macOS 請安裝 Xcode 命令列工具，並將安裝路徑與發佈 RID 換成 `osx-x64` 或 `osx-arm64`。
目前 Windows x64 的圖學建置已驗證，Linux／macOS 尚待實機驗證，平台功能差異請見
[圖學支援指南](native/README.md) 與 [Processing 使用指南](AquariusDesktopVMREPL/graphics/Processing.md)。

## 星泉介紹網站

繁體中文介紹網站原始碼位於 [website/](website/README.md)，包含可互動的 3D 水瓶、語法示例、生態系與未來展望。

GitHub Pages 網址（部署後）：https://aquarius-language.github.io/AquariusLangTW/

推送或合併 `website/` 的變更至 `main`，會自動建置並部署；pull request 會先執行網站建置檢查。首次啟用請將儲存庫 **Settings → Pages → Source** 設為 **GitHub Actions**。詳細設定與本機操作請見 [網站部署指南](website/docs/github-pages.md)。
