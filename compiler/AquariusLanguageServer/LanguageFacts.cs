namespace AquariusLanguageServer;

/// <summary>Facts of the language itself: keywords, the core globals' descriptions and the portable modules the guest
/// runtime implements (runtime/guest/{threads,arrays,hashes,texts}.h; a test keeps these tables equal). Library
/// modules come from <see cref="ModuleCatalog"/>.</summary>
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
        ["中斷"] = "中斷; — Exit a loop.", ["真"] = "Boolean true.", ["假"] = "Boolean false.",
        ["var"] = "var name = expression; — Declare a variable. Alias: 變數.",
        ["function"] = "function(parameters, ...) { ... } — Create a function and closure. Alias: 函式.",
        ["if"] = "if (condition) { ... } — Conditional expression. Alias: 如果.",
        ["elif"] = "elif (condition) { ... } — Additional conditional branch; also else if. Alias: 否則如果.",
        ["else"] = "else { ... } / else if (condition) { ... } — Fallback or additional branch. Alias: 否則.",
        ["return"] = "return expression; — Return a value from a function. Alias: 回傳.",
        ["for"] = "for (var i = 0; condition; i++) { ... } — For loop. Alias: 迴圈.",
        ["break"] = "break; — Exit a loop. Alias: 中斷.",
        ["true"] = "Boolean true. Alias: 真.",
        ["false"] = "Boolean false. Alias: 假."
    };

    /// <summary>The type names of optional annotations (recognized only after a colon; see TypeAnnotation).</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string>
    {
        ["整數"] = "型別 整數 — 32-bit integer. Alias: int.", ["int"] = "type int — 32-bit integer. Alias: 整數.",
        ["浮點數"] = "型別 浮點數 — 32-bit float. Alias: float.", ["float"] = "type float — 32-bit float. Alias: 浮點數.",
        ["雙精度"] = "型別 雙精度 — 64-bit double. Alias: double.", ["double"] = "type double — 64-bit double. Alias: 雙精度.",
        ["布林"] = "型別 布林 — Boolean. Alias: bool.", ["bool"] = "type bool — Boolean. Alias: 布林.",
        ["字串"] = "型別 字串 — string. Alias: string.", ["string"] = "type string — string. Alias: 字串.",
        ["陣列"] = "型別 陣列 — an array whose elements may be any values; add [] after a scalar type for a typed array (整數[]). Alias: array.",
        ["array"] = "type array — an array whose elements may be any values; add [] after a scalar type for a typed array (int[]). Alias: 陣列.",
    };

    /// <summary>Descriptions of the core globals; the runtime catalog adds any global not described here.</summary>
    public static IReadOnlyDictionary<string, string> Builtins => builtins.Value;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> builtins = new(CreateBuiltins);

    private static IReadOnlyDictionary<string, string> CreateBuiltins()
    {
        var pairs = new (string Chinese, string English, string Detail)[]
        {
            ("長度", "len", "長度(值) — UTF-16 string length, array element count or hash entry count."),
            ("最後一個", "last", "最後一個(陣列) — Last array element, or null for an empty array."),
            ("其餘", "rest", "其餘(陣列) — Array without its first element."),
            ("加入", "push", "加入(陣列, 值) — Return an array with a value appended."),
            ("印出", "print", "印出(值, ...) — Print values to the console."),
            ("型別", "typeOf", "型別(值) — The value's type tag: integer, float, double, string, boolean, null, array, hash, function, module or error."),
            ("目前時間", "currentTime", "目前時間() — Monotonic time in double milliseconds; subtract two readings to measure elapsed time. The origin is arbitrary, not a calendar timestamp."),
            ("目前日期", "currentDate", "目前日期() — The local calendar date as yyyy-MM-dd."),
            ("匯入", "import", "匯入(名稱或路徑) — Load a library module by name (Processing, Audio, Models, ...), an Aquarius script by relative path, or a Bottle library script (/bottles/NAME/main.aqua)."),
            ("是Windows", "isOSWindows", "是Windows() — Whether the runtime is Windows."),
            ("是Linux", "isOSLinux", "是Linux() — Whether the runtime is Linux."),
            ("是MacOS", "isOSMacOS", "是MacOS() — Whether the runtime is macOS."),
            ("是Android", "isOSAndroid", "是Android() — Whether the runtime is Android."),
            ("是網頁", "isWeb", "是網頁() — Whether the runtime is a web browser."),
            ("執行檔案", "execFile", "執行檔案(路徑, 引數陣列) — Execute a program synchronously (desktop)."),
            ("區域填色", "floodFill", "區域填色(...) — Flood-fill a grid."),
            ("目前工作目錄", "currWorkingDir", "目前工作目錄 — Directory of the executing source file (string value)."),
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (chinese, english, detail) in pairs) { result[chinese] = detail + $" Alias: {english}."; result[english] = detail + $" Alias: {chinese}."; }
        foreach (var module in PortableModules)
            foreach (string name in module.Names) result[name] = $"{string.Join(" / ", module.Names)} — Built-in portable module: {string.Join(", ", module.Members.Select(m => m.Names[0]))}.";
        return result;
    }

    /// <summary>The built-in portable modules (English name, Chinese name, then each function's English and Chinese
    /// names), exactly as the guest runtime declares them.</summary>
    public static readonly IReadOnlyList<CatalogModule> PortableModules = new[]
    {
        Portable("Thread", "執行緒", ("Start", "啟動"), ("Wait", "等待"), ("Result", "結果"), ("Cancel", "取消"), ("Close", "關閉"), ("IsDone", "是否完成"),
            ("Sleep", "休眠"), ("IsCancellationRequested", "是否要求取消")),
        Portable("Array", "陣列", ("Make", "建立"), ("Len", "長度"), ("Cap", "容量"), ("Grow", "擴充"), ("Append", "追加"), ("AppendArray", "追加陣列"),
            ("Copy", "複製"), ("Clone", "克隆"), ("Slice", "切片"), ("Fill", "填入"), ("Delete", "刪除"), ("Reverse", "反轉"), ("Index", "尋找"), ("Contains", "包含")),
        Portable("Hash", "雜湊", ("Keys", "鍵列表"), ("Values", "值列表"), ("Has", "包含鍵"), ("Get", "取得"), ("Delete", "刪除鍵"), ("Len", "長度"),
            ("Clone", "克隆"), ("Clear", "清空"), ("Merge", "合併"), ("Entries", "項目列表")),
        Portable("String", "字串", ("From", "轉文字"), ("Slice", "切片"), ("Find", "尋找"), ("Contains", "包含"), ("StartsWith", "開頭是"), ("EndsWith", "結尾是"),
            ("Split", "分割"), ("Join", "合併"), ("Replace", "取代"), ("Trim", "修剪"), ("Upper", "轉大寫"), ("Lower", "轉小寫"), ("Repeat", "重複"),
            ("Code", "字元碼"), ("FromCode", "從字元碼"), ("ParseInt", "解析整數")),
    };

    private static CatalogModule Portable(string english, string chinese, params (string English, string Chinese)[] functions) =>
        new("language", [english, chinese], functions.Select(f => new CatalogMember([f.English, f.Chinese], true, $"{english}.{f.English} / {chinese}.{f.Chinese}")).ToArray());
}
