using System.Reflection;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.token;
using AquariusTests.Packaging;

namespace AquariusTests.CommandLine;

/// <summary>The language examples in examples/ built into applications and run on the native runtime, end to end.</summary>
public class ExamplesTest
{
    private static string Examples => Path.Combine(AquariusInstallation.Require().Root, "examples");

    /// <summary>Builds example sources (relative to examples/, the first is the entry) and runs the application.</summary>
    private static RunResult Run(string root, params string[] sources)
    {
        using var t = new TestFolder();
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile(sources.Select(s => Path.Combine(Examples, root, s)), wasm, Path.Combine(Examples, root));
        return WasmTestHost.RunWasm(wasm);
    }
    private static string Value(RunResult result) => result.Succeeded().Lines[^1];

    [RuntimeTheory]
    [InlineData("for_loop.aqua", "-20")]
    [InlineData("nested_func.aqua", "59")]
    [InlineData("increment.aqua", "7")]
    [InlineData("increment_and_plus_equal.aqua", "真")]
    [InlineData("num_operations_casting.aqua", "[-20, 20.38]")]
    public void SingleFileExamplesProduceTheirResults(string example, string value)
    {
        var result = Run("", example);
        Assert.Equal(value, Value(result));
        if (example == "increment_and_plus_equal.aqua") Assert.Contains("Failed: 0", result.Output);
    }

    [RuntimeFact]
    public void PlatformChecksReportTheDesktopHost()
    {
        bool windows = OperatingSystem.IsWindows(), linux = OperatingSystem.IsLinux();
        string Bool(bool b) => b ? "真" : "假";
        // [Windows, Linux, macOS, Android, web]
        Assert.Equal($"[{Bool(windows)}, {Bool(linux)}, {Bool(OperatingSystem.IsMacOS())}, 假, 假]", Value(Run("", "check_os_platform.aqua")));
    }

    [RuntimeFact]
    public void ExecuteFileExampleReturnsABoolean()
    {
        Assert.Matches("^(真|假)$", Value(Run("", "execute_file.aqua")));
    }

    [RuntimeTheory]
    [InlineData(false)][InlineData(true)]
    public void ModuleImportExampleWorksWithEitherFunctionName(bool chinese)
    {
        using var t = new TestFolder();
        string folder = Path.Combine(Examples, "using_modules");
        string source = File.ReadAllText(Path.Combine(folder, "main.aqua"));
        if (chinese) source = source.Replace("moduleA.add", "moduleA.加法").Replace("moduleA.minus", "moduleA.減法");
        var files = new[] { t.Write("main.aqua", source), t.Write("module_folder/module.aqua", File.ReadAllText(Path.Combine(folder, "module_folder/module.aqua"))) };
        WasmApplication.Compile(files, t.FilePath("app.wasm"), t.Path);
        Assert.Equal("[8, 7]\n[8, 7]\n", WasmTestHost.RunWasm(t.FilePath("app.wasm")).Succeeded().Output);
    }

    [RuntimeFact]
    public void ScriptDirectoryExampleSeesTheApplicationFolder()
    {
        using var t = new TestFolder();
        string folder = Path.Combine(Examples, "dir_paths");
        var files = new[] { t.Write("main.aqua", File.ReadAllText(Path.Combine(folder, "main.aqua"))), t.Write("others/anotherfile.aqua", File.ReadAllText(Path.Combine(folder, "others/anotherfile.aqua"))) };
        WasmApplication.Compile(files, t.FilePath("app.wasm"), t.Path);
        string root = t.Path.Replace('\\', '/');
        Assert.Equal($"[{root}, {root}/others]", Value(WasmTestHost.RunWasm(t.FilePath("app.wasm"))));
    }

    [RuntimeFact]
    public void ArrayBuiltinsHaveChineseAndEnglishNames()
    {
        var values = WasmTestHost.EvalMany(["長度([1, 2, 3]);", "最後一個([1, 2, 3]);", "len([1, 2, 3]);", "last([1, 2, 3]);", "其餘([1, 2, 3]);", "加入([1], 2);"]);
        Assert.Equal(["3", "3", "3", "3", "[2, 3]", "[1, 2]"], values.Select(v => v.Text));
    }

    // ---- The starship expedition: every language feature in Traditional Chinese ---------------------------------

    private static string Starship => Path.Combine(Examples, "starship_expedition");

    [RuntimeFact]
    public void StarshipExpeditionProducesTheExpectedOutput()
    {
        var result = Run("starship_expedition", "main.aqua", "航行工具.aqua");
        string expected = File.ReadAllText(Path.Combine(Starship, "expected-output.txt")).Replace("\r\n", "\n");
        Assert.Equal(expected, result.Succeeded().Output);
        Assert.Equal("", result.Error);
    }

    [RuntimeFact]
    public void StarshipExpeditionReturnsValuesOfTheExpectedTypes()
    {
        // The example's final 回傳 makes it a function body: run it as one and check each result's type.
        string main = File.ReadAllText(Path.Combine(Starship, "main.aqua"));
        var result = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = $"變數 結果 = (函式() {{\n{main}\n}})();\n變數 型別們 = [];\n迴圈 (變數 i = 0; i < 長度(結果); i++) {{ 型別們 = 加入(型別們, 型別(結果[i])); }}\n印出(型別們);\n",
            ["航行工具.aqua"] = File.ReadAllText(Path.Combine(Starship, "航行工具.aqua")),
        }, "main.aqua");
        Assert.Equal("[array, array, array, array, integer, integer, float, double, integer, integer, boolean]", result.Succeeded().Lines[^1]);
    }

    [Fact]
    public void StarshipExpeditionParsesAndCoversEveryLanguageTokenWithChineseIdentifiers()
    {
        var coveredTypes = new HashSet<string>();
        foreach (string file in Directory.GetFiles(Starship, "*.aqua")) {
            string source = File.ReadAllText(file);
            Lexer lexer = Lexer.NewInstance(source);
            Token token;
            do {
                token = lexer.NextToken();
                Assert.NotEqual(TokenType.ILLEGAL, token.Type);
                coveredTypes.Add(token.Type);
                if (token.Type == TokenType.IDENT) Assert.Matches(@"^[㐀-䶿一-鿿_0-9]+$", token.Literal);
            } while (token.Type != TokenType.EOF);
            Assert.Empty(lexer.Errors);
            Parser parser = Parser.NewInstance(Lexer.NewInstance(source));
            parser.ParseAST();
            Assert.Empty(parser.Errors);
        }
        // New syntax must be represented in the comprehensive example as it is added.
        foreach (FieldInfo field in typeof(TokenType).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            string type = (string)field.GetRawConstantValue()!;
            if (type != TokenType.ILLEGAL) Assert.Contains(type, coveredTypes);
        }
    }
}
