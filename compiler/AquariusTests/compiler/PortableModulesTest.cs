namespace AquariusTests.Wasm;

/// <summary>Shared JSON cases (tests/*.json) for the portable Hash and String modules and the % operator.</summary>
public class PortableModulesTest {
    private static string Translate(string source, string english) {
        var module = ArrayLibraryTest.Module(english);
        string chinese = module.Chinese;
        foreach (var function in module.Functions.OrderByDescending(f => f.English.Length))
            source = source.Replace($"{english}.{function.English}(", $"{chinese}.{function.Chinese}(");
        return source.Replace($"{english}=={chinese}", $"{chinese}=={english}");
    }

    [RuntimeFact]
    public void PortableOperations() {
        var cases = new List<(string, string, string)>();
        foreach (var (file, english) in new[] { ("hash-library", "Hash"), ("string-library", "String") }) {
            using var json = ArrayLibraryTest.Vectors(file);
            foreach (var item in json.RootElement.GetProperty("valid").EnumerateArray()) {
                string source = item[1].GetString()!, expected = item[2].GetString()!;
                cases.Add((source, ArrayLibraryTest.TypeOf(expected), expected));
                cases.Add((Translate(source, english), ArrayLibraryTest.TypeOf(expected), expected));
            }
        }
        using var modulo = ArrayLibraryTest.Vectors("modulo-operator");
        foreach (var item in modulo.RootElement.GetProperty("valid").EnumerateArray())
            cases.Add((item[1].GetString()!, ArrayLibraryTest.TypeOf(item[2].GetString()!), item[2].GetString()!));
        Assert.Equal(2 * (12 + 15) + 7, cases.Count);
        Assert.Contains(cases, c => c.Item1.Contains("雜湊.項目列表(") );
        Assert.Contains(cases, c => c.Item1.Contains("字串.解析整數("));
        LanguageCases.Expect(cases.ToArray());
    }

    [RuntimeFact]
    public void InvalidOperationsReportErrors() {
        var sources = new List<string>();
        foreach (var file in new[] { "hash-library", "string-library", "modulo-operator" }) {
            using var json = ArrayLibraryTest.Vectors(file);
            sources.AddRange(json.RootElement.GetProperty("invalid").EnumerateArray().Select(i => "變數 a=[1];" + i.GetString()!));
        }
        Assert.Equal(6 + 8 + 3, sources.Count);
        LanguageCases.ExpectErrors(sources.ToArray());
    }

    [RuntimeFact(Skip = RuntimeBug.PortableImportIdentity)]
    public void ImportAndNamesExposeBothLanguages() {
        var cases = new List<(string, string, string)>();
        foreach (var (english, chinese, count) in new[] { ("Hash", "雜湊", 10), ("String", "字串", 16) }) {
            var module = ArrayLibraryTest.Module(english);
            Assert.Equal(chinese, module.Chinese);
            Assert.Equal(count, module.Functions.Length);
            cases.Add(($"[匯入(\"{english}\")==匯入(\"{chinese}\"),匯入(\"{chinese}\")=={english},型別({chinese})];", "array", "[真, 真, module]"));
            // Every function answers to its English and Chinese name in either module name.
            string checks = string.Join(",", module.Functions.Select(f =>
                $"{english}.{f.English}=={chinese}.{f.Chinese} && {chinese}.{f.English}=={english}.{f.Chinese} && 型別({english}.{f.English})==\"function\""));
            cases.Add(($"[{checks}];", "array", "[" + string.Join(", ", Enumerable.Repeat("真", count)) + "]"));
        }
        cases.Add(("變數 文=匯入(\"字串\");變數 表=匯入(\"雜湊\");文.轉文字(表.長度({\"a\":1}));", "string", "1"));
        cases.Add(("[長度({\"a\":1,\"b\":2}),len({1:真,2:假})];", "array", "[2, 2]"));
        LanguageCases.Expect(cases.ToArray());
    }

    [RuntimeFact]
    public void PortableFunctionsSurviveTransport() => LanguageCases.Expect(
        ("變數 identity=函式(f){f;};變數 keys=identity(Hash.Keys);變數 text=identity(String.From);[keys({\"a\":1}),text(5)];", "array", "[[a], 5]"));

    [RuntimeFact]
    public void RetainedHashReceivesDeletions() => LanguageCases.Expect(
        // A closure keeps the hash; deletions and indexed writes through another name reach it.
        ("變數 h={\"a\":1,\"b\":2};變數 check=(函式(kept){函式(){kept;};})(h);雜湊.刪除鍵(h,\"a\");h[\"c\"]=3;check();", "hash", "{b: 2, c: 3}"));
}
