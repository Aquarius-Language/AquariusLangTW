using System.Text;
using AquariusLang.Wasm;

namespace AquariusTests.Packaging;

/// <summary>Packaging sources and assets into one portable .wasm (<see cref="WasmApplication"/>) and running it after the
/// sources are gone: module paths, assets, entries and imports between packaged modules.</summary>
public class WasmApplicationTest
{
    private static string Text(RunResult result) => result.Output.TrimEnd('\n');

    // ---- Packaging -----------------------------------------------------------------------------------------------

    [Fact]
    public void PackagingDependsOnlyOnTheCompilerAndFramework()
    {
        var names = typeof(WasmApplication).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(names, n => n.StartsWith("Aquarius", StringComparison.Ordinal) || n.Contains("Wasmtime") || n.StartsWith("Silk") || n.StartsWith("Jolt") || n.Contains("ClearScript"));
    }

    [Fact]
    public void AssetDirectoryIncludesNestedAndEmptyResourcesButSkipsSourcesAndBuildFolders()
    {
        using var t = new TestFolder();
        string main = t.Write("main.aqua", "42;"); t.Write("assets/lib.aqua", "1;");
        t.Write("assets/empty.txt", ""); t.Write("assets/nested/file.bin", "bytes"); t.Write("assets/node_modules/secret.txt", "excluded");
        t.Write("assets/bin/x.dll", "excluded"); t.Write("assets/obj/y.txt", "excluded");
        WasmApplication.Compile([main], t.FilePath("app.wasm"), t.Path, [t.FilePath("assets")]);
        var assets = WasmApplication.Load(t.FilePath("app.wasm")).Assets;
        Assert.Equal(2, assets.Count); Assert.Empty(assets["assets/empty.txt"]); Assert.Contains("assets/nested/file.bin", assets.Keys);
    }

    [Fact]
    public void BuildsAreDeterministicAndExplicitEntryCanBeSelectedCaseInsensitively()
    {
        using var t = new TestFolder();
        var sources = new[] { t.Write("main.aqua", "1;"), t.Write("lib/Other.aqua", "42;") }; var assets = new[] { t.Write("z.txt", "z"), t.Write("a.txt", "a") };
        WasmApplication.Compile(sources, t.FilePath("one.wasm"), t.Path, assets, "LIB/other.aqua");
        WasmApplication.Compile(sources, t.FilePath("two.wasm"), t.Path, assets.Reverse(), "LIB/other.aqua");
        Assert.Equal(File.ReadAllBytes(t.FilePath("one.wasm")), File.ReadAllBytes(t.FilePath("two.wasm")));
        Assert.Equal("lib/Other.aqua", WasmApplication.Load(t.FilePath("one.wasm")).EntryPoint);
        Assert.Equal(WasmAbi.Version, WasmApplication.Load(t.FilePath("one.wasm")).Program.Metadata.AbiVersion);
    }

