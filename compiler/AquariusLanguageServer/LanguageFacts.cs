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
        ["繼續"] = "繼續; — Start the loop's next iteration.",
        ["空值"] = "The null value: a missing field, entry or element.",
        ["結構"] = "結構 名稱 { 欄位: 型別; 欄位 } — Declare a struct type; construct values with 名稱(欄位: 值) or 名稱(值, ...). Fields left out are null.",
        ["非同步"] = "非同步 函式(參數, ...) { ... } — 非同步函式：呼叫時立即回傳承諾（承諾 / Promise），函式本身作為獨立工作執行，可在其中使用 等待。",
        ["非同步函式"] = "非同步函式(參數, ...) { ... } — 與 非同步 函式 相同：回傳承諾的非同步函式。",
        ["等待"] = "等待 運算式 — 暫停目前的非同步函式（或腳本最上層），直到承諾完成，取得其值；被拒絕時會以其原因失敗。其他值直接成為結果。",
        ["async"] = "async function(parameters, ...) { ... } — An async function: a call returns a promise (Promise / 承諾) at once and the body runs as its own task, where await may be used. Alias: 非同步.",
        ["await"] = "await expression — Suspend the current async function (or the script's top level) until a promise settles and give its value; a rejection fails with its reason. Any other value is the result itself. Alias: 等待.",
        ["var"] = "var name = expression; — Declare a variable. Alias: 變數.",
        ["function"] = "function(parameters, ...) { ... } — Create a function and closure. Alias: 函式.",
        ["if"] = "if (condition) { ... } — Conditional expression. Alias: 如果.",
        ["elif"] = "elif (condition) { ... } — Additional conditional branch; also else if. Alias: 否則如果.",
        ["else"] = "else { ... } / else if (condition) { ... } — Fallback or additional branch. Alias: 否則.",
        ["return"] = "return expression; — Return a value from a function (a bare return; returns null). Alias: 回傳.",
        ["for"] = "for (var i = 0; condition; i++) { ... } — For loop. Alias: 迴圈.",
        ["break"] = "break; — Exit a loop. Alias: 中斷.",
        ["continue"] = "continue; — Start the loop's next iteration. Alias: 繼續.",
        ["null"] = "The null value: a missing field, entry or element. Alias: 空值.",
        ["struct"] = "struct Name { field: type; field } — Declare a struct type; construct values with Name(field: value) or Name(value, ...). Fields left out are null. Alias: 結構.",
        ["true"] = "Boolean true. Alias: 真.",
        ["false"] = "Boolean false. Alias: 假."
    };

    /// <summary>The type names of optional annotations (recognized only after a colon; see TypeAnnotation).</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string>
    {
        ["整數"] = "型別 整數 — 32-bit integer. Alias: int.", ["int"] = "type int — 32-bit integer. Alias: 整數.",
        ["浮點數"] = "型別 浮點數 — 32-bit float. Alias: float.", ["float"] = "type float — 32-bit float. Alias: 浮點數.",
        ["雙精度"] = "型別 雙精度 — 64-bit double. Alias: double.", ["double"] = "type double — 64-bit double. Alias: 雙精度.",
        ["長整數"] = "型別 長整數 — 64-bit integer (literals end in L: 5000000000L). Alias: long.", ["long"] = "type long — 64-bit integer (literals end in L: 5000000000L). Alias: 長整數.",
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
            ("參數個數", "arityOf", "參數個數(函式) — How many parameters a script function (or async function) declares; -1 for native functions."),
            ("目前時間", "currentTime", "目前時間() — Monotonic time in double milliseconds; subtract two readings to measure elapsed time. The origin is arbitrary, not a calendar timestamp."),
            ("目前日期", "currentDate", "目前日期() — The local calendar date as yyyy-MM-dd."),
            ("匯入", "import", "匯入(名稱或路徑) — Load a library module by name (Processing, Audio, Models, ...), an Aquarius script by relative path, or a Bottle library script (/bottles/NAME/main.aqua)."),
            ("是Windows", "isOSWindows", "是Windows() — Whether the runtime is Windows."),
            ("是Linux", "isOSLinux", "是Linux() — Whether the runtime is Linux."),
            ("是MacOS", "isOSMacOS", "是MacOS() — Whether the runtime is macOS."),
            ("是Android", "isOSAndroid", "是Android() — Whether the runtime is Android."),
            ("是網頁", "isWeb", "是網頁() — Whether the runtime is a web browser."),
            ("執行檔案", "execFile", "執行檔案(路徑, 引數陣列) — Execute a program synchronously (desktop)."),
            ("區域填色", "floodFill", "區域填色(陣列, 寬, 高, x, y, 新值) — Flood-fill the four-connected region of a numeric grid around (x, y); returns the cells changed."),
            ("整數", "int", "整數(值) — Convert a number to a 32-bit integer: truncates toward zero, saturates, NaN gives 0."),
            ("浮點數", "float", "浮點數(值) — Convert a number to a 32-bit float."),
            ("雙精度", "double", "雙精度(值) — Convert a number to a 64-bit double."),
            ("長整數", "long", "長整數(值) — Convert a number to a 64-bit integer: truncates toward zero, saturates, NaN gives 0."),
            ("目前工作目錄", "currWorkingDir", "目前工作目錄 — Directory of the executing source file (string value)."),
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (chinese, english, detail) in pairs) { result[chinese] = detail + $" Alias: {english}."; result[english] = detail + $" Alias: {chinese}."; }
        foreach (var module in PortableModules)
            foreach (string name in module.Names) result[name] = $"{string.Join(" / ", module.Names)} — Built-in portable module: {string.Join(", ", module.Members.Select(m => m.Names[0]))}.";
        return result;
    }

    /// <summary>The built-in portable modules exactly as the guest runtime declares them (names in
    /// runtime/guest/{threads,arrays,hashes,texts}.h; a test keeps the names and arities equal), each function with its
    /// parameters in both languages (optional ones end in ?) and what it does.</summary>
    private static readonly Dictionary<(string, string), string[]> portableParameters = new();
    public static readonly IReadOnlyList<CatalogModule> PortableModules = new[]
    {
        Portable("Thread", "執行緒",
            ("Start", "啟動", "function, arguments?", "函式, 引數陣列?", "Start an isolated worker; returns its handle. / 啟動獨立工作，回傳控制代碼。"),
            ("Wait", "等待", "handle, timeout?", "工作, 逾時?", "True when the worker stopped, false on timeout. / 停止時為真，逾時為假。"),
            ("Result", "結果", "handle", "工作", "Wait and return a copy of the result; worker errors propagate. / 等待並取得結果副本。"),
            ("Cancel", "取消", "handle", "工作", "Request cancellation without waiting. / 要求取消，不等待。"),
            ("Close", "關閉", "handle, timeout?", "工作, 逾時?", "Cancel, join and release the worker. / 取消、等待停止並釋放。"),
            ("IsDone", "是否完成", "handle", "工作", "Whether the worker stopped. / 是否已停止。"),
            ("Sleep", "休眠", "milliseconds", "毫秒", "Sleep; a cancelled worker wakes and gets false. / 休眠。"),
            ("IsCancellationRequested", "是否要求取消", "", "", "Whether this worker was asked to stop (false on the main execution). / 是否要求取消。")),
        Portable("Array", "陣列",
            ("Make", "建立", "length, value, capacity?", "長度, 值, 容量?", "New array of length copies of value, with at least capacity. / 建立填好初值的陣列。"),
            ("Len", "長度", "array", "陣列", "Element count. / 元素數。"),
            ("Cap", "容量", "array", "陣列", "Allocated capacity. / 已配置容量。"),
            ("Grow", "擴充", "array, additional", "陣列, 額外", "Reserve room for additional elements; returns the array. / 預留空間。"),
            ("Append", "追加", "array, value", "陣列, 值", "Append in place; returns the array. / 原地追加。"),
            ("AppendArray", "追加陣列", "array, source", "陣列, 來源", "Append every element of source in place. / 批次追加。"),
            ("Copy", "複製", "destination, source, destinationStart?, sourceStart?, count?", "目的地, 來源, 目的起點?, 來源起點?, 數量?", "Copy the range that fits; returns the count. / 複製可容納的範圍，回傳數量。"),
            ("Clone", "克隆", "array", "陣列", "Independent shallow copy. / 獨立淺拷貝。"),
            ("Slice", "切片", "array, start, end", "陣列, 起點, 終點", "Copy of [start, end). / 半開區間的拷貝。"),
            ("Fill", "填入", "array, value", "陣列, 值", "Fill in place; returns the array. / 原地填入。"),
            ("Delete", "刪除", "array, start, end", "陣列, 起點, 終點", "Remove [start, end) in place. / 原地刪除。"),
            ("Reverse", "反轉", "array", "陣列", "Reverse in place. / 原地反轉。"),
            ("Index", "尋找", "array, value", "陣列, 值", "First index of value, or -1. / 第一個相符索引或 -1。"),
            ("Contains", "包含", "array, value", "陣列, 值", "Whether the array holds value. / 是否包含。"),
            ("Sort", "排序", "array, compare?", "陣列, 比較?", "Sort in place (numbers or strings, or by compare(x, y) < 0); stable; returns the array. / 原地穩定排序。"),
            ("Sum", "總和", "array", "陣列", "Sum of the numbers with the rules of +; 0 for []. / 依 + 規則加總。"),
            ("Min", "最小值", "array", "陣列", "First smallest number or string; null for []. / 最小元素。"),
            ("Max", "最大值", "array", "陣列", "First largest number or string; null for []. / 最大元素。"),
            ("Map", "映射", "array, function", "陣列, 函式", "New array of function(element). / 新陣列。"),
            ("Filter", "篩選", "array, function", "陣列, 函式", "New array of the elements function accepts. / 篩選後的新陣列。"),
            ("Reduce", "歸約", "array, function, initial", "陣列, 函式, 初值", "function(accumulator, element) from left to right. / 由左至右累積。")),
        Portable("Hash", "雜湊",
            ("Keys", "鍵列表", "hash", "雜湊", "Keys in insertion order. / 依插入順序的鍵。"),
            ("Values", "值列表", "hash", "雜湊", "Values in insertion order. / 依插入順序的值。"),
            ("Has", "包含鍵", "hash, key", "雜湊, 鍵", "Whether the typed key is present. / 是否含鍵。"),
            ("Get", "取得", "hash, key, default", "雜湊, 鍵, 預設", "The stored value, or default. / 值或預設值。"),
            ("Delete", "刪除鍵", "hash, key", "雜湊, 鍵", "Remove in place; whether a key was removed. / 原地刪除。"),
            ("Len", "長度", "hash", "雜湊", "Entry count. / 項目數。"),
            ("Clone", "克隆", "hash", "雜湊", "Independent shallow copy. / 獨立淺拷貝。"),
            ("Clear", "清空", "hash", "雜湊", "Remove every entry in place. / 原地清空。"),
            ("Merge", "合併", "destination, source", "目的地, 來源", "Write every pair of source into destination. / 合併。"),
            ("Entries", "項目列表", "hash", "雜湊", "[key, value] pairs in insertion order. / 鍵值配對。")),
        Portable("String", "字串",
            ("From", "轉文字", "value", "值", "Text form, as 印出 shows it. / 轉成文字。"),
            ("Slice", "切片", "text, start, end", "文字, 起點, 終點", "Copy of [start, end) in UTF-16 units. / 半開區間。"),
            ("Find", "尋找", "text, part", "文字, 部分", "First index of part, or -1. / 第一個位置或 -1。"),
            ("Contains", "包含", "text, part", "文字, 部分", "Whether text contains part. / 是否包含。"),
            ("StartsWith", "開頭是", "text, prefix", "文字, 前綴", "Whether text starts with prefix. / 是否以前綴開頭。"),
            ("EndsWith", "結尾是", "text, suffix", "文字, 後綴", "Whether text ends with suffix. / 是否以後綴結尾。"),
            ("Split", "分割", "text, separator", "文字, 分隔", "Parts between separators (\"\" splits into characters). / 分割。"),
            ("Join", "合併", "array, separator", "陣列, 分隔", "Elements' text joined by separator. / 合併。"),
            ("Replace", "取代", "text, old, new", "文字, 舊, 新", "Replace every occurrence. / 全部取代。"),
            ("Trim", "修剪", "text", "文字", "Without leading and trailing whitespace (U+3000 too). / 去除前後空白。"),
            ("Upper", "轉大寫", "text", "文字", "ASCII and full-width letters upper-cased. / 轉大寫。"),
            ("Lower", "轉小寫", "text", "文字", "ASCII and full-width letters lower-cased. / 轉小寫。"),
            ("Repeat", "重複", "text, count", "文字, 次數", "Text repeated count times. / 重複。"),
            ("Code", "字元碼", "text, index", "文字, 索引", "Code point at index, or null. / 字元碼。"),
            ("FromCode", "從字元碼", "codePoint", "字元碼", "One-character text. / 由字元碼建立文字。"),
            ("ParseInt", "解析整數", "text", "文字", "32-bit decimal integer, or null. / 解析整數。"),
            ("ParseNumber", "解析數字", "text", "文字", "Integer within 32 bits, else double (1.5, 1e-9, Infinity, NaN), or null. / 解析數字。"),
            ("ParseLong", "解析長整數", "text", "文字", "64-bit decimal integer (長整數 / long), or null. / 解析長整數。")),
        Portable("Math", "數學",
            ("Abs", "絕對值", "x", "數", "Absolute value, same number type. / 絕對值，型別不變。"),
            ("Sign", "正負號", "x", "數", "-1, 0 or 1 in x's type (±0 and NaN unchanged). / 正負號。"),
            ("Min", "最小值", "a, b", "甲, 乙", "Smaller number (NaN if either is NaN; -0 below 0), wider type. / 較小值。"),
            ("Max", "最大值", "a, b", "甲, 乙", "Larger number (NaN if either is NaN), wider type. / 較大值。"),
            ("Clamp", "限制範圍", "x, low, high", "數, 下限, 上限", "Min(Max(x, low), high), widest type. / 限制在範圍內。"),
            ("Floor", "向下取整", "x", "數", "Largest whole number <= x, same type. / 向下取整，型別不變。"),
            ("Ceil", "向上取整", "x", "數", "Smallest whole number >= x, same type. / 向上取整，型別不變。"),
            ("Round", "四捨五入", "x", "數", "Nearest whole number, halves away from zero, same type. / 四捨五入（遠離零），型別不變。"),
            ("Trunc", "截斷", "x", "數", "Whole part toward zero, same type. / 去除小數，型別不變。"),
            ("FloorInt", "向下取整數", "x", "數", "Floor as an integer (saturating, NaN gives 0). / 向下取整成整數。"),
            ("CeilInt", "向上取整數", "x", "數", "Ceil as an integer (saturating, NaN gives 0). / 向上取整成整數。"),
            ("RoundInt", "四捨五入取整數", "x", "數", "Round as an integer (saturating, NaN gives 0). / 四捨五入成整數。"),
            ("TruncInt", "截斷取整數", "x", "數", "Trunc as an integer, like 整數(x). / 截斷成整數。"),
            ("Sqrt", "平方根", "x", "數", "Square root (NaN below 0). / 平方根。"),
            ("Cbrt", "立方根", "x", "數", "Cube root. / 立方根。"),
            ("Hypot", "斜邊長", "x, y", "x, y", "sqrt(x² + y²) without overflow. / 斜邊長。"),
            ("Pow", "次方", "x, y", "底數, 指數", "x to the power y. / 次方。"),
            ("Exp", "指數", "x", "數", "e to the power x. / e 的 x 次方。"),
            ("Log", "對數", "x", "數", "Natural logarithm (log(0) = -Infinity). / 自然對數。"),
            ("Log2", "以二為底對數", "x", "數", "Base-2 logarithm. / 以 2 為底的對數。"),
            ("Log10", "以十為底對數", "x", "數", "Base-10 logarithm. / 以 10 為底的對數。"),
            ("Sin", "正弦", "x", "弧度", "Sine of radians. / 正弦。"),
            ("Cos", "餘弦", "x", "弧度", "Cosine of radians. / 餘弦。"),
            ("Tan", "正切", "x", "弧度", "Tangent of radians. / 正切。"),
            ("Asin", "反正弦", "x", "數", "Arc sine in radians. / 反正弦。"),
            ("Acos", "反餘弦", "x", "數", "Arc cosine in radians. / 反餘弦。"),
            ("Atan", "反正切", "x", "數", "Arc tangent in radians. / 反正切。"),
            ("Atan2", "雙參數反正切", "y, x", "y, x", "Angle of (x, y) in radians. / 點 (x, y) 的角度。"),
            ("Sinh", "雙曲正弦", "x", "數", "Hyperbolic sine. / 雙曲正弦。"),
            ("Cosh", "雙曲餘弦", "x", "數", "Hyperbolic cosine. / 雙曲餘弦。"),
            ("Tanh", "雙曲正切", "x", "數", "Hyperbolic tangent. / 雙曲正切。"),
            ("IsNaN", "是非數", "x", "數", "Whether x is NaN. / 是否為非數。"),
            ("IsFinite", "是有限數", "x", "數", "Whether x is neither infinite nor NaN. / 是否為有限數。"),
            ("Fmod", "浮點餘數", "x, y", "被除數, 除數", "Exact remainder of x / y truncated toward zero (C fmod). / 浮點餘數。"),
            ("Random", "亂數", "", "", "Uniform double in [0, 1) (PCG32). / 0 到 1 之間的亂數。"),
            ("RandomInt", "亂數整數", "low, high", "下限, 上限", "Uniform integer in [low, high); low when equal. / 範圍內的亂數整數。"),
            ("Seed", "亂數種子", "seed", "種子", "Restart the sequence: one seed gives the same numbers on every target. / 設定亂數種子。"))
            .With(("PI", "圓周率", "π"), ("E", "自然常數", "e"), ("TAU", "二倍圓周率", "2π"), ("Infinity", "無限大", "+∞"), ("NaN", "非數", "not a number (NaN)")),
    };

    /// <summary>The parameters of a portable function as declared above (English), for arity checks.</summary>
    public static IReadOnlyList<string> PortableParameters(string module, string function) =>
        portableParameters.TryGetValue((module, function), out var list) ? list : Array.Empty<string>();

    private static CatalogModule Portable(string english, string chinese, params (string English, string Chinese, string EnglishParameters, string ChineseParameters, string Doc)[] functions)
    {
        foreach (var f in functions) portableParameters[(english, f.English)] = f.EnglishParameters.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return new("language", [english, chinese], functions.Select(f => new CatalogMember([f.English, f.Chinese], true,
            $"{chinese}.{f.Chinese}({f.ChineseParameters}) / {english}.{f.English}({f.EnglishParameters}) — {f.Doc}")).ToArray());
    }

    /// <summary>A portable module's constants (double values, after its functions; maths.h math_constants).</summary>
    private static CatalogModule With(this CatalogModule module, params (string English, string Chinese, string Doc)[] constants) =>
        module with { Members = module.Members.Concat(constants.Select(c => new CatalogMember([c.English, c.Chinese], false,
            $"{module.Names[1]}.{c.Chinese} / {module.Names[0]}.{c.English} — {c.Doc} (雙精度 double)"))).ToArray() };
}
