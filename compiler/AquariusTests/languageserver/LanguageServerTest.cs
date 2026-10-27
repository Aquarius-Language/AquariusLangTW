using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Compiler;
using AquariusLanguageServer;
using AquariusTests.Bottle;
using static AquariusTests.LanguageServer.LanguageServerSession;

namespace AquariusTests.LanguageServer;

/// <summary>The language server as editors run it: a process speaking LSP over stdio. Library modules come from the
/// runtime's catalog (aquarius --catalog), the language's portable modules and the library manifests.</summary>
public class LanguageServerTest
{
    private const string Uri = DefaultUri;

    private static LanguageServerSession Launch(IReadOnlyDictionary<string, string>? environment = null)
    {
        var session = new LanguageServerSession(environment);
        session.Initialize();
        return session;
    }

    [Fact]
    public void ChineseEnglishAndMixedSyntaxProvideDiagnosticsKeywordsAndSourceDefinitions()
    {
        using var s = Launch();
        string text = "var 加法, add = function(甲, b) {\n  if (false) { return 0; } else 如果 (真) { 回傳 甲 + b; } else { return 1; }\n};\n變數 result = add(1, 2);";
        NoDiagnostics(s.Open(text));
        var completion = s.Feature("completion", 0, 0);
        foreach (string name in new[] { "變數", "函式", "回傳", "如果", "否則如果", "否則", "迴圈", "中斷", "真", "假", "var", "function", "return", "if", "elif", "else", "for", "break", "true", "false" })
            Assert.True(HasItem(completion, name, 14), name);
        Assert.Matches("Declare a variable", HoverText(s.Feature("hover", 0, 1)));
        Assert.Matches("function.*closure", HoverText(s.Feature("hover", 0, 15)));
        var definition = s.Feature("definition", 3, 12);
        Assert.Equal((0, 8), Start(definition)); Assert.Equal((0, 11), End(definition));
        var symbols = s.Symbols();
        foreach (string name in new[] { "加法", "add" })
            Assert.Contains(symbols.EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == name && symbol.GetProperty("kind").GetInt32() == 12);
        NoDiagnostics(s.Open("var f = function(n) { if (n < 0) { return -1; } elif (n == 0) { return 0; } else { return 1; } };\nfor (var i = 0; i < 2; i++) { if (true) { break; } }"));
        Assert.True(s.Open("var function = 1;").GetArrayLength() > 0);
    }

    [Fact]
    public void ThreadAnd執行緒ProvideBilingualCompletionImportAliasesAndShadowing()
    {
        using var s = Launch();
        foreach (var (source, line, column) in new[] { ("Thread.", 0, 7), ("執行緒.", 0, 4), ("變數 t=匯入(\"執行緒\");\nt.", 1, 2) }) {
            s.Open(source);
            var items = Labels(s.Feature("completion", line, column));
            foreach (string name in new[] { "Start", "啟動", "Wait", "等待", "Result", "結果", "Cancel", "取消", "Close", "關閉", "Sleep", "休眠", "IsDone", "是否完成", "IsCancellationRequested", "是否要求取消" })
                Assert.Contains(name, items);
        }
        s.Open("變數 Thread=1;\nThread.");
        Assert.Equal(0, s.Feature("completion", 1, 7).GetArrayLength());
    }

    [Fact]
    public void PortableArrayModulesOfferBothLanguagesAndRespectLexicalShadowing()
    {
        using var s = Launch();
        s.Open("陣列.建立(2,0);\nArray.");
        var items = Labels(s.Feature("completion", 1, 6));
        foreach (string name in new[] { "Make", "建立", "AppendArray", "追加陣列", "Copy", "複製" }) Assert.Contains(name, items);
        Assert.Matches("Make", HoverText(s.Feature("hover", 0, 4)));
        s.Open("變數 陣列=99;\n陣列.");
        Assert.Equal(0, s.Feature("completion", 1, 3).GetArrayLength());
        s.Open("變數 arrays=匯入(\"陣列\");\narrays.");
        Assert.True(HasItem(s.Feature("completion", 1, 7), "Make"));
        // Every portable module completes from its globals and through import, in both languages.
        foreach (var module in LanguageFacts.PortableModules)
            foreach (string alias in module.Names) {
                s.Open($"變數 m=匯入(\"{alias}\");\nm.");
                Assert.Equal(module.Members.SelectMany(m => m.Names).Order(StringComparer.Ordinal), Labels(s.Feature("completion", 1, 2)).Order(StringComparer.Ordinal));
            }
    }

