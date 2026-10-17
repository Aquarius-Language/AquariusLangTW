# 雜湊與字串函式庫、餘數運算子 / Hash and String libraries, remainder operator

`雜湊` / `Hash` 與 `字串` / `String` 是核心內建模組，和 [`陣列` / `Array`](arrays.md) 一樣
不需安裝或匯入；桌面、網頁與 Android 也可用 `匯入("雜湊")`、`匯入("字串")` 取得同一個模組。
所有演算法都在共用的核心 Wasm 執行（`AquariusCore/wasm/runtime/hashes.h`、`texts.h`），
C# 與 JavaScript 主機只傳遞可呼叫的權杖，因此三個平台的結果完全一致。

`Hash` / `雜湊` and `String` / `字串` are built-in core modules, like
[`Array` / `陣列`](arrays.md). No dependency or import is needed, and
`import("Hash")` / `import("String")` return the same modules on desktop, web and
Android. Every algorithm runs in the shared core Wasm, so results are identical on
every host. User declarations named `Hash`, `雜湊`, `String` or `字串` still shadow
the modules.

## 餘數運算子 / Remainder operator

`%` 與 `%=` 的優先順序與 `*`、`/` 相同。整數餘數向零截斷，與整數除法一致：
`7 % 3 == 1`、`-7 % 3 == -1`。任一運算元為浮點數時採浮點餘數（`7.5d % 2 == 1.5`），
結果型別依一般的數值提升規則；`%=` 保留左側變數的型別。整數對零取餘數是執行期錯誤，
浮點數對零取餘數得到 NaN。

`%` and `%=` share the precedence of `*` and `/`. Integer remainders truncate toward
zero, matching integer division. Floating operands produce a floating remainder with
the usual numeric promotion; `%=` keeps the left variable's type. An integer remainder
by zero is a runtime error; a floating remainder by zero is NaN.

```aquarius
迴圈 (變數 索引 = 0; 索引 < 10; 索引++) {
    如果 (索引 % 2 == 0) { 印出(索引, "是偶數"); }
}
```

## 雜湊 / Hash

```aquarius
變數 分數 = {"小明": 90, "小華": 85};
迴圈 (變數 i = 0; i < 長度(分數); i++) {
    變數 名字 = 雜湊.鍵列表(分數)[i];
    印出(名字, 分數[名字]);
}
雜湊.刪除鍵(分數, "小華");
印出(雜湊.取得(分數, "小美", 0), 雜湊.包含鍵(分數, "小明"));
```

| 繁體中文 | English | Parameters / 參數 | Result / 行為 |
| --- | --- | --- | --- |
| 鍵列表 | Keys | hash | New array of keys in insertion order / 依插入順序的鍵陣列 |
| 值列表 | Values | hash | New array of values in insertion order / 依插入順序的值陣列 |
| 項目列表 | Entries | hash | New array of `[key, value]` pairs / 鍵值配對陣列 |
| 包含鍵 | Has | hash, key | Boolean; keys are typed (`1` ≠ `1.0d` ≠ `"1"`) / 是否含鍵，鍵有型別 |
| 取得 | Get | hash, key, default | Stored value, or `default` when absent / 值或預設值 |
| 刪除鍵 | Delete | hash, key | Removes in place; returns whether a key was removed / 原地刪除，回傳是否刪除 |
| 長度 | Len | hash | Entry count / 項目數 |
| 克隆 | Clone | hash | Independent shallow copy / 獨立淺拷貝 |
| 清空 | Clear | hash | Removes every entry in place, returns the hash / 原地清空 |
| 合併 | Merge | destination, source | Writes every source pair into destination, returns destination / 合併並回傳目的地 |

The global `長度` / `len` also accepts hashes. Mutations keep the hash's identity, so
aliases and host-retained hashes observe them. Insertion order is preserved; a deleted
key that is written again moves to the end.

全域 `長度` / `len` 也接受雜湊表。原地修改保留物件身分，別名與主機保留的雜湊表都能看到變更。
刪除後重新寫入的鍵會排到最後。

## 字串 / String

字串以 UTF-16 code unit 為單位，索引與長度和 `長度`、`字串[索引]` 一致。
Strings are UTF-16 code-unit sequences; indexes and lengths match `len` and indexing.

```aquarius
變數 文 = 字串;
變數 網址 = "ws://192.168.1.5:8081/聊天";
變數 位置 = 文.尋找(網址, "://");
印出(文.切片(網址, 位置 + 3, 長度(網址)));
印出("分數：" + 文.轉文字(98.5d), 文.分割("甲,乙,丙", ","), 文.合併([1, 2, 3], "+"));
```

| 繁體中文 | English | Parameters / 參數 | Result / 行為 |
| --- | --- | --- | --- |
| 轉文字 | From | value | Text form identical to `印出` on the web host / 與網頁 `印出` 相同的文字 |
| 切片 | Slice | text, start, end | Copy of `[start, end)`; indexes must lie within the text / 半開區間 |
| 尋找 | Find | text, part | First index, or -1 / 第一個位置或 -1 |
| 包含 | Contains | text, part | Boolean / 是否包含 |
| 開頭是 | StartsWith | text, prefix | Boolean / 是否以前綴開頭 |
| 結尾是 | EndsWith | text, suffix | Boolean / 是否以後綴結尾 |
| 分割 | Split | text, separator | Array of parts, keeping empty fields; `""` splits into code points / 分割，空分隔字串依字元切開 |
| 合併 | Join | array, separator | Joins every element's `轉文字` form / 合併各元素 |
| 取代 | Replace | text, old, new | Replaces every non-overlapping occurrence; `old` must not be empty / 全部取代 |
| 修剪 | Trim | text | Removes leading/trailing whitespace, including U+3000 / 去除前後空白（含全形空白） |
| 轉大寫 | Upper | text | ASCII and full-width Latin letters / ASCII 與全形英文字母 |
| 轉小寫 | Lower | text | ASCII and full-width Latin letters / ASCII 與全形英文字母 |
| 重複 | Repeat | text, count | Text repeated `count` times / 重複 |
| 字元碼 | Code | text, index | Code point at index (surrogate pairs combined), or null / 字元碼或空值 |
| 從字元碼 | FromCode | codePoint | One-character text for 0..0x10FFFF / 由字元碼建立文字 |
| 解析整數 | ParseInt | text | 32-bit decimal integer, or null when invalid / 32 位元十進位整數或空值 |

`轉文字` formats numbers like JavaScript's `Number#toString`: the shortest digits that
read back as the same value, closest to it, with exponent notation below `1e-6` and
from `1e21` (`0.1d + 0.2d` → `0.30000000000000004`). Single-precision values use the
shortest digits for `float`. Booleans become `真` / `假`, null `null`, and arrays and
hashes use the same nested form as `印出`.

`轉文字` 的數字格式與 JavaScript 相同：取最短且最接近原值的往返數字，`1e-6` 以下與 `1e21`
以上使用指數表示。單精度數值以 float 的最短位數輸出；布林為 `真`／`假`。

Out-of-range indexes, wrong argument types and invalid counts return a language error
before any mutation. Tests: `tests/hash-library.json`, `tests/string-library.json`,
`tests/modulo-operator.json`, shared by `AquariusTests/compiler/PortableModulesTest.cs`
and `AquariusWebCompiler/tests/portable-modules.test.mjs`.
