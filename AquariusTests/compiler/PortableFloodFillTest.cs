using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;
using AquariusLang.Desktop;

namespace AquariusTests.Wasm;

public class PortableFloodFillTest
{
    [Theory]
    [InlineData("變數 a=[0,1,0,0,1,0,1,0,0];[區域填色(a,3,3,0,0,4294967295.0d),a];", "[2, [4294967295, 1, 0, 4294967295, 1, 0, 1, 0, 0]]")]
    [InlineData("變數 a=[0,0,0,0];變數 alias=a;變數 n=floodFill(a,2,2,1,1,7);[n,alias,floodFill(a,2,2,0,0,7)];", "[4, [7, 7, 7, 7], 0]")]
    [InlineData("變數 a=[0,0,0,0];變數 b=加入(a,9);區域填色(a,2,2,0,0,7);[a,b];", "[[7, 7, 7, 7], [0, 0, 0, 0, 9]]")]
    public void DesktopRunsTheSamePortableAlgorithm(string source, string expected)
    {
        using var runtime = new WasmRuntime(new DesktopBuiltins());
        Assert.Equal(expected, runtime.Execute(new WasmCompiler().Compile(source)).Inspect());
    }

    [Theory]
    [InlineData("區域填色([0],2,2,0,0,7);")]
    [InlineData("區域填色([0],1,1,-1,0,7);")]
    [InlineData("區域填色([0],1,1,0,0,\"bad\");")]
    public void InvalidArgumentsReturnALanguageError(string source)
    {
        using var runtime = new WasmRuntime(new Builtins());
        Assert.Contains("floodFill", runtime.Execute(new WasmCompiler().Compile(source)).Inspect());
    }
}
