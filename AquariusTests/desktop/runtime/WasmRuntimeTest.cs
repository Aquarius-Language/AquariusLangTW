using AquariusLang.Object;
using AquariusLang.Compiler;

namespace AquariusLang.Desktop.runtime;

public class WasmApplicationRuntimeTest {
    [Theory]
    [InlineData("匯入(\"test/module.aqua\")")]
    [InlineData("匯入(目前工作目錄 + \"/test/module.aqua\")")]
    public void RelocatedWasmImportsCompiledModulesAfterAllSourcesAreDeleted(string import) {
        using var temp = new ApplicationTestDirectory();
        string main = temp.Write("source/main.aqua", $"變數 m = {import}; m.加(40);");
        string module = temp.Write("source/test/module.aqua", "變數 加 = 函式(x) { x+2; };");
        string wasm = temp.FilePath("source/main.wasm");
        WasmApplication.Compile(new[] { main, module }, wasm, temp.FilePath("source"));
        Directory.CreateDirectory(temp.FilePath("deployment"));
        string relocated = temp.FilePath("deployment/main.wasm");
        File.Move(wasm, relocated);
        Directory.Delete(temp.FilePath("source"), true);
        Assert.Equal(42, Assert.IsType<IntegerObj>(ScriptRunner.RunFile(relocated)).Value);
        Assert.Equal(new[] { "main.wasm" }, Directory.GetFiles(temp.FilePath("deployment")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(temp.FilePath("deployment"))); // Nothing is extracted.
    }

    [Fact]
    public void ModuleFunctionsAndReturnedClosuresKeepTheirOwnDirectoriesForDeferredImports() {
        using var temp = new ApplicationTestDirectory();
        var files = new[] {
            temp.Write("main.aqua", "變數 m=匯入(\"lib/module.aqua\"); 變數 f=m.建立(); [m.讀取(), f(), 目前工作目錄];"),
            temp.Write("lib/module.aqua", "變數 讀取=函式() { 變數 c=匯入(目前工作目錄 + \"/nested/child.aqua\"); c.值; }; 變數 建立=函式() { 函式() { 變數 c=匯入(\"nested/child.aqua\"); c.值; }; };"),
            temp.Write("lib/nested/child.aqua", "變數 p=匯入(\"../sibling.aqua\"); 變數 值=p.值+2;"),
            temp.Write("lib/sibling.aqua", "變數 值=40;")
        };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        foreach (var file in files) File.Delete(file);
        var values = Assert.IsType<ArrayObj>(ScriptRunner.RunWasm(wasm)).Elements;
        Assert.Equal(42, Assert.IsType<IntegerObj>(values[0]).Value);
        Assert.Equal(42, Assert.IsType<IntegerObj>(values[1]).Value);
        Assert.Equal(temp.Path, Assert.IsType<StringObj>(values[2]).Value);
    }

    [Theory]
    [InlineData("匯入(\"missing.aqua\");", "not found")]
    [InlineData("匯入(\"../outside.aqua\");", "Invalid relative")]
    [InlineData("匯入(42);", "not STRING")]
    [InlineData("匯入(\"file.txt\");", "require an .aqua module")]
    [InlineData("匯入(\"main.aqua\");", "Circular")]
    public void ImportFailuresAreLanguageErrors(string source, string message) {
        using var temp = new ApplicationTestDirectory();
        string main = temp.Write("main.aqua", source), wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(new[] { main }, wasm, temp.Path);
        Assert.Contains(message, Assert.IsType<ErrorObj>(ScriptRunner.RunWasm(wasm)).Message);
    }

    [Fact]
    public void CircularImportsAcrossModulesReportAnError() {
        using var temp = new ApplicationTestDirectory();
        var files = new[] { temp.Write("main.aqua", "匯入(\"lib/module.aqua\");"), temp.Write("lib/module.aqua", "匯入(\"../main.aqua\");") };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        Assert.Contains("Circular", Assert.IsType<ErrorObj>(ScriptRunner.RunWasm(wasm)).Message);
    }

    [Fact]
    public void ImportedRuntimeErrorsPropagateAndScriptsKeepIndependentGlobals() {
        using var temp = new ApplicationTestDirectory();
        string main = temp.Write("main.aqua", "變數 a=匯入(\"module.aqua\"); 變數 b=匯入(\"module.aqua\"); a.增加(); b.值;");
        string module = temp.Write("module.aqua", "變數 值=40; 變數 增加=函式() { 值++; };"), wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(new[] { main, module }, wasm, temp.Path);
        Assert.Equal(40, Assert.IsType<IntegerObj>(ScriptRunner.RunWasm(wasm)).Value);
        File.WriteAllText(module, "不存在;");
        WasmApplication.Compile(new[] { main, module }, wasm, temp.Path);
        Assert.Contains("Identifier not found", Assert.IsType<ErrorObj>(ScriptRunner.RunWasm(wasm)).Message);
    }

    [Fact]
    public void SelectedEntryRunsWithItsOwnDirectoryAndEachRunStartsFresh() {
        using var temp = new ApplicationTestDirectory();
        var files = new[] { temp.Write("main.aqua", "1;"), temp.Write("lib/second.aqua", "變數 n=40; n+=2; [n, 目前工作目錄];") };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        for (int i = 0; i < 2; i++) {
            var values = Assert.IsType<ArrayObj>(ScriptRunner.RunWasm(wasm, "lib/second.aqua")).Elements;
            Assert.Equal(42, Assert.IsType<IntegerObj>(values[0]).Value);
            Assert.Equal(temp.FilePath("lib"), Assert.IsType<StringObj>(values[1]).Value);
        }
        Assert.IsType<ErrorObj>(ScriptRunner.RunWasm(wasm, "missing.aqua"));
    }

    [Fact]
    public void NativeMathModuleAndCallbacksWorkWithLoadedFunctions() {
        using var temp = new ApplicationTestDirectory();
        string source = temp.Write("main.aqua", "變數 m=匯入(\"GLM\"); m.Sqrt(1764);");
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(new[] { source }, wasm, temp.Path);
        Assert.Equal(42d, Assert.IsType<DoubleObj>(ScriptRunner.RunWasm(wasm)).Value);
        var functionProgram = new AquariusLang.Wasm.WasmCompiler().Compile("函式(x) { x+2; };");
        var vm = new AquariusLang.Wasm.WasmRuntime(engine:new WasmtimeEngine());
        var function = vm.Execute(AquariusLang.Wasm.WasmProgram.Load(functionProgram.Bytes.ToArray()));
        Assert.Equal(42, Assert.IsType<IntegerObj>(vm.Invoke(function, new IntegerObj(40))).Value);
    }

    [Fact]
    public void PackagedModulesShareTheNativeRuntimeWithTheEntryScript() {
        using var temp = new ApplicationTestDirectory();
        var files = new[] {
            temp.Write("main.aqua", "變數 m=匯入(\"lib/module.aqua\"); [匯入(\"GLM\"), m.取得()];"),
            temp.Write("lib/module.aqua", "變數 取得=函式() { 匯入(\"GLM\"); };")
        };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        var values = Assert.IsType<ArrayObj>(ScriptRunner.RunWasm(wasm)).Elements;
        Assert.Same(Assert.IsType<ModuleObj>(values[0]), Assert.IsType<ModuleObj>(values[1]));
    }

    [Fact]
    public void StarshipExampleRunsFromLoweredProgramWithTheSameResults() {
        using var temp = new ApplicationTestDirectory();
        string examples = Path.Combine(AppContext.BaseDirectory, "examples/starship_expedition");
        string main = Path.Combine(examples, "main.aqua"), module = Path.Combine(examples, "航行工具.aqua");
        string wasm = temp.FilePath("starship.wasm");
        WasmApplication.Compile(new[] { main, module }, wasm, examples);
        Assert.Equal(ScriptRunner.RunFile(main).Inspect(), ScriptRunner.RunWasm(wasm).Inspect());
    }
}
