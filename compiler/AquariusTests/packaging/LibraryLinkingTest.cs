using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Wasm;

namespace AquariusTests.Packaging;

/// <summary>How the compiler links libraries into applications (application metadata ABI 4): native modules list their
/// library, Aquarius-source modules are bundled at .aquarius/&lt;library&gt;/&lt;source&gt; with every import name, declared
/// libraries bundle all their modules, and the platforms narrow to what every needed library supports. Uses the fixture
/// libraries in packaging/fixtures and the default libraries' manifests.</summary>
public class LibraryLinkingTest
{
    private const string StarsModule = ".aquarius/stars/aqua/Stars.aqua", StarsShared = ".aquarius/stars/aqua/shared/亮度.aqua",
        ChartModule = ".aquarius/stars/aqua/chart/Chart.aqua", CometsModule = ".aquarius/comets/Comets.aqua";

    private static WasmProgram Compile(string source, LibrarySet? libraries = null, IEnumerable<string>? declared = null, string[]? platforms = null) =>
        new WasmCompiler(true, libraries).Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = new LoweringCompiler().Compile(source) },
            "main.aqua", null, platforms, declared);

    private static LibrarySet Sky() => Fixtures.WithDefaults(Fixtures.Library("stars"), Fixtures.Library("comets"));

    // ---- Native modules ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("變數 a = 匯入(\"Audio\");")]
    [InlineData("變數 a = 匯入(\"音訊\");")]
    [InlineData("var a = import(\"Audio\");")]
    [InlineData("變數 f = 函式() { 函式() { import(\"音訊\"); }; };")]
    public void NativeModulesOfDefaultLibrariesListTheirLibraryByEitherName(string source)
    {
        var audio = LibrarySet.Default.OwnerOf("Audio");
        Assert.NotNull(audio); Assert.Equal("audio", audio!.NativeName);
        var program = Compile(source);
        Assert.Equal(["audio"], program.Metadata.NativeLibraries);
        Assert.Empty(program.Metadata.ImportTable);
        Assert.Equal(["main.aqua"], program.Metadata.Modules.Keys);
        Assert.Equal(audio.Platforms.Order(), program.Metadata.SupportedPlatforms!.Order());
        // The application metadata carries the list to every loader.
        Assert.Equal(["audio"], WasmProgram.Load(program.Bytes.ToArray()).Metadata.NativeLibraries);
    }

    [Theory]
    [InlineData("42;")]
    [InlineData("變數 名 = \"Au\" + \"dio\"; 匯入(名);")]
    [InlineData("印出(\"Audio\");")]
    public void ProgramsWithoutLiteralLibraryImportsNeedNoLibrary(string source)
    {
        var program = Compile(source);
        Assert.Empty(program.Metadata.NativeLibraries); Assert.Empty(program.Metadata.ImportTable);
        Assert.Null(program.Metadata.SupportedPlatforms);
    }

    [Fact]
    public void SeveralLibrariesAreListedOnceInNameOrder()
    {
        var program = Compile("匯入(\"Terminal\"); 匯入(\"音訊\"); 匯入(\"終端機\"); 匯入(\"Audio\");");
        Assert.Equal(["audio", "terminal"], program.Metadata.NativeLibraries);
    }

    // ---- Aquarius-source modules ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Stars")][InlineData("星星")]
    public void AquariusSourceModulesAreBundledUnderTheirLibraryWithEveryImportName(string name)
    {
        var program = Compile($"變數 s = 匯入(\"{name}\");", Fixtures.Set("stars"));
        // The module and the library file it imports by a relative path; the library's other module is not needed.
        Assert.Equal(["main.aqua", StarsModule, StarsShared], program.Metadata.Modules.Keys);
        Assert.Equal(new Dictionary<string, string> { ["Stars"] = StarsModule, ["星星"] = StarsModule }, program.Metadata.ImportTable);
        Assert.Empty(program.Metadata.NativeLibraries);
        Assert.Equal(["windows", "linux", "android", "web"], program.Metadata.SupportedPlatforms!);
        var loaded = WasmProgram.Load(program.Bytes.ToArray());
        Assert.Equal(program.Metadata.ImportTable, loaded.Metadata.ImportTable);
        Assert.Equal(program.Metadata.Modules.Keys, loaded.Metadata.Modules.Keys);
    }

    [Fact]
    public void ParentRelativeImportsInsideALibraryAreBundled()
    {
        var program = Compile("匯入(\"星圖\");", Fixtures.Set("stars"));
        Assert.Equal(["main.aqua", ChartModule, StarsModule, StarsShared], program.Metadata.Modules.Keys);
        // Stars.aqua is bundled for the chart's relative import, not imported by name: its names stay out of the table.
        Assert.Equal([ "StarChart", "星圖" ], program.Metadata.ImportTable.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ModulesOfOtherLibrariesAreLinkedTransitively()
    {
        var program = Compile("匯入(\"彗星\");", Sky());
        Assert.Equal(["main.aqua", CometsModule, StarsModule, StarsShared], program.Metadata.Modules.Keys);
        Assert.Equal(new Dictionary<string, string> { ["Comets"] = CometsModule, ["Stars"] = StarsModule, ["彗星"] = CometsModule, ["星星"] = StarsModule },
            program.Metadata.ImportTable);
        // Comets.aqua imports the terminal library's native module.
        Assert.Equal(["terminal"], program.Metadata.NativeLibraries);
        // Without the default libraries the native import is an unknown name: nothing to list.
        Assert.Empty(Compile("匯入(\"彗星\");", Fixtures.Set("stars", "comets")).Metadata.NativeLibraries);
    }

    [RuntimeFact]
    public void TheProgramImportsBundledLibraryModulesByBothNamesOnTheNativeRuntime()
    {
        var result = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = """
                變數 a = 匯入("Stars"); 變數 b = import("星星"); 變數 c = 匯入("StarChart"); 變數 d = 匯入("彗星");
                印出(a.名稱, b.名稱, a.亮度(3), b.brightness(1), c.繪製(), c.draw(), d.orbit(), d.終端型別);
                變數 f = 函式() { 匯入("Comets").軌道(); };
                f();
                """,
        }, "main.aqua", libraries: Sky());
        Assert.Equal("星星 星星 30 10 星星圖 星星圖 42 module\n42\n", result.Succeeded().Output);
    }

    [RuntimeFact]
    public void BundledModulesAreCompiledIntoTheApplicationAndNeedNoLibraryFiles()
    {
        using var t = new TestFolder();
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("src/main.aqua", "匯入(\"星星\").亮度(4) + 2;")], wasm, t.FilePath("src"), libraries: Fixtures.Set("stars"));
        Directory.Delete(t.FilePath("src"), true);
        Directory.CreateDirectory(t.FilePath("moved"));
        File.Move(wasm, t.FilePath("moved/app.wasm"));
        Assert.Equal("42\n", WasmTestHost.RunWasm(t.FilePath("moved/app.wasm")).Succeeded().Output);
    }

    [Fact]
    public void TheLibraryFolderIsReservedForLibraryModules()
    {
        var modules = new Dictionary<string, LoweredProgram> { [StarsModule] = new LoweringCompiler().Compile("42;") };
        Assert.Throws<CompilationException>(() => new WasmCompiler(Fixtures.Set("stars")).Compile(modules, StarsModule));
        Assert.Throws<CompilationException>(() => new WasmCompiler().Compile(new Dictionary<string, LoweredProgram> { [".aquarius/Models.aqua"] = new LoweringCompiler().Compile("1;") }, ".aquarius/Models.aqua"));
    }

    [Fact]
    public void LinkingIsDeterministic()
    {
        Assert.Equal(Compile("匯入(\"彗星\"); 匯入(\"Audio\");", Sky()).Bytes.ToArray(), Compile("匯入(\"彗星\"); 匯入(\"Audio\");", Sky()).Bytes.ToArray());
    }

    [Fact]
    public void LibrarySetsRejectAmbiguousModuleAndLibraryNames()
    {
        using var t = new TestFolder();
        t.Write("twin/aqua.toml", "manifest_version = 2\n[library]\nname = \"twin\"\nversion = \"1.0.0\"\nplatforms = [\"windows\"]\n[[modules]]\nnames = \"Twin|星星\"\nsource = \"Twin.aqua\"\n");
        t.Write("twin/Twin.aqua", "1;");
        Assert.Contains("星星", Assert.Throws<InvalidDataException>(() => new LibrarySet([Fixtures.Library("stars"), Library.Load(t.FilePath("twin"))])).Message);
        Assert.Throws<InvalidDataException>(() => new LibrarySet([Fixtures.Library("stars"), Fixtures.Library("stars")]));
    }

    // ---- Declared libraries --------------------------------------------------------------------------------------

    [Fact]
    public void DeclaredLibrariesBundleEveryModuleForImportsByComputedNames()
    {
        var program = Compile("變數 名 = \"Star\" + \"Chart\"; 匯入(名).繪製();", Fixtures.Set("stars"), ["stars"]);
        Assert.Equal(["StarChart", "Stars", "星圖", "星星"], program.Metadata.ImportTable.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(ChartModule, program.Metadata.Modules.Keys); Assert.Contains(StarsShared, program.Metadata.Modules.Keys);
        Assert.Equal(["audio"], Compile("1;", null, ["audio"]).Metadata.NativeLibraries);
        Assert.Contains("neither a default library nor a dependency",
            Assert.Throws<InvalidDataException>(() => Compile("1;", Fixtures.Set("stars"), ["no_such_library"])).Message);
        // The packaging API takes the same declaration.
        using var t = new TestFolder();
        WasmApplication.Compile([t.Write("main.aqua", "1;")], t.FilePath("app.wasm"), t.Path, libraries: Fixtures.Set("stars"), declaredLibraries: ["STARS"]);
        Assert.Equal(4, WasmApplication.Load(t.FilePath("app.wasm")).Program.Metadata.ImportTable.Count);
    }

    [RuntimeFact]
    public void ComputedImportsWorkOnlyForDeclaredLibraries()
    {
        const string source = "變數 名 = \"星\" + \"圖\"; 匯入(名).繪製();";
        using var t = new TestFolder();
        var declared = WasmTestHost.RunWasm(t.Write("declared.wasm", Compile(source, Fixtures.Set("stars"), ["stars"]).Bytes.ToArray()));
        Assert.Equal("星星圖\n", declared.Succeeded().Output);
        var undeclared = WasmTestHost.RunWasm(t.Write("undeclared.wasm", Compile(source, Fixtures.Set("stars")).Bytes.ToArray()));
        Assert.Equal(1, undeclared.ExitCode); Assert.Contains("Module not found: 星圖", undeclared.Output);
    }

    // ---- Platforms -----------------------------------------------------------------------------------------------

    [Fact]
    public void PlatformsNarrowToWhatEveryNeededLibrarySupports()
    {
        var set = Fixtures.Set("stars", "desktop_only");
        Assert.Equal(["windows", "linux"], Compile("匯入(\"桌面限定\"); 匯入(\"Stars\");", set).Metadata.SupportedPlatforms!);
        Assert.Equal(["windows"], Compile("匯入(\"DesktopOnly\");", set, platforms: ["windows"]).Metadata.SupportedPlatforms!);
        Assert.Contains("does not support web", Assert.Throws<InvalidDataException>(() => Compile("匯入(\"DesktopOnly\");", set, platforms: ["web"])).Message);
        // A computed import does not need the library, so it does not narrow the platforms.
        Assert.Null(Compile("變數 n = \"DesktopOnly\"; 匯入(n);", set).Metadata.SupportedPlatforms);
    }

    [Fact]
    public void DesktopOnlyLibrariesMakeWebAndAndroidBuildsFailBeforeWritingOutput()
    {
        using var t = new TestFolder();
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("main.aqua", "匯入(\"DesktopOnly\").值;")], wasm, t.Path, libraries: Fixtures.Set("desktop_only"));
        var application = WasmApplication.Load(wasm);
        application.CheckPlatform("windows"); application.CheckPlatform("linux");
        var web = Assert.Throws<InvalidDataException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, t.FilePath("site"))));
        Assert.Contains("web", web.Message);
        Assert.Throws<InvalidDataException>(() => new AndroidTarget().Build(new WasmBuildRequest(wasm, t.FilePath("app.apk"))));
        Assert.False(Directory.Exists(t.FilePath("site"))); Assert.False(File.Exists(t.FilePath("app.apk")));
    }

    // ---- External libraries --------------------------------------------------------------------------------------

    [Fact]
    public void ExternalLibrariesListTheirNativeLibraryAndAquaRunPassesItsHostBinary()
    {
        using var t = new TestFolder();
        string host = AquariusInstallation.HostTarget;
        var nebula = Fixtures.ExternalLibrary(t, host);
        var set = Fixtures.WithDefaults(nebula);
        var program = Compile("匯入(\"星雲\").名稱;", set);
        Assert.Equal(["nebula"], program.Metadata.NativeLibraries);
        Assert.Equal(new Dictionary<string, string> { ["Nebula"] = ".aquarius/nebula/aqua/Nebula.aqua", ["星雲"] = ".aquarius/nebula/aqua/Nebula.aqua" }, program.Metadata.ImportTable);
        Assert.Equal(["nebula"], Compile("匯入(\"NebulaNative\");", set).Metadata.NativeLibraries);
        string wasm = t.Write("app.wasm", program.Bytes.ToArray());
        var application = WasmApplication.Load(wasm);
        Assert.Equal([t.FilePath($"nebula/native/{host}/{Library.NativeFile("nebula", host)}")], NativeRuntime.LibraryFiles(application, set));
        // Default libraries are part of the runtime: nothing to pass.
        Assert.Empty(NativeRuntime.LibraryFiles(WasmApplication.Load(t.Write("audio.wasm", Compile("匯入(\"Audio\");").Bytes.ToArray())), set));
        File.Delete(t.FilePath($"nebula/native/{host}/{Library.NativeFile("nebula", host)}"));
        Assert.Contains("aqua library build", Assert.Throws<IOException>(() => NativeRuntime.LibraryFiles(application, set)).Message);
    }

    [RuntimeFact]
    public void TheRuntimeRefusesToStartWithoutANeededExternalLibrary()
    {
        using var t = new TestFolder();
        var set = Fixtures.WithDefaults(Fixtures.ExternalLibrary(t));
        var result = WasmTestHost.RunWasm(t.Write("app/app.wasm", Compile("印出(\"RAN\"); 匯入(\"NebulaNative\");", set).Bytes.ToArray()));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("needs the library nebula", result.Error);
        Assert.DoesNotContain("RAN", result.Output);
    }
}
