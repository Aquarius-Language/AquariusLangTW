using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Runtime errors name the file, line and column, show the source line with the failing part underlined and
/// print the call stack (runtime/host/diagnostics.c, from the compiler's aquarius.debug section, WasmDebugInfo). The first
/// line stays "ERROR: message" on standard output; the rest goes to standard error.</summary>
public class RuntimeErrorDiagnosticsTest {
    private static RunResult Failing(string source, IReadOnlyDictionary<string, string>? environment = null) {
        var run = WasmTestHost.Run(source, environment);
        Assert.Equal(1, run.ExitCode);
        return run;
    }
    private static RunResult FailingModules(Dictionary<string, string> modules, string entry) {
        var run = WasmTestHost.RunModules(modules, entry);
        Assert.Equal(1, run.ExitCode);
        return run;
    }

    [RuntimeFact]
    public void AMissingModuleMemberIsUnderlinedAtItsName() {
        var run = Failing("var f = function(name) {\n    var n = String.Length(name);\n    return n;\n};\nf(\"abc\");\n");
        Assert.Equal("ERROR: Module member not found: Length\n", run.Output);
        Assert.Equal(
            " --> main.aqua:2:20\n" +
            "  |\n" +
            "2 |     var n = String.Length(name);\n" +
            "  |                    ~~~~~~\n" +
            "  at f (main.aqua:2)\n" +
            "  at <top level> (main.aqua:5)\n", run.Error);
    }

    [RuntimeTheory]
    // Undefined variable.
    [InlineData("var x = 1;\nprint(x + nope);\n", "Identifier not found: nope", 2, 11, "          ~~~~")]
    // Type error: the whole operation.
    [InlineData("var s = \"a\";\nvar y = s - 1;\n", "Type mismatch: STRING - INTEGER", 2, 9, "        ~~~~~")]
    // Wrong number of arguments: the whole call.
    [InlineData("var add = function(a, b) { return a + b; };\nadd(1, 2, 3);\n", "Function expects 2 arguments, got 3.", 2, 1, "~~~~~~~~~~~~")]
    // Not a function.
    [InlineData("var n = 3;\nn(1);\n", "Not a function: INTEGER", 2, 1, "~~~~")]
    // Index out of range (an array write).
    [InlineData("var a = [1, 2];\na[5] = 3;\n", "Array assignment index is out of bounds.", 2, 1, "~~~~~~~~")]
    // Division by zero in generic code.
    [InlineData("var d = function(a, b) { return a / b; };\nd(1, 0);\n", "Integer division by zero / 整數不能除以零。", 1, 33, "                                ~~~~~")]
    // Division by zero in a fully typed function (the native tier: its error site, not a frame).
    [InlineData("var d = function(a: 整數, b: 整數): 整數 { return a / b; };\nprint(d(1, 0));\n", "Integer division by zero / 整數不能除以零。", 1, 45, "                                                  ~~~~~")]
    // A member of null.
    [InlineData("var p = null;\nprint(p.x);\n", "Cannot access member of NULL", 2, 9, "        ~")]
    // An error raised by a native library function (here the core library's argument check).
    [InlineData("execFile(42);\n", "globals.execFile expects 2 arguments, got 1.", 1, 1, "~~~~~~~~~~~~")]
    // A user-raised error: a rejected promise that is awaited.
    [InlineData("var job = async function() {\n    await Promise.Reject(\"boom\");\n};\nawait job();\n", "boom", 2, 5, "    ~~~~~~~~~~~~~~~~~~~~~~~~~~~~")]
    // A struct's unknown field.
    [InlineData("struct Point { x, y }\nvar p = Point(x: 1, y: 2);\nprint(p.z);\n", "Unknown field of Point: z", 3, 9, "        ~")]
    public void EveryKindOfErrorPointsAtItsSource(string source, string message, int line, int column, string marks) {
        var run = Failing(source);
        Assert.Equal($"ERROR: {message}\n", run.Output);
        string[] lines = run.Error.Split('\n');
        Assert.Equal($" --> main.aqua:{line}:{column}", lines[0]);
        Assert.Equal($"{line} | {source.Split('\n')[line - 1]}", lines[2]);
        Assert.Equal("  | " + marks, lines[3]);
    }

    [RuntimeFact]
    public void TabsAndWideCharactersKeepTheMarksUnderTheText() {
        // A tab is 4 columns; each Chinese character takes two.
        var run = Failing("變數 f = 函式(x) {\n\t\t回傳 x.欄位;\n};\nf(5);\n");
        Assert.Equal(
            " --> main.aqua:2:8\n" +
            "  |\n" +
            "2 |         回傳 x.欄位;\n" +
            "  |                ~~~~\n" +
            "  at f (main.aqua:2)\n" +
            "  at <top level> (main.aqua:4)\n", run.Error);
        var wide = Failing("變數 名字 = \"水瓶\";\n變數 結果 = 名字 * 名字;\n");
        Assert.Contains("2 | 變數 結果 = 名字 * 名字;\n  |             ~~~~~~~~~~~\n", wide.Error);
    }

    [RuntimeFact]
    public void AnExpressionOverSeveralLinesIsUnderlinedOnItsFirstLine() {
        var run = Failing("var total = function(a, b) {\n    return a +\n        b;\n};\ntotal(1, true);\n");
        Assert.Equal("ERROR: Type mismatch: INTEGER + BOOLEAN\n", run.Output);
        Assert.StartsWith(
            " --> main.aqua:2:12\n" +
            "  |\n" +
            "2 |     return a +\n" +
            "  |            ~~~... (continues to line 3)\n", run.Error);
    }

