# 星泉（AquariusLang 繁體中文版本）

以 C# 實作的直譯式程式語言，支援中文變數、函式與參數名稱。

作者：林天牧 / Temple Lin

## 名稱與寓意

**星泉**是 AquariusLang 繁體中文版本的中文名稱，希望讓人以熟悉的文字表達想法，
從程式邏輯到圖形與互動創作，將靈感化為能執行的作品。

- **星**：象徵星光、探索與靈感，呼應 Aquarius（水瓶座）的星空意象，寓意對未知的好奇與創作的可能。
- **泉**：象徵泉水湧出、生生不息，呼應 Aquarius 的水意象，寓意知識流動與創造力源源不絕。

「星泉」寄託的是：**靈感如星光閃耀，創意如泉水湧流。**

> 用熟悉的文字，讓靈感湧成作品。

## 語法

| 原關鍵字 | 繁體中文關鍵字 |
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

英文關鍵字已改為以上中文關鍵字。運算符號、括號、分號以及數值的 `f` / `d` 後綴維持原有寫法。
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
完整中文範例位於 `AquariusDesktopInterpretedREPL/examples`。
綜合範例 [星艦遠征](AquariusDesktopInterpretedREPL/examples/starship_expedition/README.md)
以繁體中文名稱展示全部現有語法，印出質數航點、費氏航線與能量報告，並納入單元測試。

## OpenGL 電腦圖學

桌面直譯器提供 `匯入("GLFW")`、`匯入("GLAD")`、`匯入("GL")`、
`匯入("GLM")` 與 `匯入("STBImage")` 原生模組。
`GL` 包含完整的 **344 個 OpenGL 3.3 core 函式**與常數；`GLM` 是以
System.Numerics 實作的 GLM 風格向量／矩陣模組，`STBImage` 提供圖片載入。

首次執行前需建置原生函式庫（Windows 需要 CMake 與 Visual Studio C++ 工具）：

```powershell
./native/build.ps1
dotnet run --project AquariusDesktopInterpretedREPL -- AquariusDesktopInterpretedREPL/examples/opengl_cube/main.aqua
```

[旋轉材質立方體](AquariusDesktopInterpretedREPL/examples/opengl_cube/README.md)
以 Aquarius 編寫頂點、索引、GLSL 著色器、紋理、材質、光源與動畫迴圈。
按 **Esc** 離開、**空白鍵**暫停／繼續旋轉；支援調整視窗大小。
建置、平台限制、API 與測試說明請見 [圖學支援指南](native/README.md)。

## Processing 風格繪圖函式庫

`匯入("Processing")` 提供以 OpenGL 3.3 實作的創意程式設計 API：
2D 圖形、曲線、多邊形與孔洞、顏色、矩陣與樣式堆疊、圖片與像素、文字、
PGraphics 離屏畫布、PShape 可重用圖形、3D 立方體／球體、光源與材質、
GLSL 著色器、滑鼠／鍵盤事件、動畫迴圈、亂數、Perlin noise 與 PVector。

```powershell
./native/build.ps1
dotnet run --project AquariusDesktopInterpretedREPL -- AquariusDesktopInterpretedREPL/examples/processing_showcase/main.aqua
```

[六面板展示](AquariusDesktopInterpretedREPL/examples/processing_showcase/README.md)
支援空白鍵暫停、滑鼠吸引粒子、S 儲存 PNG、Esc 離開。
完整 API 與和 Java Processing 的差異請見
[Processing 使用指南](AquariusDesktopInterpretedREPL/graphics/Processing.md)。

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

建置完整方案需要 .NET 8 SDK；執行現有桌面直譯器與其測試另需 .NET 6 Runtime。

```powershell
dotnet run --project AquariusDesktopInterpretedREPL -- AquariusDesktopInterpretedREPL/examples/increment.aqua
dotnet test AquariusLang.sln
```
