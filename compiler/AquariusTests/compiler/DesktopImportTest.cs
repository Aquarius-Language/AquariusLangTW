using AquariusLang.Compiler;

namespace AquariusTests.Compiler;

/// <summary>匯入 / import of packaged scripts. Imports are linked at compile time and the native runtime runs the imported
/// module when the import executes (runtime/host/session.c host_import); the C# DesktopBuiltins 匯入 builtin that read
/// files at run time no longer exists.</summary>
public class DesktopImportTest {
    [Theory]
    [InlineData("變數 = ;")]
    [InlineData("\"unterminated")]
    public void ImportedSyntaxErrorsFailCompilation(string source) {
        var modules = new Dictionary<string, string> { ["main.aqua"] = "匯入(\"錯誤.aqua\");", ["錯誤.aqua"] = source };
        Assert.Throws<CompilationException>(() => WasmTestHost.RunModules(modules, "main.aqua"));
    }

    [RuntimeFact]
    public void ImportPropagatesRuntimeErrorsBeforeLaterSideEffects() {
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "印出(\"before\");變數 b = 匯入(\"bad.aqua\");印出(\"after\");",
            ["bad.aqua"] = "印出(\"module\");未知名稱;印出(\"module after\");",
        }, "main.aqua");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("before", run.Lines[0]);
        Assert.Equal("module", run.Lines[1]);
        Assert.DoesNotContain("after", run.Output);
        // The error is reported once, like any program's error.
        Assert.Equal(["ERROR: Identifier not found: 未知名稱"], run.Lines.Skip(2));
    }

    [RuntimeTheory]
    [InlineData("變數 m = 匯入(\"{0}\");")]
    [InlineData("變數 名稱 = \"{0}\"; 變數 m = 匯入(名稱);")]
    public void MissingImportReturnsALanguageError(string import) {
        // A literal or computed name of a script that is not part of the application fails when the import runs.
        string missing = Guid.NewGuid().ToString("N") + ".aqua";
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "印出(\"start\");" + string.Format(import, missing) + "印出(\"after\");",
        }, "main.aqua");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["start", $"ERROR: 匯入: Packaged script not found: {missing}"], run.Lines);
    }

    [RuntimeFact]
    public void ModuleFunctionsKeepCompiledBodiesAndCanBeReturnedToTheCaller() {
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "變數 模組 = 匯入(\"模組.aqua\"); 變數 增加 = 模組.建立(2); [增加(40), 型別(模組)];",
            ["模組.aqua"] = "變數 建立 = 函式(甲) { 函式(乙) { 甲 + 乙; }; };",
        }, "main.aqua").Succeeded();
        Assert.Equal("[42, module]\n", run.Output);
    }
}
