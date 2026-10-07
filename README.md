# AquariusLang 繁體中文版本

以 C# 實作的直譯式程式語言，支援中文變數、函式與參數名稱。

作者：林天牧 / Temple Lin

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