    [Fact]
    public void LspLifecycleUnsupportedRequestsMalformedJsonAndFragmentedUtf8Frames()
    {
        using var s = new LanguageServerSession();
        s.Send("textDocument/hover", new { }, "before-init");
        Assert.Equal(-32002, s.ErrorCode("before-init"));
        byte[] body = "{broken json"u8.ToArray();
        s.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        s.Write(body);
        Assert.Equal(-32700, s.Take(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null).GetProperty("error").GetProperty("code").GetInt32());
        var capabilities = s.Initialize().GetProperty("capabilities");
        Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        s.Send("unsupported/request", new { }, "unknown");
        Assert.Equal(-32601, s.ErrorCode("unknown"));
        // Raw UTF-8 (a four-byte emoji, three-byte Chinese) split across writes, with a lowercase header and a Content-Type.
        byte[] open = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri +
            "\",\"languageId\":\"aquarius\",\"version\":1,\"text\":\"變數 中文 = \\\"😀\\\";\"}}}");
        byte[] frame = [.. Encoding.ASCII.GetBytes($"content-length: {open.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n"), .. open];
        for (int i = 0; i < frame.Length; i += 3) s.Write(frame[i..Math.Min(frame.Length, i + 3)]);
        NoDiagnostics(s.Diagnostics(1));
        Assert.Equal(JsonValueKind.Null, s.Request("shutdown").ValueKind);
        s.Send("exit");
        Assert.Equal(0, s.WaitForExit());
    }

    [Fact]
    public void ChineseCompletionHoverDefinitionAndOutlineUseRealSourceRanges()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 加法 = 函式(甲, 乙) { 回傳 甲 + 乙; };\r\n印出(加法(1, 2));"));
        var completion = s.Feature("completion", 1, 0);
        Assert.True(HasItem(completion, "加法", 3));
        Assert.True(HasItem(completion, "印出"));
        Assert.Matches(@"加法\(甲, 乙\)", HoverText(s.Feature("hover", 1, 3)));
        var definition = s.Feature("definition", 1, 3);
        Assert.Equal(Uri, definition.GetProperty("uri").GetString());
        Assert.Equal((0, 3), Start(definition)); Assert.Equal((0, 5), End(definition));
        Assert.Contains(s.Symbols().EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "加法" && symbol.GetProperty("kind").GetInt32() == 12);
    }

    [Fact]
    public void TypedProgramsShowAnnotationsCompleteTypeNamesAndReportTypeErrors()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 次數: 整數 = 0;\n變數 樹高 = 函式(樹: 陣列, 深: 整數): 整數 { 回傳 深; };\nvar scale = function(v: double[], k: float): bool { return true; };\n變數 表 = {\"鍵\": 次數};"));
        Assert.Matches("變數 次數: 整數", HoverText(s.Feature("hover", 0, 3)));
        Assert.Matches(@"樹高\(樹: 陣列, 深: 整數\): 整數", HoverText(s.Feature("hover", 1, 3)));
        Assert.Matches("參數 樹: 陣列", HoverText(s.Feature("hover", 1, 11)));
        Assert.Matches(@"scale\(v: double\[\], k: float\): bool", HoverText(s.Feature("hover", 2, 5)));
        Assert.Matches("型別 整數.*int", HoverText(s.Feature("hover", 0, 7)));
        Assert.Matches("array whose elements may be any values", HoverText(s.Feature("hover", 1, 14)));
        Assert.Matches("type double", HoverText(s.Feature("hover", 2, 24)));
        // After an annotation's colon only type names complete; after a hash literal's colon, ordinary names do.
        var types = Labels(s.Feature("completion", 1, 13));
        foreach (string name in new[] { "整數", "浮點數", "雙精度", "布林", "字串", "陣列", "int", "float", "double", "bool", "string", "array" }) Assert.Contains(name, types);
        Assert.DoesNotContain("次數", types);
        var values = Labels(s.Feature("completion", 3, 14));
        Assert.Contains("次數", values); Assert.DoesNotContain("雙精度", values);
        // Type errors are diagnostics in either language.
        s.Change(Uri, 2, new { text = "var n: int = 2.5d;\n變數 名: 整數 = \"字\";" });
        Assert.Contains(s.Diagnostics(2).EnumerateArray(), d => d.GetProperty("message").GetString()!.Contains("Type mismatch"));
    }

    [Fact]
    public void CoreTimeAliasesProvideCompletionAndExplainMillisecondsInHover()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("目前時間();\ncurrentTime();"));
        var completion = s.Feature("completion", 0, 0);
        foreach (string name in new[] { "目前時間", "currentTime" }) Assert.True(HasItem(completion, name), name);
        foreach (int line in new[] { 0, 1 }) Assert.Matches("Monotonic.*double milliseconds", HoverText(s.Feature("hover", line, 0)));
    }

    [Fact]
    public void FunctionsAndLoopScopesResolveShadowedIdentifiersAndHideLocalsOutsideTheirScope()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 名稱 = 1;\n變數 測試 = 函式(名稱) {\n  變數 本地 = 名稱;\n  回傳 名稱;\n};\n印出(名稱);\n迴圈 (變數 索引 = 0; 索引 < 2; 索引++) { 印出(索引); }\n印出(名稱);"));
        Assert.Equal(1, Start(s.Feature("definition", 3, 5)).Line);
        Assert.Equal(0, Start(s.Feature("definition", 5, 3)).Line);
        Assert.True(HasItem(s.Feature("completion", 3, 2), "本地"));
        var outside = Labels(s.Feature("completion", 7, 0));
        Assert.DoesNotContain("本地", outside); Assert.DoesNotContain("索引", outside);
    }

    [Fact]
    public void IncrementalEditsMultipleChangesFullReplacementAndStaleVersions()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 名稱 = 1;\n印出(名稱);"));
        s.Change(Uri, 2, new { range = new { start = new { line = 0, character = 8 }, end = new { line = 0, character = 9 } }, text = "" });
        var errors = s.Diagnostics(2);
        Assert.True(errors.GetArrayLength() > 0);
        Assert.Equal((0, 8), Start(errors[0]));
        s.Change(Uri, 3,
            new { range = new { start = new { line = 0, character = 8 }, end = new { line = 0, character = 8 } }, text = "2" },
            new { range = new { start = new { line = 0, character = 8 }, end = new { line = 0, character = 9 } }, text = "3" });
        NoDiagnostics(s.Diagnostics(3));
        s.Change(Uri, 4, new { text = "變數 更新 = 4;" });
        NoDiagnostics(s.Diagnostics(4));
        s.Change(Uri, 2, new { text = "變數 舊版 = 0;" });
        Assert.Equal(new[] { "更新" }, s.Symbols().EnumerateArray().Select(symbol => symbol.GetProperty("name").GetString()!));
    }

    [Fact]
    public void Utf16OffsetsSurviveAstralCharactersCombiningMarksAndCrlf()
    {
        using var s = Launch();
        const string identifier = "𠀀名稱";
        string text = $"變數 {identifier} = \"😀\"; 印出({identifier});\r\n變數 café = 1;\r\n印出(café);";
        NoDiagnostics(s.Open(text));
        var definition = s.Feature("definition", 0, text.IndexOf(identifier, 8, StringComparison.Ordinal));
        Assert.Equal((0, 3), Start(definition)); Assert.Equal((0, 7), End(definition));
        Assert.Equal(8, End(s.Feature("definition", 2, 4)).Character);
    }

    [Fact]
    public void CommentsAndStringsSuppressCompletionAndModuleMembersDoNotResolveToLocals()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 名稱 = 1;\n# 名稱\n\"名稱\";\n變數 模組 = 匯入(\"other.aqua\");\n模組.名稱();\n## 名稱 # still comment ##印出(名稱);"));
        Assert.Equal(0, s.Feature("completion", 1, 4).GetArrayLength());
        Assert.Equal(0, s.Feature("completion", 2, 2).GetArrayLength());
        Assert.Equal(JsonValueKind.Null, s.Feature("definition", 4, 3).ValueKind);
        Assert.Equal(JsonValueKind.Null, s.Feature("hover", 4, 3).ValueKind);
        Assert.Equal(0, s.Feature("completion", 4, 3).GetArrayLength());
    }

    [Fact]
    public void IncompleteInputReportsErrorsAndNeverHangsOrCrashesTheServer()
    {
        using var s = Launch();
        foreach (string text in new[] { "## unterminated", "\"unterminated", "變數 函 = 函式(甲) { 回傳 甲;", "變數 函 = 函式(1) {};", "變數 數字 = 1.5;", "變數 = ;",
                     "迴圈 (變數 i = 0; i < 2; i++) 印出(i);", new string('(', 400) + "1" + new string(')', 400) })
            Assert.True(s.Open(text).GetArrayLength() > 0, text);
        NoDiagnostics(s.Open(string.Concat(Enumerable.Repeat("# line\n", 10000)) + "## block ##變數 正常 = 1;"));
        NoDiagnostics(s.Open("變數 正常 = 1;"));
    }

    [Fact]
    public void ClosingADocumentClearsErrorsAndInvalidFeaturePositionsReturnErrors()
    {
        using var s = Launch();
        s.Open("變數 值 = ;");
        s.Send("textDocument/hover", new { textDocument = new { uri = Uri }, position = new { line = 99, character = 0 } }, "bad-position");
        Assert.Equal(-32602, s.ErrorCode("bad-position"));
        s.Send("textDocument/didClose", new { textDocument = new { uri = Uri } });
        Assert.Equal(0, s.Take(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics")
            .GetProperty("params").GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void AllRepositoryExamplesParseThroughTheServerWithoutExecutingThem()
    {
        var installation = AquariusInstallation.Current;
        Assert.True(installation != null, "Needs the repository or a distribution (AQUARIUS_HOME).");
        string examples = Path.Combine(installation!.Root, "examples");
        Assert.True(Directory.Exists(examples), examples);
        using var s = Launch();
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(examples, "*.aqua", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            // Language diagnostics only: project diagnostics of examples depend on which Bottle libraries this machine
            // has cached (examples/jolt_physics needs Aqua_JoltPhysics from the cache), and the session's cache is empty.
            var language = s.Open(File.ReadAllText(file), FileUri(file)).EnumerateArray().Where(d => d.GetProperty("source").GetString() == "aquarius").ToArray();
            Assert.True(language.Length == 0, $"{Path.GetRelativePath(examples, file)}: {string.Join("; ", language.Select(d => d.ToString()))}");
            count++;
        }
        Assert.True(count > 0);
    }

    [Fact]
    public void BilingualDeclarationsCompleteHoverResolveDefinitionsAndExposeBothSymbols()
    {
        using var s = Launch();
        string text = "變數 加法, add = 函式(甲,乙){甲+乙;};\n加法(1,2);\nadd(3,4);\n";
        NoDiagnostics(s.Open(text));
        foreach (var (name, line) in new[] { ("加法", 1), ("add", 2) }) {
            Assert.Matches(@"加法 / add\(甲, 乙\)", HoverText(s.Feature("hover", line, 1)));
            Assert.Equal(text.IndexOf(name, StringComparison.Ordinal), Start(s.Feature("definition", line, 1)).Character);
        }
        var items = Labels(s.Feature("completion", 3, 0));
        foreach (string name in new[] { "加法", "add", "len", "長度", "print", "印出", "import", "匯入" }) Assert.Contains(name, items);
        var symbols = s.Symbols().EnumerateArray().ToArray();
        Assert.Contains(symbols, symbol => symbol.GetProperty("name").GetString() == "加法" && symbol.GetProperty("kind").GetInt32() == 12);
        Assert.Contains(symbols, symbol => symbol.GetProperty("name").GetString() == "add" && symbol.GetProperty("kind").GetInt32() == 12);
    }

    [Fact]
    public void BilingualAliasesRespectShadowingAndMalformedDeclarationsProduceDiagnostics()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("變數 中文, english=函式(){1;};\n變數 測試=函式(){變數 中文, english=函式(){2;};\nenglish();};\nenglish();"));
        Assert.Equal(1, Start(s.Feature("definition", 2, 1)).Line);
        Assert.Equal(0, Start(s.Feature("definition", 3, 1)).Line);
        foreach (string text in new[] { "變數 中文,english=1;", "變數 相同,相同=函式(){};", "變數 中文,english,third=函式(){};", "變數 中文,=函式(){};" })
            Assert.True(s.Open(text).GetArrayLength() > 0, text);
    }

    /// <summary>The library modules of the runtime pack, as aquarius --catalog describes them: every module the runtime
    /// registers by name, with its members' English and Chinese names.</summary>
    private static IEnumerable<(string Library, string[] Names, (string[] Names, string Kind)[] Members)> CatalogModules()
    {
        using var catalog = JsonDocument.Parse(NativeRuntime.Catalog());
        foreach (var library in catalog.RootElement.GetProperty("libraries").EnumerateArray())
            foreach (var module in library.GetProperty("modules").EnumerateArray())
                if (module.GetProperty("module").GetBoolean())
                    yield return (library.GetProperty("name").GetString()!, module.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                        module.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(), m.GetProperty("kind").GetString()!)).ToArray());
    }

    [RuntimeFact]
    public void EveryNativeCatalogModuleOffersAllMemberNamesOnceWithoutRunningPrograms()
    {
        var modules = CatalogModules().ToArray();
        Assert.NotEmpty(modules);
        using var s = Launch();
        foreach (var (library, names, members) in modules)
            foreach (string alias in names) {
                if (members.Length == 0) {
                    s.Open($"變數 lib=import(\"{alias}\");\nlib.");
                    Assert.Equal(0, s.Feature("completion", 1, 4).GetArrayLength());
                    continue;
                }
                var first = members[0];
                NoDiagnostics(s.Open($"變數 lib=import(\"{alias}\");\nlib.{first.Names[0]};"), $"{library}.{alias}");
                var items = s.Feature("completion", 1, 4);
                var expected = members.SelectMany(m => m.Names.Select(n => (Name: n, Kind: m.Kind == "function" ? 3 : 5))).ToArray();
                // Single-name members are listed once; bilingual members once per name.
                Assert.Equal(expected.Length, items.GetArrayLength());
                foreach (var (name, kind) in expected) Assert.True(HasItem(items, name, kind), $"{alias}.{name}");
                string hover = HoverText(s.Feature("hover", 1, 4));
                Assert.All(first.Names, name => Assert.Contains(name, hover));
                Assert.Equal(JsonValueKind.Null, s.Feature("definition", 1, 4).ValueKind);
            }
    }

    [RuntimeFact]
    public void AudioNativeModuleCompletesBothLanguagesFromTheRuntimeCatalog()
    {
        using var s = Launch();
        foreach (string module in new[] { "Audio", "音訊" }) {
            NoDiagnostics(s.Open($"變數 音=匯入(\"{module}\");\n音.CreateDevice();"));
            var items = s.Feature("completion", 1, 2);
            foreach (string name in new[] { "CreateDevice", "建立裝置", "CreateOfflineDevice", "建立離線裝置" }) Assert.True(HasItem(items, name, 3), name);
            foreach (string name in new[] { "SampleRate", "取樣率", "Channels", "聲道數" }) Assert.True(HasItem(items, name, 5), name);
            Assert.Equal("CreateDevice / 建立裝置()", HoverText(s.Feature("hover", 1, 3)));
        }
    }

    [RuntimeFact]
    public void ChinesePrefixesAndIncompleteMemberCallsCompleteNativeAliasesAndRespectReceiverShadowing()
    {
        using var s = Launch();
        s.Open("變數 m=匯入(\"音訊\");\nm.建立裝置();\nm.");
        Assert.Equal(new[] { "建立裝置", "建立離線裝置" }, Labels(s.Feature("completion", 1, 4)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "建立裝置" }, Labels(s.Feature("completion", 1, 6)));
        Assert.Matches("CreateDevice", HoverText(s.Feature("hover", 1, 3)));
        Assert.True(HasItem(s.Feature("completion", 2, 2), "建立裝置"));
        // A parameter named like the import shadows it; so does a local import function.
        NoDiagnostics(s.Open("變數 m=匯入(\"音訊\");\n變數 f=函式(m){m.未知;};"));
        Assert.Equal(0, s.Feature("completion", 1, 13).GetArrayLength());
        Assert.Equal(JsonValueKind.Null, s.Feature("hover", 1, 14).ValueKind);
        NoDiagnostics(s.Open("變數 import=函式(路徑){42;};\n變數 m=import(\"音訊\");\nm.未知;"));
        Assert.Equal(0, s.Feature("completion", 2, 2).GetArrayLength());
    }

    [RuntimeFact]
    public void NativeCallResultsHaveNoStaticMembers()
    {
        // The runtime's catalog gives no return types, so the editor no longer chains members through native call
        // results (the old C# catalog inferred WGPU devices, Processing vectors, DynamicLibrary handles, ...).
        using var s = Launch();
        NoDiagnostics(s.Open("變數 音=匯入(\"Audio\");變數 裝置=音.CreateDevice();\n裝置.Play();"));
        Assert.Equal(0, s.Feature("completion", 1, 3).GetArrayLength());
        Assert.Equal(JsonValueKind.Null, s.Feature("hover", 1, 4).ValueKind);
        NoDiagnostics(s.Open("變數 音=匯入(\"Audio\");\n音.CreateDevice().Play();"));
        Assert.Equal(0, s.Feature("completion", 1, 17).GetArrayLength());
    }

    [Fact]
    public void LanguageFactsPortableModulesEqualTheGuestRuntimeTables()
    {
        var installation = AquariusInstallation.Current;
        Assert.True(installation is { IsRepository: true }, "Needs the repository checkout (runtime/guest).");
        var tables = new List<(string[] Names, string[][] Members)>();
        foreach (string header in new[] { "threads.h", "arrays.h", "hashes.h", "texts.h" }) {
            string text = File.ReadAllText(Path.Combine(installation!.Root, "runtime", "guest", header), Encoding.UTF8);
            var module = Regex.Match(text, @"\w+_module_names\[2\]\s*=\s*\{\s*u""([^""]*)""\s*,\s*u""([^""]*)""\s*\}");
            var names = Regex.Match(text, @"\w+_names\[\w+\]\[2\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
            Assert.True(module.Success && names.Success, header);
            var members = Regex.Matches(names.Groups[1].Value, @"\{\s*u""([^""]*)""\s*,\s*u""([^""]*)""\s*\}").Select(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).ToArray();
            Assert.NotEmpty(members);
            tables.Add(([module.Groups[1].Value, module.Groups[2].Value], members));
        }
        Assert.Equal(tables.Count, LanguageFacts.PortableModules.Count);
        foreach (var (table, facts) in tables.Zip(LanguageFacts.PortableModules)) {
            Assert.Equal(table.Names, facts.Names);
            Assert.Equal(table.Members.Select(m => string.Join('|', m)), facts.Members.Select(m => string.Join('|', m.Names)));
        }
    }

    [Fact]
    public void ScriptLibraryAliasesResolveFromDiskAndUnsavedBuffersWithoutExecutingTheLibrary()
    {
        using var t = new BottleDirectory();
        const string library = "變數 加法,add=函式(甲,乙){甲+乙;};\n變數 中文限定=函式(){1;};\n變數 englishOnly=函式(){2;};\n執行檔案(\"MUST_NOT_EXECUTE\",[]);";
        string libraryUri = FileUri(t.Write("工具.aqua", library));
        string mainUri = FileUri(t.FilePath("main.aqua"));
        using var s = Launch();
        NoDiagnostics(s.Open("變數 lib=import(\"工具.aqua\");\nlib.加法(1,2);\nlib.add(3,4);", mainUri));
        var items = Labels(s.Feature("completion", 1, 4, mainUri));
        foreach (string name in new[] { "加法", "add", "中文限定", "englishOnly" }) Assert.Contains(name, items);
        foreach (var (name, line) in new[] { ("加法", 1), ("add", 2) }) {
            Assert.Matches("加法 / add", HoverText(s.Feature("hover", line, 4, mainUri)));
            var definition = s.Feature("definition", line, 4, mainUri);
            Assert.Equal(libraryUri, definition.GetProperty("uri").GetString());
            Assert.Equal(library.IndexOf(name, StringComparison.Ordinal), Start(definition).Character);
        }
        foreach (string directory in new[] { "目前工作目錄", "currWorkingDir" }) {
            NoDiagnostics(s.Open($"變數 路徑={directory}+\"/工具.aqua\";變數 lib=匯入(路徑);\nlib.加法;", mainUri));
            Assert.True(HasItem(s.Feature("completion", 1, 4, mainUri), "add"));
        }
        NoDiagnostics(s.Open("變數 新函式,newFunction=函式(){42;};", libraryUri));
        items = Labels(s.Feature("completion", 1, 4, mainUri));
        Assert.Contains("新函式", items); Assert.Contains("newFunction", items); Assert.DoesNotContain("add", items);
        s.Change(libraryUri, 2, new { text = "變數 更新,updated=函式(){7;};" });
        NoDiagnostics(s.Diagnostics(2, libraryUri));
        items = Labels(s.Feature("completion", 1, 4, mainUri));
        Assert.Contains("更新", items); Assert.DoesNotContain("newFunction", items);
    }

    // A manifest_version 1 project with a version 1 library imported from /bottles.
    private sealed record BottleFixture(BottleDirectory Folder, string Manifest, string ManifestUri, string MainUri, string LibraryUri, string Cache);
    private static BottleFixture Version1Fixture(BottleDirectory t)
    {
        const string manifest = "[project]\nname=\"demo\"\ntype=\"executable\"\nentry=\"main.aqua\"\nsources=[\"main.aqua\"]\nplatforms=[\"web\"]\n[dependencies]\ngreeter={path=\"vendor/greeter\",version=\">= 1.0, < 2.0\"}\n";
        string file = t.Write("aqua.toml", manifest);
        t.Write("vendor/greeter/aqua.toml", "[project]\nname=\"greeter\"\ntype=\"library\"\nversion=\"1.2.0\"\nplatforms=[\"web\"]\n");
        string library = t.Write("vendor/greeter/main.aqua", "變數 問候,greet=函式(){回傳 42;};");
        string main = t.Write("main.aqua", "變數 lib=import(\"/bottles/greeter/main.aqua\");\nlib.greet();");
        return new(t, manifest, FileUri(file), FileUri(main), FileUri(library), t.FilePath("cache"));
    }
    private static Dictionary<string, string> CacheEnvironment(string cache) => new() { ["AQUA_BOTTLE_HOME"] = cache };

    [Fact]
    public void TomlManifestsDiagnoseNamesAndVersionsAndRefreshSourceDiagnosticsOnUnsavedEdits()
    {
        using var t = new BottleDirectory();
        var f = Version1Fixture(t);
        using var s = Launch(CacheEnvironment(f.Cache));
        NoDiagnostics(s.Open(File.ReadAllText(t.FilePath("main.aqua")), f.MainUri));
        var errors = s.Open(f.Manifest.Replace("name=\"demo\"", "name=\"bad name\""), f.ManifestUri);
        Assert.Contains(errors.EnumerateArray(), d => d.GetProperty("message").GetString()!.Contains("whitespace") && Start(d).Line == 1);
        Assert.Contains(Messages(s.Diagnostics(1, f.MainUri)), m => m.Contains("whitespace"));
        var items = Labels(s.Feature("completion", 1, 0, f.ManifestUri));
        foreach (string name in new[] { "platforms", "desktop", "hide_terminal", "library", "native", "modules", "names", "libraries", "android", "permissions" })
            Assert.Contains(name, items);
        s.Change(f.ManifestUri, 2, new { text = f.Manifest });
        NoDiagnostics(s.Diagnostics(2, f.ManifestUri)); NoDiagnostics(s.Diagnostics(1, f.MainUri));
        errors = s.Open("[project]\nname=\"greeter\"\ntype=\"library\"\nplatforms=[\"web\"]", FileUri(t.FilePath("vendor/greeter/aqua.toml")));
        Assert.Contains(Messages(errors), m => m.Contains("version"));
        Assert.False(Directory.Exists(f.Cache), "editor inspection does not write to the global cache");
    }

    [Fact]
    public void BottleImportCompletionHoverAndDefinitionsWorkFromLocalLibrariesBeforeInstallation()
    {
        using var t = new BottleDirectory();
        var f = Version1Fixture(t);
        using var s = Launch(CacheEnvironment(f.Cache));
        NoDiagnostics(s.Open(File.ReadAllText(t.FilePath("main.aqua")), f.MainUri));
        var items = Labels(s.Feature("completion", 1, 4, f.MainUri));
        foreach (string name in new[] { "問候", "greet" }) Assert.Contains(name, items);
        Assert.Matches("greet", HoverText(s.Feature("hover", 1, 5, f.MainUri)));
        Assert.Equal(f.LibraryUri, s.Feature("definition", 1, 5, f.MainUri).GetProperty("uri").GetString());
        Assert.False(File.Exists(t.FilePath("aqua.lock.json")));
    }

    [Fact]
    public void LockedCachedLibrariesRemainNavigableAfterSourceRemovalAndFileWatchersRefreshDiagnostics()
    {
        using var t = new BottleDirectory();
        var f = Version1Fixture(t);
        new BottleManager(new BottleCache(f.Cache)).Restore(ProjectManifest.Load(t.Path));
        Directory.Delete(t.FilePath("vendor"), true);
        using var s = Launch(CacheEnvironment(f.Cache));
        NoDiagnostics(s.Open(File.ReadAllText(t.FilePath("main.aqua")), f.MainUri));
        Assert.Matches("packages/greeter/1.2.0/content/main.aqua$", s.Feature("definition", 1, 5, f.MainUri).GetProperty("uri").GetString()!);
        string corrupt = Path.Combine(f.Cache, "packages", "greeter", "1.2.0", "content", "main.aqua");
        File.WriteAllText(corrupt, "corrupt");
        s.Send("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = FileUri(corrupt), type = 2 } } });
        Assert.Contains(Messages(s.Diagnostics(1, f.MainUri)), m => m.Contains("integrity"));
    }

    // A manifest_version 2 application and the version 2 library it depends on, whose Aquarius-source module has two names.
    private static (string App, string Library, string Module) Version2Fixture(BottleDirectory t)
    {
        t.Write("libs/greeting/aqua.toml", "manifest_version = 2\n\n[library]\nname = \"greeting\"\nversion = \"1.0.0\"\nplatforms = [\"windows\", \"linux\", \"web\", \"android\"]\n\n" +
            "[[modules]]\nnames = \"Greeting|問候語\"\nsource = \"aqua/Greeting.aqua\"\n");
        string module = t.Write("libs/greeting/aqua/Greeting.aqua", "變數 問候, greet = 函式(名) { 回傳 \"你好 \" + 名; };\n變數 版本 = 1;\n");
        t.Write("app/aqua.toml", "manifest_version = 2\n\n[project]\nname = \"app\"\nentry = \"main.aqua\"\nsources = [\"main.aqua\"]\n\n" +
            "[dependencies]\ngreeting = { path = \"../libs/greeting\", version = \">= 1.0, < 2.0\" }\n");
        string main = t.Write("app/main.aqua", "變數 g=匯入(\"Greeting\");\ng.greet(\"x\");\n變數 問=匯入(\"問候語\");\n問.問候(\"y\");\n");
        return (main, t.FilePath("libs/greeting"), module);
    }

    [Fact]
    public void AquariusSourceLibraryModulesCompleteHoverAndJumpByEitherImportName()
    {
        using var t = new BottleDirectory();
        var (main, _, module) = Version2Fixture(t);
        string mainUri = FileUri(main), moduleText = File.ReadAllText(module);
        using var s = Launch(CacheEnvironment(t.FilePath("cache")));
        NoDiagnostics(s.Open(File.ReadAllText(main), mainUri));
        foreach (var (line, member) in new[] { (1, "greet"), (3, "問候") }) {
            // Right after the dot of g. / 問.
            var items = Labels(s.Feature("completion", line, 2, mainUri));
            foreach (string name in new[] { "問候", "greet", "版本" }) Assert.Contains(name, items);
            Assert.Matches("問候 / greet", HoverText(s.Feature("hover", line, 3, mainUri)));
            var definition = s.Feature("definition", line, 3, mainUri);
            Assert.Equal(FileUri(module), definition.GetProperty("uri").GetString());
            Assert.Equal(moduleText.IndexOf(member, StringComparison.Ordinal), Start(definition).Character);
        }
        Assert.False(File.Exists(t.FilePath("app/aqua.lock.json")));
        Assert.False(Directory.Exists(t.FilePath("cache")));
    }

    [Fact]
    public void ProgramsInsideALibraryFolderCompleteTheLibrarysOwnModules()
    {
        using var t = new BottleDirectory();
        var (_, library, _) = Version2Fixture(t);
        string test = t.Write("libs/greeting/tests/case.aqua", "變數 g=匯入(\"問候語\");\ng.greet(\"測試\");\n");
        using var s = Launch(CacheEnvironment(t.FilePath("cache")));
        NoDiagnostics(s.Open(File.ReadAllText(test), FileUri(test)));
        var items = Labels(s.Feature("completion", 1, 2, FileUri(test)));
        foreach (string name in new[] { "問候", "greet", "版本" }) Assert.Contains(name, items);
        Assert.Equal(FileUri(Path.Combine(library, "aqua", "Greeting.aqua")), s.Feature("definition", 1, 3, FileUri(test)).GetProperty("uri").GetString());
    }

    [Fact]
    public void Version2ManifestsDiagnoseLibraryNativeAndModuleTables()
    {
        using var t = new BottleDirectory();
        var (_, library, _) = Version2Fixture(t);
        string manifestUri = FileUri(Path.Combine(library, "aqua.toml"));
        string test = t.Write("libs/greeting/tests/case.aqua", "變數 g=匯入(\"問候語\");\n");
        using var s = Launch(CacheEnvironment(t.FilePath("cache")));
        NoDiagnostics(s.Open(File.ReadAllText(test), FileUri(test)));
        const string head = "manifest_version = 2\n[library]\nname = \"greeting\"\nversion = \"1.0.0\"\nplatforms = [\"web\"]\n";
        int version = 1;
        foreach (var (tables, error) in new[] {
                     ("[native]\nmodules = []\n", "[native] modules must list"),
                     ("[native]\nname = \"Bad-Name\"\nmodules = [\"N\"]\n", "native library name 'Bad-Name'"),
                     ("[[modules]]\nnames = \"Greeting\"\nsource = \"aqua/Missing.aqua\"\n", "Module source does not exist"),
                     ("[[modules]]\nnames = \"Greeting|Greeting\"\nsource = \"aqua/Greeting.aqua\"\n", "duplicate module name 'Greeting'"),
                     ("", "needs [native] modules, [[modules]] or both"),
                 }) {
            JsonElement errors;
            if (version == 1) errors = s.Open(head + tables, manifestUri);
            else { s.Change(manifestUri, version, new { text = head + tables }); errors = s.Diagnostics(version, manifestUri); }
            version++;
            Assert.Contains(Messages(errors), m => m.Contains(error, StringComparison.Ordinal));
            // Sources of the library see the manifest's problem too.
            Assert.Contains(Messages(s.Diagnostics(1, FileUri(test))), m => m.Contains(error, StringComparison.Ordinal));
        }
        s.Change(manifestUri, version, new { text = head + "[[modules]]\nnames = \"Greeting|問候語\"\nsource = \"aqua/Greeting.aqua\"\n" });
        NoDiagnostics(s.Diagnostics(version, manifestUri)); NoDiagnostics(s.Diagnostics(1, FileUri(test)));
        // Version 2 applications have no type, and their declared libraries are each named once.
        string appUri = FileUri(t.FilePath("app/aqua.toml"));
        Assert.Contains(Messages(s.Open("manifest_version = 2\n[project]\nname = \"app\"\ntype = \"executable\"\n", appUri)), m => m.Contains("Unknown project key 'type'"));
        s.Change(appUri, 2, new { text = "manifest_version = 2\n[project]\nname = \"app\"\nlibraries = [\"greeting\", \"Greeting\"]\n" });
        Assert.Contains(Messages(s.Diagnostics(2, appUri)), m => m.Contains("'libraries' lists"));
    }

    [NativeToolchainFact]
    public void ExternalNativeLibrariesCompleteFromTheRuntimeCatalogWithTheirBinary()
    {
        string tiny = NativeToolchain.BuiltTinyLibrary;
        string uri = FileUri(Path.Combine(tiny, "tests", "case.aqua"));
        using var s = Launch();
        foreach (string module in new[] { "TinyNative", "小原生" }) {
            NoDiagnostics(s.Open($"變數 t=匯入(\"{module}\");\nt.twice(1);", uri));
            var items = s.Feature("completion", 1, 2, uri);
            foreach (string name in new[] { "twice", "兩倍", "hello", "問候" }) Assert.True(HasItem(items, name, 3), name);
            Assert.Equal("twice / 兩倍(number)", HoverText(s.Feature("hover", 1, 3, uri)));
        }
    }
}
