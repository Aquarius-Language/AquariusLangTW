using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;

namespace AquariusTests.Compiler;

public class LanguageAdditionsTest {
    [Fact]
    public void 型別回傳穩定的英文型別標記() {
        using var runtime = new WasmRuntime();
        var result = runtime.Execute(new WasmCompiler().Compile("""
            [型別(1), 型別(1.5f), 型別(2.5d), 型別("文"), 型別(真), 型別({}["無"]), 型別([1]), 型別({"a": 1}),
             型別(函式() { 1; }), typeOf(型別), 型別([1][5])];
            """));
        Assert.Equal("[integer, float, double, string, boolean, null, array, hash, function, function, null]", result.Inspect());
        Assert.IsType<ErrorObj>(runtime.Execute(new WasmCompiler().Compile("typeOf();")));
    }

    [Fact]
    public void MemberResultsCanBeIndexedWithoutParentheses() {
        using var builtins = new DesktopBuiltins();
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile("""
            變數 網路 = 匯入("網路");
            變數 表 = {"模組": 網路};
            [網路.能力()["http"], 網路.Capabilities()["串流傳輸"], 字串.分割("甲,乙,丙", ",")[1], 字串.分割("a-b", "-")[0], 表["模組"].支援("http")];
            """));
        Assert.Equal("[真, 真, 乙, a, 真]", result.Inspect());
    }
}
