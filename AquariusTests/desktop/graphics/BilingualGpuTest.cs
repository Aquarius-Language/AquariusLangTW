using AquariusLang.Object;
using AquariusLang.Compiler;
using Xunit;

namespace AquariusLang.Desktop.Graphics;

[Collection("Starship console output")]
public class BilingualGpuTest {
    [WgpuFact] public void WgpuReturnedObjectsShareAliasesAndGpuState() {
        using var runtime = new GraphicsRuntime(); using var builtins = new DesktopBuiltins(runtime);
        var objects = Assert.IsType<ArrayObj>(new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile($$"""
            變數 g=匯入("WGPU");變數 d=g.建立裝置();變數 b=d.建立緩衝區([1,2]);
            變數 shader=d.建立著色器("{{WgpuShaders.Vertex}}","{{WgpuShaders.Fragment}}");
            變數 target=d.建立繪圖目標(3,2);[d,b,shader,target];
            """))).Elements;
        foreach (var (value, library) in objects.Zip(new[] { "WGPU.Device", "WGPU.Buffer", "WGPU.Shader", "WGPU.Target" }))
            BilingualLibraryTest.AssertAliases(Assert.IsType<ModuleObj>(value), library);
        var buffer = Assert.IsType<ModuleObj>(objects[1]);
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(buffer, "寫入", new ArrayObj(new IObject[] { new IntegerObj(7), new IntegerObj(9) })));
        Assert.Equal("[7, 9]", TextInputTest.Call(buffer, "Read").Inspect());
        var target = Assert.IsType<ModuleObj>(objects[3]);
        TextInputTest.Call(target, "清除", new ArrayObj(new IObject[] { new IntegerObj(1), new IntegerObj(0), new IntegerObj(0), new IntegerObj(1) }));
        var pixels = Assert.IsType<ArrayObj>(TextInputTest.Call(target, "ReadPixels"));
        Assert.Equal(6, pixels.Elements.Length); Assert.All(pixels.Elements, p => Assert.Equal(0xFFFF0000, GraphicsRuntime.Number(p)));
        TextInputTest.Call(buffer, "釋放"); Assert.IsType<ErrorObj>(TextInputTest.Call(buffer, "Read"));
    }

    [WgpuFact] public void ProcessingCanvasesAndShadersShareTheirRegisteredAliases() {
        using var runtime = new GraphicsRuntime(); using var builtins = new DesktopBuiltins(runtime);
        var objects = Assert.IsType<ArrayObj>(new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile($$"""
            變數 p=匯入("Processing");p.尺寸(16,16);
            變數 canvas=p.建立畫布(4,4);
            變數 shader=p.建立著色器("{{WgpuShaders.Vertex}}","{{WgpuShaders.Fragment}}");[canvas,shader];
            """))).Elements;
        BilingualLibraryTest.AssertAliases(Assert.IsType<ModuleObj>(objects[0]), "Processing.Canvas");
        BilingualLibraryTest.AssertAliases(Assert.IsType<ModuleObj>(objects[1]), "Processing.Shader");
        var canvas = Assert.IsType<ModuleObj>(objects[0]);
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(canvas, "開始繪圖"));
        TextInputTest.Call(canvas, "背景", new IntegerObj(255), new IntegerObj(0), new IntegerObj(0));
        Assert.Equal(0xFFFF0000, GraphicsRuntime.Number(TextInputTest.Call(canvas, "get", new IntegerObj(1), new IntegerObj(1))));
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(canvas, "結束繪圖"));
    }
}