    [RuntimeFact]
    public void TheCallStackNamesEveryFunctionAndCollapsesRecursion() {
        var run = Failing("var deep = function(n) {\n    if (n == 0) { return nope; }\n    return deep(n - 1);\n};\nvar start = function() { return deep(300); };\nstart();\n");
        Assert.EndsWith(
            "  at deep (main.aqua:2)\n" +
            "  at deep (main.aqua:3)\n" +
            "  ... the frame above repeats 299 more times\n" +
            "  at start (main.aqua:5)\n" +
            "  at <top level> (main.aqua:6)\n", run.Error);
    }

    [RuntimeFact]
    public void CallbacksOfBuiltInsShowTheBuiltInAndAnonymousFunctions() {
        var run = Failing("var r = Array.Map([1, 2], function(v) { return v.size; });\n");
        Assert.Contains("  at <anonymous> (main.aqua:1)\n", run.Error);
        Assert.Contains("(built-in)\n", run.Error);
        Assert.EndsWith("  at <top level> (main.aqua:1)\n", run.Error);
    }

    [RuntimeFact]
    public void ErrorsInImportedModulesContinueTheirImportersCallStack() {
        var run = FailingModules(new Dictionary<string, string> {
            ["main.aqua"] = "print(\"start\");\nvar lib = import(\"lib/helper.aqua\");\n",
            ["lib/helper.aqua"] = "var helper = function() {\n    return 1 + nope;\n};\nhelper();\n",
        }, "main.aqua");
        Assert.Equal("start\nERROR: Identifier not found: nope\n", run.Output);
        Assert.StartsWith(" --> lib/helper.aqua:2:16\n", run.Error);
        Assert.EndsWith(
            "  at helper (lib/helper.aqua:2)\n" +
            "  at <top level> (lib/helper.aqua:4)\n" +
            "  at <top level> (main.aqua:2)\n", run.Error);
    }

    [RuntimeFact]
    public void AFailedImportPointsAtTheImport() {
        var run = FailingModules(new Dictionary<string, string> { ["main.aqua"] = "var m = import(\"missing.aqua\");\n" }, "main.aqua");
        Assert.Equal("ERROR: 匯入: Packaged script not found: missing.aqua\n", run.Output);
        Assert.StartsWith(" --> main.aqua:1:9\n  |\n1 | var m = import(\"missing.aqua\");\n  |         ~~~~~~~~~~~~~~~~~~~~~~\n", run.Error);
    }

    [RuntimeFact]
    public void ModulesThatRunAgainOnImportReportTheirOwnLines() {
        // A module's body runs again on every import: functions of the second instance still point into the module.
        var run = FailingModules(new Dictionary<string, string> {
            ["main.aqua"] = "var a = import(\"m.aqua\");\nvar b = import(\"m.aqua\");\nb.Fail(2);\n",
            ["m.aqua"] = "var Fail = function(n) {\n    return n.missing;\n};\n",
        }, "main.aqua");
        Assert.Equal("ERROR: Cannot access member of INTEGER\n", run.Output);
        Assert.Equal(
            " --> m.aqua:2:14\n" +
            "  |\n" +
            "2 |     return n.missing;\n" +
            "  |              ~~~~~~~\n" +
            "  at Fail (m.aqua:2)\n" +
            "  at <top level> (main.aqua:3)\n", run.Error);
    }

    [RuntimeFact]
    public void ColoursOnlyWhenWantedAndNeverWithNoColor() {
        string source = "var x = nope;\n";
        var plain = Failing(source, new Dictionary<string, string> { ["NO_COLOR"] = "1" });
        Assert.DoesNotContain("\u001b[", plain.Output + plain.Error);
        // Redirected output has no colours either (the tests' own case).
        var redirected = Failing(source);
        Assert.DoesNotContain("\u001b[", redirected.Output + redirected.Error);
        var forced = Failing(source, new Dictionary<string, string> { ["AQUARIUS_COLOR"] = "1" });
        Assert.StartsWith("\u001b[1;31mERROR:\u001b[0m Identifier not found: nope\n", forced.Output);
        Assert.Contains("\u001b[1;31m~~~~\u001b[0m", forced.Error);
    }

    [RuntimeFact]
    public void WithoutDebugInfoOnlyTheMessageIsPrinted() {
        bool saved = WasmCompiler.DebugInfo;
        WasmCompiler.DebugInfo = false;
        try {
            var program = new WasmCompiler().Compile("var x = nope;\n");
            Assert.Null(program.CustomSection(WasmDebugInfo.SectionName));
            var run = WasmTestHost.Run(program);
            Assert.Equal("ERROR: Identifier not found: nope\n", run.Output);
            Assert.Equal("", run.Error);
        } finally { WasmCompiler.DebugInfo = saved; }
        Assert.NotNull(new WasmCompiler().Compile("1;").CustomSection(WasmDebugInfo.SectionName));
    }

    [Fact]
    public void CompileErrorsShowTheLineToo() {
        string dir = WasmTestHost.Directory(), file = Path.Combine(dir, "bad.aqua");
        File.WriteAllText(file, "變數 a = 1;\n\t變數 b = (1 + ;\n");
        var error = Assert.Throws<CompilationException>(() => WasmApplication.Compile([file], Path.Combine(dir, "bad.wasm"), dir));
        Assert.Contains($"{file}:2:14: No prefix parse function for ;(;) found.\n  |\n2 |     變數 b = (1 + ;\n  |                   ~\n", error.Message);
    }
}
