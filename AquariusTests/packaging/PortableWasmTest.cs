using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquariusLang.Object;
using AquariusLang.Compiler;
using AquariusLang.Web;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

[Collection("Starship console output")]
public class PortableWasmTest {
    [Fact]
    public void ExternalLibraryManifestMatchesEveryEmittedCoreAsset() {
        using var t=new ApplicationTestDirectory();string wasm=MakeWasm(t),web=t.FilePath("web");WebsiteCompiler.BuildWasm(wasm,web);
        using var document=JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        foreach(var library in document.RootElement.GetProperty("externalLibraries").EnumerateArray()) {
            foreach(var asset in library.GetProperty("assets").EnumerateArray()) {
                string name=asset.GetProperty("name").GetString()!;
                string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(web,name)))).ToLowerInvariant();
                Assert.Equal(asset.GetProperty("sha256").GetString(),hash);
            }
        }
    }
    [Fact] public void PackagingDependsOnlyOnCoreAndFramework() {
        var names = typeof(WasmApplication).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain(names, n => n!.Contains("Desktop") || n.Contains("WebCompiler") || n.StartsWith("Silk") || n.StartsWith("Jolt"));
    }

    [Fact] public void AssetDirectoryIncludesNestedAndEmptyResourcesButSkipsSourcesAndBuildFolders() {
        using var t = new ApplicationTestDirectory();
        string main = t.Write("main.aqua", "42;"); t.Write("assets/lib.aqua", "1;");
        t.Write("assets/empty.txt", ""); t.Write("assets/nested/file.bin", "bytes"); t.Write("assets/node_modules/secret.txt", "excluded");
        WasmApplication.Compile(new[] { main }, t.FilePath("app.wasm"), t.Path, new[] { t.FilePath("assets") });
        var assets = WasmApplication.Load(t.FilePath("app.wasm")).Assets;
        Assert.Equal(2, assets.Count); Assert.Empty(assets["assets/empty.txt"]); Assert.Contains("assets/nested/file.bin", assets.Keys);
    }

    [Fact] public void BuildsAreDeterministicAndExplicitEntryCanBeSelectedCaseInsensitively() {
        using var t = new ApplicationTestDirectory();
        var sources = new[] { t.Write("main.aqua", "1;"), t.Write("lib/Other.aqua", "42;") }; var assets = new[] { t.Write("z.txt", "z"), t.Write("a.txt", "a") };
        WasmApplication.Compile(sources, t.FilePath("one.wasm"), t.Path, assets, "LIB/other.aqua");
        WasmApplication.Compile(sources, t.FilePath("two.wasm"), t.Path, assets.Reverse(), "LIB/other.aqua");
        Assert.Equal(File.ReadAllBytes(t.FilePath("one.wasm")), File.ReadAllBytes(t.FilePath("two.wasm")));
        Assert.Equal("lib/Other.aqua", WasmApplication.Load(t.FilePath("one.wasm")).EntryPoint);
    }

    [Theory]
    [InlineData("../escape.txt")][InlineData("/absolute.txt")][InlineData("C:/x.txt")][InlineData("a\\b.txt")]
    [InlineData("a//b.txt")][InlineData("a/./b.txt")][InlineData("a/../b.txt")][InlineData("a.txt ")]
    [InlineData("a.txt.")][InlineData("NUL.txt")][InlineData("COM1")][InlineData("a:stream")][InlineData("a?.txt")]
    public void UnsafeResourceArchivePathsAreRejected(string path) {
        var program=new AquariusLang.Wasm.WasmCompiler().Compile("42;");
        var bytes=AquariusTests.Wasm.WasmArtifactTest.WithMetadata(program,node=>node["assets"]![path]="");
        Assert.Throws<InvalidDataException>(() => AquariusLang.Wasm.WasmProgram.Load(bytes));
    }

    [Theory]
    [InlineData("lib/../Main.aqua", "", "Main.aqua")]
    [InlineData("../Main.AQUA", "lib/helper.aqua", "Main.aqua")]
    [InlineData("/LIB/helper.aqua", "", "lib/helper.aqua")]
    [InlineData("..\\Main.aqua", "lib/helper.aqua", "Main.aqua")]
    public void SharedResolverNormalizesModulePathsAndExtensions(string path, string current, string expected) {
        using var t = new ApplicationTestDirectory();
        WasmApplication.Compile(new[] { t.Write("Main.aqua", "1;"), t.Write("lib/helper.aqua", "2;") }, t.FilePath("app.wasm"), t.Path);
        Assert.Equal(expected, WasmApplication.Load(t.FilePath("app.wasm")).ResolveScript(path, current));
    }

    [Theory]
    [InlineData("../outside.aqua")][InlineData("lib/../../outside.aqua")][InlineData("missing.aqua")][InlineData("Main.txt")]
    public void SharedResolverRejectsEscapesAndMissingModules(string path) {
        using var t = new ApplicationTestDirectory();
        WasmApplication.Compile(new[] { t.Write("Main.aqua", "1;") }, t.FilePath("app.wasm"), t.Path);
        Assert.Throws<InvalidDataException>(() => WasmApplication.Load(t.FilePath("app.wasm")).ResolveScript(path));
    }

    [Fact] public void MissingDuplicateAndReservedAssetsPreservePreviousOutput() {
        using var t = new ApplicationTestDirectory(); string main = t.Write("main.aqua", "42;"), output = t.Write("app.wasm", "original"), asset = t.Write("asset.txt", "x");
        Assert.Throws<FileNotFoundException>(() => WasmApplication.Compile(new[] { main }, output, t.Path, new[] { t.FilePath("missing.txt") }));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile(new[] { main }, output, t.Path, new[] { asset, asset }));
        Assert.Throws<InvalidDataException>(()=>WasmApplication.ValidatePath("../bad.aqua"));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile(new[] { main }, output, t.Path, entryPoint: "missing.aqua"));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile(new[] { main }, output, t.Path, entryPoint: "main.txt"));
        Assert.Equal("original", File.ReadAllText(output)); Assert.Empty(Directory.GetFiles(t.Path, "*.tmp"));
    }

    [Fact] public void DesktopReadsPackagedAssetsAfterDeletingSourcesAndRelocatingWasm() {
        using var t = new ApplicationTestDirectory();
        var files = new[] { t.Write("source/main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); m.讀取();"),
            t.Write("source/lib/工具.aqua", "變數 讀取, read=函式(){變數 g=匯入(\"GL\"); 回傳 g.ReadText(目前工作目錄+\"/../assets/文字.txt\");};") };
        string asset = t.Write("source/assets/文字.txt", "已封裝"), wasm = t.FilePath("source/app.wasm");
        WasmApplication.Compile(files, wasm, t.FilePath("source"), new[] { asset });
        Directory.CreateDirectory(t.FilePath("deployment")); File.Move(wasm, t.FilePath("deployment/app.wasm"));
        Directory.Delete(t.FilePath("source"), true);
        Assert.Equal("已封裝", Assert.IsType<StringObj>(ScriptRunner.RunWasm(t.FilePath("deployment/app.wasm"))).Value);
        Assert.Single(Directory.GetFiles(t.FilePath("deployment")));
    }

    [Fact] public void ResourceDirectoryIsRemovedOnSuccessAndRuntimeFailure() {
        using var t = new ApplicationTestDirectory(); string wasm = MakeWasm(t);
        string? resources;
        using (var runtime = new WasmApplicationRuntime(WasmApplication.Load(wasm), wasm)) {
            resources = runtime.ResourceDirectory;
            Assert.True(Directory.Exists(resources));
            Assert.NotNull(runtime.Execute("main.aqua"));
        }
        Assert.False(Directory.Exists(resources));
        // Native ReadText returns a script-visible error; disposal must still happen.
        string main = t.Write("main.aqua", "變數 g=匯入(\"GL\"); g.ReadText(目前工作目錄+\"/missing.txt\");");
        WasmApplication.Compile(new[] { main }, wasm, t.Path, new[] { t.FilePath("asset.txt") });
        Assert.IsType<ErrorObj>(ScriptRunner.RunWasm(wasm));
        Assert.False(File.Exists(t.FilePath("missing.txt")));
    }

    [Fact] public void WebBuildUsesOnlyTheWasmAndEntryOverrideWithoutExecutingIt() {
        using var t = new ApplicationTestDirectory();
        var files = new[] { t.Write("src/main.aqua", "印出(\"DO-NOT-RUN\"); 1;"), t.Write("src/other.aqua", "42;") };
        string asset = t.Write("src/assets/empty.txt", ""), wasm = t.FilePath("app.wasm");
        WasmApplication.Compile(files, wasm, t.FilePath("src"), new[] { asset }); Directory.Delete(t.FilePath("src"), true);
        WebsiteCompiler.BuildWasm(wasm, t.FilePath("web"), "OTHER.aqua");
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        Assert.Equal("other.aqua", doc.RootElement.GetProperty("entry").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("modules").EnumerateObject().Count());
        Assert.Equal("", doc.RootElement.GetProperty("assets").GetProperty("assets/empty.txt").GetString());
        Assert.True(File.Exists(t.FilePath("web/vendor-jolt.wasm"))); Assert.True(File.Exists(t.FilePath("web/vendor-jolt.LICENSE.txt")));
    }

    [Fact] public void FailedWebRebuildKeepsPreviousWebsiteAndDoesNotRemoveUnrelatedFiles() {
        using var t = new ApplicationTestDirectory(); string wasm = MakeWasm(t), web = t.FilePath("web");
        WebsiteCompiler.BuildWasm(wasm, web); byte[] before = File.ReadAllBytes(t.FilePath("web/program.json"));
        Assert.Throws<InvalidDataException>(() => WebsiteCompiler.BuildWasm(wasm, web, "missing.aqua"));
        Assert.Equal(before, File.ReadAllBytes(t.FilePath("web/program.json")));
        File.WriteAllText(t.FilePath("web/user.txt"), "keep");
        Assert.Throws<ArgumentException>(() => WebsiteCompiler.BuildWasm(wasm, web));
        Assert.Equal("keep", File.ReadAllText(t.FilePath("web/user.txt")));
        Assert.Throws<ArgumentException>(() => WebsiteCompiler.BuildWasm(wasm, t.Path));
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-web-*"));
    }

    [Fact] public void SuccessfulWebRebuildReplacesOldProgram() {
        using var t = new ApplicationTestDirectory(); string wasm = MakeWasm(t), web = t.FilePath("web");
        WebsiteCompiler.BuildWasm(wasm, web);
        string main = t.Write("main.aqua", "7;"); WasmApplication.Compile(new[] { main }, wasm, t.Path);
        WebsiteCompiler.BuildWasm(wasm, web);
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        Assert.Equal(7, Assert.IsType<IntegerObj>(new AquariusLang.Wasm.WasmRuntime(engine:new WasmtimeEngine()).Execute(AquariusLang.Wasm.WasmProgram.Load(t.FilePath("web/program.wasm")))).Value);
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-web-*"));
    }

    [AquariusLang.Desktop.Graphics.WgpuFact]
    public void SourceFreeGpuWasmCreatesPersistentOutputUnderItsVirtualModuleDirectory() {
        using var t = new ApplicationTestDirectory();
        string source = t.Write("src/nested/main.aqua", "變數 gpu=匯入(\"WGPU\"); 變數 d=gpu.CreateDevice(); 變數 target=d.CreateRenderTarget(4,4); target.Clear([1,0,0,1]); target.Save(目前工作目錄+\"/new/output.png\"); d.Dispose(); 真;");
        string wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.FilePath("src"));
        Directory.Delete(t.FilePath("src"), true);
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunWasm(wasm)).Value);
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, File.ReadAllBytes(t.FilePath("nested/new/output.png")).Take(8));
    }

    [AquariusLang.Desktop.Graphics.WgpuFact]
    public void SourceFreeProcessingLoadsBundledShaderFilesOnTheNativeGpu() {
        using var t = new ApplicationTestDirectory();
        string source = t.Write("src/main.aqua", "變數 p=匯入(\"Processing\"); p.size(16,16,p.P2D); 變數 s=p.loadShader(目前工作目錄+\"/assets/fragment.wgsl\",目前工作目錄+\"/assets/vertex.wgsl\"); p.shader(s); p.beginFrame(); p.background(255,0,0); p.endFrame(); p.close(); 真;");
        var assets = new[] { t.Write("src/assets/vertex.wgsl", AquariusLang.Graphics.WgpuShaderAbi.Vertex), t.Write("src/assets/fragment.wgsl", AquariusLang.Graphics.WgpuShaderAbi.Fragment) };
        string wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.FilePath("src"), assets);
        Directory.Delete(t.FilePath("src"), true);
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunWasm(wasm)).Value);
    }

    private static string MakeWasm(ApplicationTestDirectory t) {
        string wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { t.Write("main.aqua", "42;") }, wasm, t.Path, new[] { t.Write("asset.txt", "asset") }); return wasm;
    }
}
