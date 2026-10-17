using System.Text.Json;
using AquariusLang.Desktop.runtime;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop;

namespace AquariusTests.Wasm;

/// <summary>Shared JSON cases (tests/*.json) for the portable Hash and String modules and the % operator.</summary>
public class PortableModulesTest {
    private static string Root {
        get { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path != null && !File.Exists(Path.Combine(path.FullName, "AquariusLang.sln"))) path = path.Parent; return path!.FullName; }
    }
    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", name + ".json")));
    private static string Translate(string source, string english, string chinese, IReadOnlyList<LibraryFunction> functions) {
        foreach (var function in functions.OrderByDescending(f => f.EnglishName.Length))
            source = source.Replace($"{english}.{function.EnglishName}(", $"{chinese}.{function.TraditionalChineseName}(");
        return source.Replace($"{english}=={chinese}", $"{chinese}=={english}");
    }
    public static IEnumerable<object[]> ValidCases() {
        foreach (var (file, english, chinese, functions) in new[] { ("hash-library", "Hash", "雜湊", HashLibrary.Functions), ("string-library", "String", "字串", StringLibrary.Functions) }) {
            using var json = Load(file);
            foreach (var item in json.RootElement.GetProperty("valid").EnumerateArray()) {
                string name = item[0].GetString()!, source = item[1].GetString()!, expected = item[2].GetString()!;
                yield return new object[] { $"{english}: {name} English", source, expected };
                yield return new object[] { $"{english}: {name} 繁體中文", Translate(source, english, chinese, functions), expected };
            }
        }
        using var modulo = Load("modulo-operator");
        foreach (var item in modulo.RootElement.GetProperty("valid").EnumerateArray())
            yield return new object[] { "%: " + item[0].GetString()!, item[1].GetString()!, item[2].GetString()! };
    }
    public static IEnumerable<object[]> InvalidCases() {
        foreach (var file in new[] { "hash-library", "string-library", "modulo-operator" }) {
            using var json = Load(file);
            foreach (var item in json.RootElement.GetProperty("invalid").EnumerateArray()) yield return new object[] { item.GetString()! };
        }
    }
    [Theory, MemberData(nameof(ValidCases))]
    public void PortableOperations(string name, string source, string expected) {
        using var runtime = new WasmRuntime(new Builtins());
        Assert.True(expected == runtime.Execute(new WasmCompiler().Compile(source)).Inspect(), name);
    }
    [Theory, MemberData(nameof(InvalidCases))]
    public void InvalidOperationsReportErrors(string source) {
        var scope = AquariusLang.Object.Environment.NewEnvironment(); scope.Create("a", new ArrayObj(new IObject[] { new IntegerObj(1) }));
        using var runtime = new WasmRuntime(new Builtins());
        Assert.IsType<ErrorObj>(runtime.Execute(new WasmCompiler().Compile(source), scope));
    }
    [Theory, InlineData("Hash", "雜湊", 10), InlineData("String", "字串", 16)]
    public void ImportAndCatalogExposeBothNames(string english, string chinese, int count) {
        using var runtime = new WasmRuntime(new DesktopBuiltins());
        Assert.Equal("真", runtime.Execute(new WasmCompiler().Compile($"匯入(\"{english}\")==匯入(\"{chinese}\");")).Inspect());
        Assert.Equal(count, LibraryCatalog.Members(english).Count());
        Assert.Equal(count, LibraryCatalog.Members(chinese).Count());
        Assert.Equal("1", runtime.Execute(new WasmCompiler().Compile("變數 文=匯入(\"字串\");變數 表=匯入(\"雜湊\");文.轉文字(表.長度({\"a\":1}));")).Inspect());
        Assert.Equal("[2, 2]", runtime.Execute(new WasmCompiler().Compile("[長度({\"a\":1,\"b\":2}),len({1:真,2:假})];")).Inspect());
    }
    [Fact]
    public void PortableTokensSurviveHostTransport() {
        var builtins = new Builtins(); builtins.BuiltinFuncs["identity"] = new BuiltinObj(args => args[0]);
        using var runtime = new WasmRuntime(builtins);
        Assert.Equal("[[a], 5]", runtime.Execute(new WasmCompiler().Compile("變數 keys=identity(Hash.Keys);變數 text=identity(String.From);[keys({\"a\":1}),text(5)];")).Inspect());
    }
    [Fact]
    public void RetainedHostHashReceivesDeletions() {
        var hash = new HashObj(new Dictionary<HashKey, HashPair> {
            [new StringObj("a").HashKey()] = new(new StringObj("a"), new IntegerObj(1)),
            [new StringObj("b").HashKey()] = new(new StringObj("b"), new IntegerObj(2))
        });
        var scope = AquariusLang.Object.Environment.NewEnvironment(); scope.Create("h", hash);
        var builtins = new Builtins(); builtins.BuiltinFuncs["check"] = new BuiltinObj(_ => new StringObj(hash.Inspect()));
        using var runtime = new WasmRuntime(builtins);
        Assert.Equal("{b: 2, c: 3}", runtime.Execute(new WasmCompiler().Compile("雜湊.刪除鍵(h,\"a\");h[\"c\"]=3;check();"), scope).Inspect());
    }
}
