using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquariusLang.Object;
using AquariusLang.VM;
using AquariusLang.Web;
using AquariusREPL.runtime;

namespace AquariusLangVMTesting.Packaging;

[Collection("Starship console output")]
public class PortableBottleTest {
    [Fact] public void PackagingDependsOnlyOnCoreAndFramework() {
        var names = typeof(BottlePackage).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain(names, n => n!.Contains("Desktop") || n.Contains("WebCompiler") || n.StartsWith("Silk") || n.StartsWith("Jolt"));
    }

    [Fact] public void V2DeclaresModulesAssetsAndBytecodeVersionWithoutSources() {
        using var t = new BottleTestDirectory();
        string source = t.Write("main.aqua", "42;"), asset = t.Write("assets/字 shader.wgsl", "shader"), bottle = t.FilePath("app.bottle");
        BottlePackage.Compile(new[] { source }, bottle, t.Path, new[] { asset });
        var package = BottlePackage.Load(bottle);
        Assert.Equal(2, package.Version); Assert.Equal("main.rius", package.EntryPoint);
        Assert.Equal("shader", System.Text.Encoding.UTF8.GetString(package.Assets["assets/字 shader.wgsl"]));
        using var zip = ZipFile.OpenRead(bottle);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".aqua"));
        using var doc = JsonDocument.Parse(zip.GetEntry("bottle.json")!.Open());
        Assert.Equal(BytecodeSerializer.FormatVersion, doc.RootElement.GetProperty("bytecodeVersion").GetInt32());
        Assert.Equal("main.rius", doc.RootElement.GetProperty("modules")[0].GetString());
        Assert.Equal("assets/字 shader.wgsl", doc.RootElement.GetProperty("assets")[0].GetString());
    }

    [Fact] public void LegacyV1RunsAndExportsWithoutOriginalSources() {
        using var t = new BottleTestDirectory(); string bottle = t.FilePath("v1.bottle");
        using (var zip = ZipFile.Open(bottle, ZipArchiveMode.Create)) {
            using (var writer = new StreamWriter(zip.CreateEntry("bottle.json").Open())) writer.Write("{\"format\":\"aquarius-bottle\",\"version\":1,\"entryPoint\":\"main.rius\"}");
            using var stream = zip.CreateEntry("main.rius").Open(); BytecodeSerializer.Write(new VmCompiler().Compile("42;"), stream);
        }
        Assert.Equal(1, BottlePackage.Load(bottle).Version);
        Assert.Equal(42, Assert.IsType<IntegerObj>(ScriptRunner.RunBottle(bottle)).Value);
        WebsiteCompiler.BuildBottle(bottle, t.FilePath("web"));
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        Assert.Equal("main.rius", doc.RootElement.GetProperty("entry").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("packageVersion").GetInt32());
    }

    [Fact] public void AssetDirectoryIncludesNestedAndEmptyResourcesButSkipsSourcesAndBuildFolders() {
        using var t = new BottleTestDirectory();
        string main = t.Write("main.aqua", "42;"); t.Write("assets/lib.aqua", "1;");
        t.Write("assets/empty.txt", ""); t.Write("assets/nested/file.bin", "bytes"); t.Write("assets/node_modules/secret.txt", "excluded");
        BottlePackage.Compile(new[] { main }, t.FilePath("app.bottle"), t.Path, new[] { t.FilePath("assets") });
        var assets = BottlePackage.Load(t.FilePath("app.bottle")).Assets;
        Assert.Equal(2, assets.Count); Assert.Empty(assets["assets/empty.txt"]); Assert.Contains("assets/nested/file.bin", assets.Keys);
    }

    [Fact] public void BuildsAreDeterministicAndExplicitEntryCanBeSelectedCaseInsensitively() {
        using var t = new BottleTestDirectory();
        var sources = new[] { t.Write("main.aqua", "1;"), t.Write("lib/Other.aqua", "42;") }; var assets = new[] { t.Write("z.txt", "z"), t.Write("a.txt", "a") };
        BottlePackage.Compile(sources, t.FilePath("one.bottle"), t.Path, assets, "LIB/other.aqua");
        BottlePackage.Compile(sources, t.FilePath("two.bottle"), t.Path, assets.Reverse(), "LIB/other.aqua");
        Assert.Equal(File.ReadAllBytes(t.FilePath("one.bottle")), File.ReadAllBytes(t.FilePath("two.bottle")));
        Assert.Equal("lib/Other.rius", BottlePackage.Load(t.FilePath("one.bottle")).EntryPoint);
    }

    [Theory]
    [InlineData("../escape.txt")][InlineData("/absolute.txt")][InlineData("C:/x.txt")][InlineData("a\\b.txt")]
    [InlineData("a//b.txt")][InlineData("a/./b.txt")][InlineData("a/../b.txt")][InlineData("a.txt ")]
    [InlineData("a.txt.")][InlineData("NUL.txt")][InlineData("COM1")][InlineData("a:stream")][InlineData("a?.txt")]
    public void UnsafeResourceArchivePathsAreRejected(string path) {
        using var t = new BottleTestDirectory();
        using (var zip = ZipFile.Open(t.FilePath("bad.bottle"), ZipArchiveMode.Create)) zip.CreateEntry(path);
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(t.FilePath("bad.bottle")));
    }

    [Theory]
    [InlineData("lib/../Main.aqua", "", "Main.rius")]
    [InlineData("../Main.RIUS", "lib/helper.rius", "Main.rius")]
    [InlineData("/LIB/helper.aqua", "", "lib/helper.rius")]
    [InlineData("..\\Main.aqua", "lib/helper.rius", "Main.rius")]
    public void SharedResolverNormalizesModulePathsAndExtensions(string path, string current, string expected) {
        using var t = new BottleTestDirectory();
        BottlePackage.Compile(new[] { t.Write("Main.aqua", "1;"), t.Write("lib/helper.aqua", "2;") }, t.FilePath("app.bottle"), t.Path);
        Assert.Equal(expected, BottlePackage.Load(t.FilePath("app.bottle")).ResolveScript(path, current));
    }

    [Theory]
    [InlineData("../outside.aqua")][InlineData("lib/../../outside.rius")][InlineData("missing.aqua")][InlineData("Main.txt")]
    public void SharedResolverRejectsEscapesAndMissingModules(string path) {
        using var t = new BottleTestDirectory();
        BottlePackage.Compile(new[] { t.Write("Main.aqua", "1;") }, t.FilePath("app.bottle"), t.Path);
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(t.FilePath("app.bottle")).ResolveScript(path));
    }

    [Theory]
    [InlineData("modules")][InlineData("assets")][InlineData("bytecodeVersion")]
    public void V2RequiresCompleteMetadata(string field) {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t);
        RewriteManifest(bottle, node => node.Remove(field));
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(bottle));
    }

    [Theory]
    [InlineData("version", 99)][InlineData("bytecodeVersion", 99)]
    public void UnknownVersionsAreRejected(string field, int value) {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t);
        RewriteManifest(bottle, node => node[field] = value);
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(bottle));
    }

    [Theory]
    [InlineData("duplicate")][InlineData("missing")][InlineData("undeclared")][InlineData("source")][InlineData("null")]
    public void MetadataMustExactlyDescribeTheArchive(string kind) {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t);
        RewriteManifest(bottle, node => {
            if (kind == "duplicate") node["modules"]!.AsArray().Add("MAIN.rius");
            if (kind == "missing") node["assets"]!.AsArray().Add("missing.txt");
            if (kind == "undeclared") node["assets"] = new JsonArray();
            if (kind == "source") node["assets"]!.AsArray().Add("main.aqua");
            if (kind == "null") node["assets"]!.AsArray().Add((JsonNode?)null);
        });
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(bottle));
    }

    [Fact] public void MissingDuplicateAndReservedAssetsPreservePreviousOutput() {
        using var t = new BottleTestDirectory(); string main = t.Write("main.aqua", "42;"), output = t.Write("app.bottle", "original"), asset = t.Write("asset.txt", "x");
        Assert.Throws<FileNotFoundException>(() => BottlePackage.Compile(new[] { main }, output, t.Path, new[] { t.FilePath("missing.txt") }));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { main }, output, t.Path, new[] { asset, asset }));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { main }, output, t.Path, new[] { t.Write("bad.rius", "bad") }));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { main }, output, t.Path, entryPoint: "missing.rius"));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { main }, output, t.Path, entryPoint: "main.txt"));
        Assert.Equal("original", File.ReadAllText(output)); Assert.Empty(Directory.GetFiles(t.Path, "*.tmp"));
    }

    [Fact] public void DesktopReadsPackagedAssetsAfterDeletingSourcesAndRelocatingBottle() {
        using var t = new BottleTestDirectory();
        var files = new[] { t.Write("source/main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); m.讀取();"),
            t.Write("source/lib/工具.aqua", "變數 讀取, read=函式(){變數 g=匯入(\"GL\"); 回傳 g.ReadText(目前工作目錄+\"/../assets/文字.txt\");};") };
        string asset = t.Write("source/assets/文字.txt", "已封裝"), bottle = t.FilePath("source/app.bottle");
        BottlePackage.Compile(files, bottle, t.FilePath("source"), new[] { asset });
        Directory.CreateDirectory(t.FilePath("deployment")); File.Move(bottle, t.FilePath("deployment/app.bottle"));
        Directory.Delete(t.FilePath("source"), true);
        Assert.Equal("已封裝", Assert.IsType<StringObj>(ScriptRunner.RunBottle(t.FilePath("deployment/app.bottle"))).Value);
        Assert.Single(Directory.GetFiles(t.FilePath("deployment")));
    }

    [Fact] public void ResourceDirectoryIsRemovedOnSuccessAndRuntimeFailure() {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t);
        string? resources;
        using (var runtime = new BottleRuntime(BottlePackage.Load(bottle), bottle)) {
            resources = runtime.ResourceDirectory;
            Assert.True(Directory.Exists(resources));
            Assert.NotNull(runtime.Execute("main.rius"));
        }
        Assert.False(Directory.Exists(resources));
        // Native ReadText returns a script-visible error; disposal must still happen.
        string main = t.Write("main.aqua", "變數 g=匯入(\"GL\"); g.ReadText(目前工作目錄+\"/missing.txt\");");
        BottlePackage.Compile(new[] { main }, bottle, t.Path, new[] { t.FilePath("asset.txt") });
        Assert.IsType<ErrorObj>(ScriptRunner.RunBottle(bottle));
        Assert.False(File.Exists(t.FilePath("missing.txt")));
    }

    [Fact] public void WebBuildUsesOnlyTheBottleAndEntryOverrideWithoutExecutingIt() {
        using var t = new BottleTestDirectory();
        var files = new[] { t.Write("src/main.aqua", "印出(\"DO-NOT-RUN\"); 1;"), t.Write("src/other.aqua", "42;") };
        string asset = t.Write("src/assets/empty.txt", ""), bottle = t.FilePath("app.bottle");
        BottlePackage.Compile(files, bottle, t.FilePath("src"), new[] { asset }); Directory.Delete(t.FilePath("src"), true);
        WebsiteCompiler.BuildBottle(bottle, t.FilePath("web"), "OTHER.aqua");
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        Assert.Equal("other.rius", doc.RootElement.GetProperty("entry").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("modules").EnumerateObject().Count());
        Assert.Equal("", doc.RootElement.GetProperty("assets").GetProperty("assets/empty.txt").GetString());
        Assert.True(File.Exists(t.FilePath("web/vendor-jolt.wasm"))); Assert.True(File.Exists(t.FilePath("web/vendor-jolt.LICENSE.txt")));
    }

    [Fact] public void FailedWebRebuildKeepsPreviousWebsiteAndDoesNotRemoveUnrelatedFiles() {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t), web = t.FilePath("web");
        WebsiteCompiler.BuildBottle(bottle, web); byte[] before = File.ReadAllBytes(t.FilePath("web/program.json"));
        Assert.Throws<InvalidDataException>(() => WebsiteCompiler.BuildBottle(bottle, web, "missing.rius"));
        Assert.Equal(before, File.ReadAllBytes(t.FilePath("web/program.json")));
        File.WriteAllText(t.FilePath("web/user.txt"), "keep");
        Assert.Throws<ArgumentException>(() => WebsiteCompiler.BuildBottle(bottle, web));
        Assert.Equal("keep", File.ReadAllText(t.FilePath("web/user.txt")));
        Assert.Throws<ArgumentException>(() => WebsiteCompiler.BuildBottle(bottle, t.Path));
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-web-*"));
    }

    [Fact] public void SuccessfulWebRebuildReplacesOldProgram() {
        using var t = new BottleTestDirectory(); string bottle = MakeBottle(t), web = t.FilePath("web");
        WebsiteCompiler.BuildBottle(bottle, web);
        string main = t.Write("main.aqua", "7;"); BottlePackage.Compile(new[] { main }, bottle, t.Path);
        WebsiteCompiler.BuildBottle(bottle, web);
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/program.json")));
        Assert.Equal(7, doc.RootElement.GetProperty("modules").GetProperty("main.rius").GetProperty("pool")[0].GetProperty("value").GetInt32());
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-web-*"));
    }

    [AquariusREPL.Graphics.WgpuFact]
    public void SourceFreeGpuBottleCreatesPersistentOutputUnderItsVirtualModuleDirectory() {
        using var t = new BottleTestDirectory();
        string source = t.Write("src/nested/main.aqua", "變數 gpu=匯入(\"WGPU\"); 變數 d=gpu.CreateDevice(); 變數 target=d.CreateRenderTarget(4,4); target.Clear([1,0,0,1]); target.Save(目前工作目錄+\"/new/output.png\"); d.Dispose(); 真;");
        string bottle = t.FilePath("app.bottle"); BottlePackage.Compile(new[] { source }, bottle, t.FilePath("src"));
        Directory.Delete(t.FilePath("src"), true);
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunBottle(bottle)).Value);
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, File.ReadAllBytes(t.FilePath("nested/new/output.png")).Take(8));
    }

    [AquariusREPL.Graphics.WgpuFact]
    public void SourceFreeProcessingLoadsBundledShaderFilesOnTheNativeGpu() {
        using var t = new BottleTestDirectory();
        string source = t.Write("src/main.aqua", "變數 p=匯入(\"Processing\"); p.size(16,16,p.P2D); 變數 s=p.loadShader(目前工作目錄+\"/assets/fragment.wgsl\",目前工作目錄+\"/assets/vertex.wgsl\"); p.shader(s); p.beginFrame(); p.background(255,0,0); p.endFrame(); p.close(); 真;");
        var assets = new[] { t.Write("src/assets/vertex.wgsl", AquariusLang.Graphics.WgpuShaderAbi.Vertex), t.Write("src/assets/fragment.wgsl", AquariusLang.Graphics.WgpuShaderAbi.Fragment) };
        string bottle = t.FilePath("app.bottle"); BottlePackage.Compile(new[] { source }, bottle, t.FilePath("src"), assets);
        Directory.Delete(t.FilePath("src"), true);
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunBottle(bottle)).Value);
    }

    private static string MakeBottle(BottleTestDirectory t) {
        string bottle = t.FilePath("app.bottle"); BottlePackage.Compile(new[] { t.Write("main.aqua", "42;") }, bottle, t.Path, new[] { t.Write("asset.txt", "asset") }); return bottle;
    }
    private static void RewriteManifest(string bottle, Action<JsonObject> change) {
        using var zip = ZipFile.Open(bottle, ZipArchiveMode.Update); var item = zip.GetEntry("bottle.json")!;
        JsonObject node; using (var reader = new StreamReader(item.Open())) node = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        change(node); item.Delete(); using var writer = new StreamWriter(zip.CreateEntry("bottle.json").Open()); writer.Write(node.ToJsonString());
    }
}
