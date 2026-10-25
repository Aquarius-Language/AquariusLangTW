using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusTests.Packaging;

/// <summary>Deployments that store assets beside the module (websites, APKs) keep the same executable module.</summary>
public class WasmExternalAssetsTest
{
    [RuntimeTheory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(16384)]
    [InlineData(1048576)]
    public void ExternalResourcesPreserveExecutableWasmAndRelocatableNames(int size)
    {
        var modules = new Dictionary<string, LoweredProgram> { ["中文.aqua"] = new LoweringCompiler().Compile("變數 f=函式(x){x+2;};f(40);") };
        var original = new WasmCompiler().Compile(modules, "中文.aqua", new Dictionary<string, byte[]> { ["模型/姿態.bin"] = Enumerable.Repeat((byte)253, size).ToArray() });
        var external = WasmProgram.Load(original.WithExternalAssets().Bytes.ToArray());
        using var folder = new TestFolder();
        Assert.Equal("42\n", WasmTestHost.RunWasm(folder.Write("app.wasm", external.Bytes.ToArray())).Succeeded().Output);
        Assert.Equal(original.Metadata.HeapStart, external.Metadata.HeapStart);
        Assert.Equal(original.Metadata.Modules, external.Metadata.Modules);
        Assert.Equal(original.Metadata.AbiVersion, external.Metadata.AbiVersion);
        Assert.True(external.Metadata.ExternalAssets); Assert.Equal(0, external.Assets["模型/姿態.bin"].Length);
        Assert.Equal(Enumerable.Repeat((byte)253, size), original.Assets["模型/姿態.bin"].ToArray());
        Assert.True(external.Bytes.Length <= original.Bytes.Length);
        Assert.Equal(external.Bytes.ToArray(), original.WithExternalAssets().Bytes.ToArray());
        Assert.Same(external, external.WithExternalAssets());
    }

    [Fact]
    public void AssetlessDeploymentReusesTheOriginalProgram()
    {
        var original = new WasmCompiler().Compile("42;"); Assert.Same(original, original.WithExternalAssets());
    }

    [Fact]
    public void ExternalAssetsKeepTheLibraryTables()
    {
        var program = new WasmCompiler(Fixtures.Set("stars")).Compile(
            new Dictionary<string, LoweredProgram> { ["main.aqua"] = new LoweringCompiler().Compile("匯入(\"星星\");") }, "main.aqua",
            new Dictionary<string, byte[]> { ["a.bin"] = [1] });
        var external = WasmProgram.Load(program.WithExternalAssets().Bytes.ToArray());
        Assert.Equal(program.Metadata.ImportTable, external.Metadata.ImportTable);
        Assert.Equal(program.Metadata.NativeLibraries, external.Metadata.NativeLibraries);
        Assert.Equal(program.Metadata.SupportedPlatforms, external.Metadata.SupportedPlatforms);
    }

    [RuntimeFact]
    public void UnrelatedCustomSectionsSurviveExternalization()
    {
        var original = new WasmCompiler().Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = new LoweringCompiler().Compile("42;") }, "main.aqua", new Dictionary<string, byte[]> { ["a.bin"] = [1] });
        byte[] extra = [0, 5, 3, (byte)'a', (byte)'b', (byte)'c', 42];
        var tagged = WasmProgram.Load(original.Bytes.ToArray().Concat(extra).ToArray());
        Assert.True(tagged.WithExternalAssets().Bytes.Span.IndexOf(extra) > 0);
        using var folder = new TestFolder();
        Assert.Equal("42\n", WasmTestHost.RunWasm(folder.Write("app.wasm", tagged.WithExternalAssets().Bytes.ToArray())).Succeeded().Output);
    }
}