    [Theory]
    [InlineData("../escape.txt")][InlineData("/absolute.txt")][InlineData("C:/x.txt")][InlineData("a\\b.txt")]
    [InlineData("a//b.txt")][InlineData("a/./b.txt")][InlineData("a/../b.txt")][InlineData("a.txt ")]
    [InlineData("a.txt.")][InlineData("NUL.txt")][InlineData("COM1")][InlineData("a:stream")][InlineData("a?.txt")]
    public void UnsafeResourceArchivePathsAreRejected(string path)
    {
        var program = new WasmCompiler().Compile("42;");
        var manifest = WasmArtifactTest.Manifest(program) with { Assets = [(path, 0)] };
        Assert.NotNull(WasmProgram.Load(WasmArtifactTest.WithManifest(program, manifest with { Assets = [("safe.txt", 0)] }, [])));
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(WasmArtifactTest.WithManifest(program, manifest, [])));
        Assert.Throws<InvalidDataException>(() => WasmApplication.ValidatePath(path));
    }

    [Theory]
    [InlineData("lib/../Main.aqua", "", "Main.aqua")]
    [InlineData("../Main.AQUA", "lib/helper.aqua", "Main.aqua")]
    [InlineData("/LIB/helper.aqua", "", "lib/helper.aqua")]
    [InlineData("..\\Main.aqua", "lib/helper.aqua", "Main.aqua")]
    public void SharedResolverNormalizesModulePathsAndExtensions(string path, string current, string expected)
    {
        using var t = new TestFolder();
        WasmApplication.Compile([t.Write("Main.aqua", "1;"), t.Write("lib/helper.aqua", "2;")], t.FilePath("app.wasm"), t.Path);
        Assert.Equal(expected, WasmApplication.Load(t.FilePath("app.wasm")).ResolveScript(path, current));
    }

    [Theory]
    [InlineData("../outside.aqua")][InlineData("lib/../../outside.aqua")][InlineData("missing.aqua")][InlineData("Main.txt")]
    public void SharedResolverRejectsEscapesAndMissingModules(string path)
    {
        using var t = new TestFolder();
        WasmApplication.Compile([t.Write("Main.aqua", "1;")], t.FilePath("app.wasm"), t.Path);
        Assert.Throws<InvalidDataException>(() => WasmApplication.Load(t.FilePath("app.wasm")).ResolveScript(path));
    }

    [Fact]
    public void MissingDuplicateAndReservedAssetsPreservePreviousOutput()
    {
        using var t = new TestFolder(); string main = t.Write("main.aqua", "42;"), output = t.Write("app.wasm", "original"), asset = t.Write("asset.txt", "x");
        Assert.Throws<FileNotFoundException>(() => WasmApplication.Compile([main], output, t.Path, [t.FilePath("missing.txt")]));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile([main], output, t.Path, [asset, asset]));
        Assert.Throws<InvalidDataException>(() => WasmApplication.ValidatePath("../bad.aqua"));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile([main], output, t.Path, entryPoint: "missing.aqua"));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile([main], output, t.Path, entryPoint: "main.txt"));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile([main], t.FilePath("app.zip"), t.Path));
        Assert.Throws<ArgumentException>(() => WasmApplication.Compile([main, main], output, t.Path));
        Assert.Throws<AquariusLang.Compiler.CompilationException>(() => WasmApplication.Compile([t.Write(".aquarius/stars/x.aqua", "1;")], output, t.Path));
        Assert.Equal("original", File.ReadAllText(output)); Assert.Empty(Directory.GetFiles(t.Path, "*.tmp"));
    }

    // ---- Source-free deployment ----------------------------------------------------------------------------------

    [RuntimeFact]
    public void PackagedAssetsAreReadAfterDeletingSourcesAndRelocatingTheWasm()
    {
        using var t = new TestFolder();
        var files = new[] { t.Write("source/main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); m.讀取();"),
            t.Write("source/lib/工具.aqua", "變數 讀取, read=函式(){變數 f=匯入(\"Files\"); 回傳 f.讀取資產文字(\"assets/文字.txt\", \"utf-8\");};") };
        string asset = t.Write("source/assets/文字.txt", "已封裝"), wasm = t.FilePath("source/app.wasm");
        WasmApplication.Compile(files, wasm, t.FilePath("source"), [asset]);
        Directory.CreateDirectory(t.FilePath("deployment")); File.Move(wasm, t.FilePath("deployment/app.wasm"));
        Directory.Delete(t.FilePath("source"), true);
        Assert.Equal("已封裝", Text(WasmTestHost.RunWasm(t.FilePath("deployment/app.wasm")).Succeeded()));
        // Nothing is extracted beside the application.
        Assert.Equal(["app.wasm"], Directory.GetFiles(t.FilePath("deployment")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(t.FilePath("deployment")));
    }

    [RuntimeTheory]
    [InlineData("匯入(\"test/module.aqua\")")]
    [InlineData("匯入(目前工作目錄 + \"/test/module.aqua\")")]
    [InlineData("import(\"./test/../test/module.aqua\")")]
    public void RelocatedWasmImportsCompiledModulesAfterAllSourcesAreDeleted(string import)
    {
        using var temp = new TestFolder();
        string main = temp.Write("source/main.aqua", $"變數 m = {import}; m.加(40);");
        string module = temp.Write("source/test/module.aqua", "變數 加 = 函式(x) { x+2; };");
        string wasm = temp.FilePath("source/main.wasm");
        WasmApplication.Compile([main, module], wasm, temp.FilePath("source"));
        Directory.CreateDirectory(temp.FilePath("deployment"));
        string relocated = temp.FilePath("deployment/main.wasm");
        File.Move(wasm, relocated);
        Directory.Delete(temp.FilePath("source"), true);
        Assert.Equal("42", Text(WasmTestHost.RunWasm(relocated).Succeeded()));
        Assert.Equal(["main.wasm"], Directory.GetFiles(temp.FilePath("deployment")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(temp.FilePath("deployment")));
    }

    [RuntimeFact]
    public void ModuleFunctionsAndReturnedClosuresKeepTheirOwnDirectoriesForDeferredImports()
    {
        using var temp = new TestFolder();
        var files = new[] {
            temp.Write("main.aqua", "變數 m=匯入(\"lib/module.aqua\"); 變數 f=m.建立(); [m.讀取(), f(), 目前工作目錄, m.目錄];"),
            temp.Write("lib/module.aqua", "變數 目錄=目前工作目錄; 變數 讀取=函式() { 變數 c=匯入(目前工作目錄 + \"/nested/child.aqua\"); c.值; }; 變數 建立=函式() { 函式() { 變數 c=匯入(\"nested/child.aqua\"); c.值; }; };"),
            temp.Write("lib/nested/child.aqua", "變數 p=匯入(\"../sibling.aqua\"); 變數 值=p.值+2;"),
            temp.Write("lib/sibling.aqua", "變數 值=40;")
        };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        foreach (var file in files) File.Delete(file);
        string root = temp.Path.Replace('\\', '/');
        // 目前工作目錄 is the application folder plus the calling script's folder, with '/' separators.
        Assert.Equal($"[42, 42, {root}, {root}/lib]", Text(WasmTestHost.RunWasm(wasm).Succeeded()));
    }

    [RuntimeTheory]
    [InlineData("匯入(\"missing.aqua\");", "Packaged script not found: missing.aqua")]
    [InlineData("匯入(\"../outside.aqua\");", "Packaged script not found")]
    [InlineData("匯入(\"C:/outside/x.aqua\");", "outside the application package")]
    [InlineData("匯入(42);", "must be a string")]
    [InlineData("匯入(\"file.txt\");", "Module not found: file.txt")]
    [InlineData("匯入(\"NoSuchModule\");", "Module not found: NoSuchModule")]
    public void ImportFailuresAreLanguageErrors(string source, string message)
    {
        using var temp = new TestFolder();
        string main = temp.Write("main.aqua", source), wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile([main], wasm, temp.Path);
        var result = WasmTestHost.RunWasm(wasm);
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("ERROR: ", result.Output); Assert.Contains(message, result.Output);
    }

    [RuntimeTheory]
    [InlineData("匯入(\"main.aqua\");", null)]
    [InlineData("匯入(\"lib/module.aqua\");", "匯入(\"../main.aqua\");")]
    public void CircularImportsReportALanguageError(string main, string? module)
    {
        using var temp = new TestFolder();
        var files = new List<string> { temp.Write("main.aqua", main) };
        if (module != null) files.Add(temp.Write("lib/module.aqua", module));
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        var result = WasmTestHost.RunWasm(wasm);
        string all = result.Output + result.Error, shown = all.Length > 400 ? all[..400] + "..." : all;
        Assert.True(result.ExitCode == 1, shown);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(result.Output, "(?i)circular|循環") && !all.Contains("WebAssembly trap"),
            "Expected a circular import error, got: " + shown);
    }

    [RuntimeFact]
    public void ImportedRuntimeErrorsPropagateAndScriptsKeepIndependentGlobals()
    {
        using var temp = new TestFolder();
        string main = temp.Write("main.aqua", "變數 a=匯入(\"module.aqua\"); 變數 b=匯入(\"module.aqua\"); a.增加(); [a.值, b.值];");
        string module = temp.Write("module.aqua", "變數 值=40; 變數 增加=函式() { 值++; };"), wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile([main, module], wasm, temp.Path);
        Assert.Equal("[41, 40]", Text(WasmTestHost.RunWasm(wasm).Succeeded()));
        File.WriteAllText(module, "不存在;");
        WasmApplication.Compile([main, module], wasm, temp.Path);
        var failed = WasmTestHost.RunWasm(wasm);
        Assert.Equal(1, failed.ExitCode); Assert.Contains("ERROR: Identifier not found: 不存在", failed.Output);
    }

    [RuntimeFact]
    public void SelectedEntryRunsWithItsOwnDirectoryAndEachRunStartsFresh()
    {
        using var temp = new TestFolder();
        var files = new[] { temp.Write("main.aqua", "1;"), temp.Write("lib/second.aqua", "變數 n=40; n+=2; [n, 目前工作目錄];") };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        for (int i = 0; i < 2; i++)
            Assert.Equal($"[42, {temp.Path.Replace('\\', '/')}/lib]", Text(WasmTestHost.RunWasm(wasm, entry: "LIB/second.aqua").Succeeded()));
        Assert.Equal("1", Text(WasmTestHost.RunWasm(wasm).Succeeded()));
        var missing = WasmTestHost.RunWasm(wasm, entry: "missing.aqua");
        Assert.Equal(1, missing.ExitCode); Assert.Contains("Packaged script not found: missing.aqua", missing.Error);
    }

    [RuntimeFact]
    public void PackagedModulesShareNativeModulesWithTheEntryScript()
    {
        using var temp = new TestFolder();
        var files = new[] {
            temp.Write("main.aqua", "變數 m=匯入(\"lib/module.aqua\"); [匯入(\"Terminal\") == m.取得(), 匯入(\"終端機\") == 匯入(\"Terminal\"), 型別(m.取得())];"),
            temp.Write("lib/module.aqua", "變數 取得=函式() { 匯入(\"Terminal\"); };")
        };
        string wasm = temp.FilePath("main.wasm");
        WasmApplication.Compile(files, wasm, temp.Path);
        Assert.Equal("[真, 真, module]", Text(WasmTestHost.RunWasm(wasm).Succeeded()));
    }

    [RuntimeFact]
    public void ModuleAliasesShareClosuresInSourceFreeApplications()
    {
        using var temp = new TestFolder();
        string library = temp.Write("工具.aqua", """
            變數 n=40;
            變數 計數,count=函式(){n++;n;};
            變數 中文限定=函式(){7;};
            變數 englishOnly=函式(){9;};
            """);
        var cases = new[] { ("計數", "計數"), ("count", "count"), ("計數", "count"), ("count", "計數") };
        foreach (var (first, second) in cases) {
            string main = temp.Write("main.aqua", $$"""
                變數 lib=import("工具.aqua");
                變數 saved=lib.{{second}};
                print("ALIAS_SMOKE",lib.{{first}}(),saved(),lib.{{second}}(),lib.中文限定(),lib.englishOnly());
                真;
                """);
            WasmApplication.Compile([main, library], temp.FilePath("main.wasm"), temp.Path);
            Assert.Equal("ALIAS_SMOKE 41 42 43 7 9\n真\n", WasmTestHost.RunWasm(temp.FilePath("main.wasm")).Succeeded().Output);
        }
    }

    [RuntimeTheory]
    [InlineData("中文")][InlineData("english")]
    public void AliasArgumentFailuresStopTheProgramWithExitCodeOne(string name)
    {
        var result = WasmTestHost.Run($"變數 中文,english=函式(n){{n;}};{name}();");
        Assert.Equal(1, result.ExitCode); Assert.Contains("expects 1 arguments", result.Output);
    }

    [RuntimeTheory]
    [InlineData("變數 值 = 42;", 0, "")]
    [InlineData("印出(\"星泉\");", 0, "星泉\n")]
    [InlineData("", 0, "")]
    [InlineData("42;", 0, "42\n")]
    [InlineData("未知;", 1, "ERROR: Identifier not found: 未知\n")]
    [InlineData("匯入(\"missing.aqua\");", 1, "ERROR: 匯入: Packaged script not found: missing.aqua\n")]
    public void VoidResultsValuesAndRuntimeErrorsGiveTheExitCode(string source, int code, string output)
    {
        var result = WasmTestHost.Run(source);
        Assert.Equal(code, result.ExitCode); Assert.Equal(output, result.Output);
    }

    [Fact]
    public void ModuleIndexesAndAssetsArePreservedByLoad()
    {
        using var t = new TestFolder();
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("main.aqua", "1;"), t.Write("b.aqua", "2;")], wasm, t.Path, [t.Write("data/x.bin", "x")]);
        var application = WasmApplication.Load(wasm);
        Assert.Equal(["main.aqua", "b.aqua"], application.Program.Metadata.Modules.Keys);
        Assert.Equal("x", Encoding.UTF8.GetString(application.Assets["DATA/X.BIN"]));
        Assert.Empty(application.NativeLibraries);
        using var stream = File.OpenRead(wasm);
        Assert.Equal(application.Program.Bytes.ToArray(), WasmApplication.Load(stream).Program.Bytes.ToArray());
    }
}
