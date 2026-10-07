using AquariusLang.Object;
using AquariusLang.VM;
using AquariusREPL;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.VM;

public class DesktopImportTest {
    [Theory]
    [InlineData("變數 = ;")]
    [InlineData("\"unterminated")]
    [InlineData("未知名稱;")]
    public void ImportPropagatesSyntaxAndRuntimeErrors(string source) {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".aqua");
        try {
            File.WriteAllText(path, source);
            using var builtins = new DesktopBuiltins();
            var result = builtins.BuiltinFuncs["匯入"].Fn(new IObject[] { new StringObj(path) });
            Assert.IsType<ErrorObj>(result);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void MissingImportReturnsALanguageError() {
        using var builtins = new DesktopBuiltins();
        Assert.IsType<ErrorObj>(builtins.BuiltinFuncs["匯入"].Fn(new IObject[] {
            new StringObj(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".aqua"))
        }));
    }

    [Fact]
    public void ModuleFunctionsKeepCompiledBodiesAndCanBeReturnedToTheCaller() {
        var environment = AquaEnvironment.NewEnvironment();
        var module = AquaEnvironment.NewEnvironment();
        var evaluator = VmEvaluator.NewInstance(new AquariusLang.evaluator.Builtins());
        evaluator.Evaluate("變數 建立 = 函式(甲) { 函式(乙) { 甲 + 乙; }; };", module);
        environment.Create("模組", new ModuleObj(module));
        var result = evaluator.Evaluate("變數 增加 = 模組.建立(2); 增加(40);", environment);
        Assert.Equal(42, Assert.IsType<IntegerObj>(result).Value);
    }
}
