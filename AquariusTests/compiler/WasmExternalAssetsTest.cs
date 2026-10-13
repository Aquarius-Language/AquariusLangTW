using AquariusLang.Wasm;
using AquariusLang.Compiler;
using AquariusLang.Desktop.runtime;
using AquariusLang.Object;

namespace AquariusTests.Wasm;

public class WasmExternalAssetsTest
{
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(16384)]
    [InlineData(1048576)]
    public void ExternalResourcesPreserveExecutableWasmAndRelocatableNames(int size)
    {
        var modules = new Dictionary<string, LoweredProgram> { ["中文.aqua"] = new LoweringCompiler().Compile("變數 f=函式(x){x+2;};f(40);") };
        var original = new WasmCompiler().Compile(modules, "中文.aqua", new Dictionary<string, byte[]> { ["模型/姿態.bin"] = Enumerable.Repeat((byte)253, size).ToArray() });
        var external = WasmProgram.Load(original.WithExternalAssets().Bytes.ToArray());
        Assert.Equal("42", new WasmRuntime(engine: new WasmtimeEngine()).Execute(external).Inspect());
        Assert.Equal(original.Metadata.HeapStart, external.Metadata.HeapStart);
        Assert.Equal(original.Metadata.Modules, external.Metadata.Modules);
        Assert.Equal("", external.Metadata.Assets["模型/姿態.bin"]);
        Assert.Equal(size, Convert.FromBase64String(original.Metadata.Assets["模型/姿態.bin"]).Length);
        Assert.True(external.Bytes.Length <= original.Bytes.Length);
        Assert.Equal(external.Bytes.ToArray(), original.WithExternalAssets().Bytes.ToArray());
    }
    [Fact]
    public void AssetlessDeploymentReusesTheOriginalProgram()
    {
        var original = new WasmCompiler().Compile("42;"); Assert.Same(original, original.WithExternalAssets());
    }
    [Fact]
    public void UnrelatedCustomSectionsSurviveExternalization()
    {
        var original = new WasmCompiler().Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = new LoweringCompiler().Compile("42;") }, "main.aqua", new Dictionary<string, byte[]> { ["a.bin"] = new byte[] { 1 } });
        byte[] extra = { 0, 5, 3, (byte)'a', (byte)'b', (byte)'c', 42 };
        var tagged = WasmProgram.Load(original.Bytes.ToArray().Concat(extra).ToArray());
        Assert.Equal(extra, tagged.WithExternalAssets().Bytes.ToArray().TakeLast(extra.Length));
        Assert.Equal("42", new WasmRuntime(engine: new WasmtimeEngine()).Execute(tagged.WithExternalAssets()).Inspect());
    }
}
