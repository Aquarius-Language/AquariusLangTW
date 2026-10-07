namespace AquariusLanguageServer;

internal static class LanguageFacts
{
    public static readonly IReadOnlyDictionary<string, string> Keywords = new Dictionary<string, string>
    {
        ["變數"] = "變數 名稱 = 運算式; — Declare a variable.",
        ["函式"] = "函式(參數, ...) { ... } — Create a function and closure.",
        ["如果"] = "如果 (條件) { ... } — Conditional expression.",
        ["否則如果"] = "否則如果 (條件) { ... } — Additional conditional branch.",
        ["否則"] = "否則 { ... } — Fallback branch.",
        ["回傳"] = "回傳 運算式; — Return a value from a function.",
        ["迴圈"] = "迴圈 (變數 索引 = 0; 條件; 索引++) { ... } — For loop.",
        ["中斷"] = "中斷; — Exit a loop.", ["真"] = "Boolean true.", ["假"] = "Boolean false."
    };

    public static readonly IReadOnlyDictionary<string, string> Builtins = new Dictionary<string, string>
    {
        ["長度"] = "長度(值) — UTF-16 string length or array element count.",
        ["最後一個"] = "最後一個(陣列) — Last array element, or null for an empty array.",
        ["其餘"] = "其餘(陣列) — Array without its first element.",
        ["加入"] = "加入(陣列, 值) — Return an array with a value appended.",
        ["印出"] = "印出(值, ...) — Print values to the console.",
        ["匯入"] = "匯入(路徑) — Load an Aquarius module at runtime.",
        ["是Windows"] = "是Windows() — Whether the runtime is Windows.",
        ["是Linux"] = "是Linux() — Whether the runtime is Linux.",
        ["是MacOS"] = "是MacOS() — Whether the runtime is macOS.",
        ["執行檔案"] = "執行檔案(路徑, 引數陣列) — Execute a program synchronously.",
        ["目前工作目錄"] = "目前工作目錄 — Directory of the executing source file (string value)."
    };
}
