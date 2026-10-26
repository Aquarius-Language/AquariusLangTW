using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusTests.Bottle;

/// <summary>Bottle with manifest_version 2: applications ([project]) that link libraries ([library] with [native],
/// [[modules]] and [android]) whose modules they import by name, native binaries in snapshots and builds, programs inside
/// library folders, <c>aqua new --type library</c> and <c>aqua library build</c>.</summary>
public class BottleLibraryTest
{
    private const string AllPlatforms = "[\"windows\", \"linux\", \"web\", \"android\"]";
    private static readonly string[] Targets = AquariusInstallation.Targets;

    internal static string WriteLibrary(BottleDirectory t, string directory, string name, string tables, string dependencies = "", string version = "1.0.0", string platforms = AllPlatforms) =>
        t.Write(directory + "/aqua.toml", $"manifest_version = 2\n\n[library]\nname = \"{name}\"\nversion = \"{version}\"\nplatforms = {platforms}\n\n{tables}\n[dependencies]\n{dependencies}\n");

    internal static string Application(BottleDirectory t, string directory, string name, string main, string dependencies = "", string project = "", string platforms = AllPlatforms)
    {
        t.Write(directory + "/main.aqua", main);
        return t.Write(directory + "/aqua.toml", $"manifest_version = 2\n\n[project]\nname = \"{name}\"\nversion = \"1.0.0\"\nentry = \"main.aqua\"\nsources = [\"main.aqua\"]\nplatforms = {platforms}\n{project}\n\n[dependencies]\n{dependencies}\n");
    }

    private const string GreetingDependency = "greeting = { path = \"../greeting\", version = \">= 1.0, < 2.0\" }";

    /// <summary>greeting: module Greeting|問候語 (aqua/Greeting.aqua, which imports aqua/helper.aqua relatively and the
    /// numbers library's module 數字 by name) and a test program; numbers: module Numbers|數字.</summary>
    private static string GreetingLibraries(BottleDirectory t)
    {
        WriteLibrary(t, "numbers", "numbers", "[[modules]]\nnames = \"Numbers|數字\"\nsource = \"aqua/Numbers.aqua\"\n");
        t.Write("numbers/aqua/Numbers.aqua", "變數 answer = 42;\n");
        WriteLibrary(t, "greeting", "greeting", "[[modules]]\nnames = \"Greeting|問候語\"\nsource = \"aqua/Greeting.aqua\"\n",
            "numbers = { path = \"../numbers\", version = \">= 1.0, < 2.0\" }");
        t.Write("greeting/aqua/Greeting.aqua", "變數 輔助 = 匯入(\"helper.aqua\");\n變數 數字 = 匯入(\"數字\");\n變數 問候, greet = 函式(名) { 回傳 輔助.前綴 + 名 + \" \" + 字串.轉文字(數字.answer); };\n");
        t.Write("greeting/aqua/helper.aqua", "變數 前綴 = \"你好, \";\n");
        t.Write("greeting/tests/case.aqua", "變數 g = 匯入(\"問候語\");\n印出(g.greet(\"測試\"));\n");
        return t.FilePath("greeting");
    }

    /// <summary>A library whose native part is prebuilt: placeholder binaries in native/&lt;target&gt;/ for the given
    /// targets, plus files that are not binaries (CMake sources, a log, a build tree).</summary>
    private static string DummyNativeLibrary(BottleDirectory t, string directory, params string[] targets)
    {
        string manifest = WriteLibrary(t, directory, "dummy", "[native]\nmodules = [\"DummyNative|假原生\"]\nsource = \"native\"\n");
        t.Write(directory + "/native/CMakeLists.txt", "# not built by these tests\n");
        t.Write(directory + "/native/src/dummy.c", "/* not built by these tests */\n");
        t.Write(directory + "/native/windows-x64/build.log", "not a binary");
        t.Write(directory + "/build/windows-x64/aqua_dummy.dll", "build tree");
        foreach (string target in targets) t.Write($"{directory}/native/{target}/{Library.NativeFile("dummy", target)}", target + " binary");
        return manifest;
    }

    private static string[] Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private const string Lib = "[library]\nname='lib'\nversion='1.0.0'\nplatforms=['web']\n";
    private const string Module = "[[modules]]\nnames='M'\nsource='aqua/M.aqua'\n";

    [Theory]
    [InlineData("[library]\nname='lib'\nplatforms=['web']\n" + Module, null, "Missing required 'version'")]
    [InlineData("[library]\nname='lib'\nversion='1.0'\nplatforms=['web']\n" + Module, "version", "semantic versions")]
    [InlineData("[library]\nname='lib'\nversion='1.0.0'\n" + Module, null, "'platforms' must list")]
    [InlineData("[library]\nname='lib'\nversion='1.0.0'\nplatforms=['dos']\n" + Module, "platforms", "'platforms' must list")]
    [InlineData("[library]\nname='lib'\nversion='1.0.0'\nplatforms=['web']\ntype='library'\n" + Module, "type", "Unknown library key 'type'")]
    [InlineData("[library]\nname='has space'\nversion='1.0.0'\nplatforms=['web']\n" + Module, "name", "Library names")]
    [InlineData(Lib + "[native]\nname='Bad-Name'\nmodules=['N']\n", "name", "native library name 'Bad-Name'")]
    [InlineData(Lib + "[native]\nmodules=[]\n", "modules", "[native] modules must list")]
    [InlineData(Lib + "[native]\nmodules=['N']\nsources='native'\n", "sources", "Unknown native key 'sources'")]
    [InlineData(Lib + "[native]\nmodules=['N']\nsource='../outside'\n", "source", "relative path inside the library folder")]
    [InlineData("native='yes'\n" + Lib + Module, "native", "[native] must be a table")]
    [InlineData(Lib + "[native]\nmodules=['A|A']\n", "modules", "Invalid or duplicate module name 'A'")]
    [InlineData(Lib + "[native]\nmodules=['Has Space']\n", "modules", "Invalid or duplicate module name 'Has Space'")]
    [InlineData(Lib + "[native]\nmodules=['M']\n" + Module, "names", "Invalid or duplicate module name 'M'")]
    [InlineData(Lib + "[[modules]]\nnames='M'\nsource='aqua/Missing.aqua'\n", "source", "Module source does not exist")]
    [InlineData(Lib + "[[modules]]\nnames='M'\nsource='aqua/M.txt'\n", "source", "must be an .aqua file")]
    [InlineData(Lib + "[[modules]]\nnames='M.aqua'\nsource='aqua/M.aqua'\n", "names", "Invalid or duplicate module name 'M.aqua'")]
    [InlineData(Lib + "[[modules]]\nsource='aqua/M.aqua'\n", null, "Missing required 'names'")]
    [InlineData(Lib + "[[modules]]\nnames='M'\nsource='aqua/M.aqua'\nalias='x'\n", "alias", "Unknown modules key 'alias'")]
    [InlineData("modules='M'\n" + Lib, "modules", "Declare Aquarius-source modules as [[modules]]")]
    [InlineData(Lib, "library", "needs [native] modules, [[modules]] or both")]
    [InlineData(Lib + Module + "[android]\npermissions=['INTERNET']\n", "permissions", "Android permissions")]
    [InlineData(Lib + Module + "[android]\nfeatures=[]\n", "features", "Unknown android key 'features'")]
    [InlineData(Lib + Module + "[desktop]\nhide_terminal=true\n", "desktop", "Unknown manifest key 'desktop'")]
    [InlineData("[project]\nname='app'\ntype='executable'\n", "type", "Unknown project key 'type'")]
    [InlineData("[project]\nname='app'\nlibraries=['audio','Audio']\n", "libraries", "'libraries' lists default library names or dependency names")]
    [InlineData("[project]\nname='app'\nlibraries=['bad name']\n", "libraries", "'libraries' lists default library names or dependency names")]
    [InlineData("[tool]\nx=1\n", null, "Missing [project] or [library] table.")]
    public void Version2ManifestsRejectInvalidLibraryAndProjectTablesWithSourceLocations(string tables, string? key, string error)
    {
        using var t = new BottleDirectory();
        t.Write("aqua/M.aqua", "變數 m = 1;"); t.Write("aqua/M.txt", "text");
        string text = "manifest_version = 2\n" + tables;
        var failure = Assert.Throws<ManifestException>(() => ProjectManifest.Parse(t.FilePath("aqua.toml"), text));
        Assert.Contains(error, failure.Message);
        var issue = Assert.Single(failure.Issues);
        Assert.InRange(issue.Offset, 0, text.Length);
        if (key != null) Assert.Equal(key, text.Substring(issue.Offset, issue.Length));
    }

    [Fact]
    public void Version2IssuesPointAtTheKeyOfTheOffendingTable()
    {
        // The same key appears in several tables of a library manifest; editors underline the one that is wrong.
        using var t = new BottleDirectory();
        t.Write("aqua/M.txt", "text");
        foreach (var (text, offending) in new[] {
                     ("manifest_version = 2\n[library]\nname = 'lib'\nversion = '1.0.0'\nplatforms = ['web']\n[native]\nname = 'Bad-Name'\nmodules = ['N']\n", "name = 'Bad-Name'"),
                     ("manifest_version = 2\n[library]\nname = 'lib'\nversion = '1.0.0'\nplatforms = ['web']\n[native]\nmodules = ['N']\nsource = 'native'\n[[modules]]\nnames = 'M'\nsource = 'aqua/M.txt'\n", "source = 'aqua/M.txt'"),
                 }) {
            var issue = Assert.Single(Assert.Throws<ManifestException>(() => ProjectManifest.Parse(t.FilePath("aqua.toml"), text)).Issues);
            Assert.Equal(text.IndexOf(offending, StringComparison.Ordinal), issue.Offset);
        }
    }

    [Fact]
    public void ManifestVersionsAreOneOrTwoAndVersion2ProjectsAreApplications()
    {
        ProjectManifest Parse(string text) => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), text);
        foreach (string version in new[] { "3", "0", "'2'" })
            Assert.Contains("manifest_version must be 1 or 2", Assert.Throws<ManifestException>(() => Parse($"manifest_version = {version}\n[project]\nname='app'\n")).Message);
        Assert.Equal(1, Parse("[project]\nname='app'\ntype='executable'\n").ManifestVersion);
        // Version 1 manifests know no [library] table.
        Assert.Contains("Unknown manifest key 'library'", Assert.Throws<ManifestException>(() => Parse("[library]\nname='lib'\n")).Message);
        var app = Parse("manifest_version = 2\n[project]\nname='app'\nlibraries=['audio', 'greeting']\n");
        Assert.Equal(2, app.ManifestVersion); Assert.Equal("executable", app.Type); Assert.Null(app.Version); Assert.Null(app.Library); Assert.False(app.IsModuleLibrary);
        Assert.Equal(new[] { "audio", "greeting" }, app.Libraries);
        Assert.Equal("main.aqua", app.Entry);
    }

    [Fact]
    public void Version2LibraryManifestsDescribeNativeAndAquariusModules()
    {
        using var t = new BottleDirectory();
        t.Write("aqua/SQLite.aqua", "變數 開啟, open = 函式() { 1; };");
        t.Write("aqua/extra/util.aqua", "變數 util = 1;");
        t.Write("tests/case.aqua", "匯入(\"SQLite\");");
        t.Write("native/src/generated.aqua", "變數 notASource = 1;");
        string file = t.Write("aqua.toml", "manifest_version = 2\n\n[library]\nname = \"sqlite\"\nversion = \"1.0.0\"\ndescription = \"SQLite for AquariusLang\"\nlicense = \"MIT\"\n" +
            "platforms = [\"Windows\", \"web\"]\n\n[native]\nmodules = [\"SQLiteNative|資料庫原生\"]\nsource = \"native\"\n\n[[modules]]\nnames = \"SQLite|資料庫\"\nsource = \"aqua/SQLite.aqua\"\n\n" +
            "[android]\npermissions = [\"android.permission.INTERNET\"]\n\n[dependencies]\nzlib = \">= 1.0, < 2.0\"\n\n[[assets]]\nsource = \"data\"\n");
        var manifest = ProjectManifest.Load(t.Path);
        Assert.Equal(2, manifest.ManifestVersion); Assert.Equal("library", manifest.Type); Assert.Equal("sqlite", manifest.Name); Assert.Equal("1.0.0", manifest.Version);
        Assert.True(manifest.IsModuleLibrary);
        Assert.Equal(new[] { "windows", "web" }, manifest.Platforms);
        Assert.Equal("aqua/SQLite.aqua", manifest.Entry); Assert.Equal(new[] { "aqua/SQLite.aqua" }, manifest.Sources);
        Assert.Equal(">= 1.0, < 2.0", manifest.Dependencies["zlib"].Version);
        Assert.Equal("data", Assert.Single(manifest.Assets).Source);
        var library = manifest.Library!;
        Assert.True(library.External); Assert.Equal(file, library.ManifestPath);
        Assert.Equal("sqlite", library.NativeName); // [native] name defaults to the library name
        Assert.Equal(new[] { "SQLiteNative", "資料庫原生" }, Assert.Single(library.NativeModules));
        Assert.Equal("native", library.NativeSource);
        var module = Assert.Single(library.Modules);
        Assert.Equal(new[] { "SQLite", "資料庫" }, module.Names); Assert.Equal("aqua/SQLite.aqua", module.Source);
        Assert.Equal(new[] { "android.permission.INTERNET" }, library.AndroidPermissions);
        Assert.Equal("SQLite for AquariusLang", library.Description); Assert.Equal("MIT", library.License);
        // Its sources are the .aqua files outside tests/ and native/ (modules may import them relatively).
        Assert.Equal(new[] { "aqua/SQLite.aqua", "aqua/extra/util.aqua" }, manifest.SourceFiles().Select(f => Path.GetRelativePath(t.Path, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Empty(manifest.NativeBinaries());
    }

    [RuntimeFact]
    public void ApplicationsImportLibraryModulesByEitherNameAndRunFromTheCache()
    {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        GreetingLibraries(t);
        string app = Application(t, "app", "app", "變數 g = 匯入(\"Greeting\");\n印出(g.greet(\"Aquarius\"));\n變數 問 = 匯入(\"問候語\");\n印出(問.問候(\"世界\"));\n", GreetingDependency);
        string output = t.FilePath("app.wasm");
        var build = ProjectBuilder.Compile(app, output, cache: cache);
        Assert.True(build.Libraries.Find("greeting") is { External: true, HasNative: false });
        Assert.NotNull(build.Libraries.Find("numbers"));
        var package = WasmApplication.Load(output);
        foreach (string script in new[] { ".aquarius/greeting/aqua/Greeting.aqua", ".aquarius/greeting/aqua/helper.aqua", ".aquarius/numbers/aqua/Numbers.aqua" })
            Assert.Contains(script, package.Scripts.Keys);
        var imports = package.Program.Metadata.ImportTable;
        Assert.Equal(".aquarius/greeting/aqua/Greeting.aqua", imports["Greeting"]); Assert.Equal(".aquarius/greeting/aqua/Greeting.aqua", imports["問候語"]);
        Assert.Equal(".aquarius/numbers/aqua/Numbers.aqua", imports["數字"]); Assert.Equal(".aquarius/numbers/aqua/Numbers.aqua", imports["Numbers"]);
        Assert.DoesNotContain(package.Scripts.Keys, k => k.StartsWith("bottles/", StringComparison.Ordinal));
        Assert.Empty(package.NativeLibraries);
        const string expected = "你好, Aquarius 42\n你好, 世界 42\n";
        Assert.Equal(expected, WasmTestHost.RunWasm(output).Succeeded().Output);
        // The snapshot holds the manifest and the library's sources, not its tests.
        string snapshot = cache.ContentRoot(cache.List("greeting").Single());
        Assert.Equal(new[] { "aqua.toml", "aqua/Greeting.aqua", "aqua/helper.aqua" }, Files(snapshot));
        Directory.Delete(t.FilePath("greeting"), true); Directory.Delete(t.FilePath("numbers"), true);
        string offline = t.FilePath("offline.wasm");
        var cached = ProjectBuilder.Compile(app, offline, locked: true, offline: true, cache: cache);
        Assert.Equal(Path.GetFullPath(snapshot), cached.Libraries.Find("greeting")!.Root);
        Assert.Equal(expected, WasmTestHost.RunWasm(offline).Succeeded().Output);
    }

    [RuntimeFact]
    public void DeclaredLibrariesLinkModulesImportedByComputedNames()
    {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        GreetingLibraries(t);
        const string main = "變數 名 = \"Gree\" + \"ting\";\n變數 g = 匯入(名);\n印出(g.greet(\"動態\"));\n";
        string app = Application(t, "app", "app", main, GreetingDependency);
        string plain = t.FilePath("plain.wasm");
        ProjectBuilder.Compile(app, plain, cache: cache);
        Assert.Empty(WasmApplication.Load(plain).Program.Metadata.ImportTable);
        var missing = WasmTestHost.RunWasm(plain);
        Assert.Equal(1, missing.ExitCode); Assert.Contains("Module not found: Greeting", missing.Output);

        Application(t, "app", "app", main, GreetingDependency, "libraries = [\"greeting\"]");
        Assert.Equal(new[] { "greeting" }, ProjectManifest.Load(app).Libraries);
        string declared = t.FilePath("declared.wasm");
        ProjectBuilder.Compile(app, declared, cache: cache);
        var imports = WasmApplication.Load(declared).Program.Metadata.ImportTable;
        Assert.Contains("Greeting", imports.Keys); Assert.Contains("問候語", imports.Keys); Assert.Contains("數字", imports.Keys);
        Assert.Equal("你好, 動態 42\n", WasmTestHost.RunWasm(declared).Succeeded().Output);

        Application(t, "app", "app", main, GreetingDependency, "libraries = [\"missing\"]");
        Assert.Contains("neither a default library nor a dependency", Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), cache: cache)).Message);
        // A default library with a native part can be declared too: the application then lists it for the runtime.
        if (LibrarySet.Default.Libraries.FirstOrDefault(l => l.HasNative && l.Platforms.Contains("windows") && l.Platforms.Contains("web")) is { } builtIn) {
            Application(t, "app", "app", main, GreetingDependency, $"libraries = [\"greeting\", \"{builtIn.Name}\"]");
            ProjectBuilder.Compile(app, declared, cache: cache);
            Assert.Contains(builtIn.NativeName!, WasmApplication.Load(declared).NativeLibraries);
        }
    }

    [Fact]
    public void LibrarySnapshotsCarryPrebuiltNativeBinariesForEveryTarget()
    {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string libraryManifest = DummyNativeLibrary(t, "dummy", Targets);
        Assert.Equal(Targets.Select(target => $"native/{target}/{Library.NativeFile("dummy", target)}"),
            ProjectManifest.Load(libraryManifest).NativeBinaries().Select(f => Path.GetRelativePath(t.FilePath("dummy"), f).Replace('\\', '/')));
        string app = Application(t, "app", "app", "變數 d = 匯入(\"DummyNative\");\n", "dummy = { path = \"../dummy\", version = \"1.0.0\" }");
        string output = t.FilePath("app.wasm");
        var build = ProjectBuilder.Compile(app, output, cache: cache);
        string snapshot = cache.ContentRoot(cache.List("dummy").Single());
        Assert.Equal(new[] { "aqua.toml" }.Concat(Targets.Select(target => $"native/{target}/{Library.NativeFile("dummy", target)}")).Order(StringComparer.Ordinal), Files(snapshot));
        var dummy = build.Libraries.Find("dummy")!;
        Assert.True(dummy.External); Assert.Equal("dummy", dummy.NativeName); Assert.Equal(Path.GetFullPath(snapshot), dummy.Root);
        Assert.Equal(new[] { "DummyNative", "假原生" }, Assert.Single(dummy.NativeModules));
        foreach (string target in Targets) Assert.Equal(target + " binary", File.ReadAllText(dummy.NativeBinary(target)!));
        var application = WasmApplication.Load(output);
        Assert.Equal(new[] { "dummy" }, application.NativeLibraries);
        // aqua run passes the host target's binary to the runtime as --library.
        Assert.Equal(new[] { dummy.NativeBinary(AquariusInstallation.HostTarget)! }, NativeRuntime.LibraryFiles(application, build.Libraries));
        Assert.Empty(NativeRuntime.LibraryFiles(application, LibrarySet.Default));

        // The cache serves the binaries once the library's source is gone.
        Directory.Delete(t.FilePath("dummy"), true);
        var offline = ProjectBuilder.Compile(app, output, locked: true, offline: true, cache: cache);
        Assert.Equal(dummy.NativeBinary("web"), offline.Libraries.Find("dummy")!.NativeBinary("web"));
        Assert.Equal(dummy.NativeBinary("windows-x64"), ProjectBuilder.Inspect(ProjectManifest.Load(app), cache).Find("dummy")!.NativeBinary("windows-x64"));
        // Binaries are part of the snapshot's integrity hash.
        File.WriteAllText(dummy.NativeBinary("web")!, "tampered");
        Assert.Contains("integrity", Assert.Throws<InvalidDataException>(() => new BottleManager(cache).Restore(ProjectManifest.Load(app))).Message);
    }

    [Fact]
    public void ExportsShipExternalLibraryBinariesBesideExecutablesAndInWebsites()
    {
        using var t = new BottleDirectory();
        DummyNativeLibrary(t, "dummy", "windows-x64", "web");
        Application(t, "app", "app", "變數 d = 匯入(\"假原生\");\n", "dummy = { path = \"../dummy\", version = \"1.0.0\" }");
        var environment = SyntheticPacks.Create(t, "packs", [("windows-x64", "aquarius.exe", SyntheticPacks.PeTemplate), ("linux-x64", "aquarius", "linux runtime"u8.ToArray()), .. SyntheticPacks.Web]);
        AquaCli.Run(t, environment, "build", "app", "--target", "windows", "-o", "windows/app.exe").Succeeded();
        Assert.Equal("windows-x64 binary", File.ReadAllText(t.FilePath("windows/libraries/aqua_dummy.dll")));
        AquaCli.Run(t, environment, "build", "app", "--target", "web", "-o", "site").Succeeded();
        Assert.Equal("web binary", File.ReadAllText(t.FilePath("site/runtime/libraries/dummy.wasm")));
        // A target the library has no binary for fails before writing anything.
        var linux = AquaCli.Run(t, environment, "build", "app", "--target", "linux", "-o", "linux/app");
        Assert.Equal(1, linux.Code); Assert.Contains("has no linux-x64 binary", linux.Error);
        Assert.False(File.Exists(t.FilePath("linux/app")));
    }

    [RuntimeFact]
    public void NewLibraryTemplateIsABuildableVersion2Library()
    {
        using var t = new BottleDirectory();
        AquaCli.Run(t, "new", "mathlib", "--type", "library", "-o", "mathlib").Succeeded();
        var manifest = ProjectManifest.Load(t.FilePath("mathlib"));
        Assert.Equal(2, manifest.ManifestVersion); Assert.True(manifest.IsModuleLibrary); Assert.Equal("library", manifest.Type);
        Assert.Equal("0.1.0", manifest.Version); Assert.Equal(new[] { "windows", "linux", "web", "android" }, manifest.Platforms);
        var module = Assert.Single(manifest.Library!.Modules);
        Assert.Equal(new[] { "mathlib" }, module.Names); Assert.Equal("main.aqua", module.Source); Assert.Null(manifest.Library.NativeName);
        t.Write("mathlib/tests/case.aqua", "變數 m = 匯入(\"mathlib\");\n印出(m.add(20, 22));\n印出(m.加法(1, 2));\n");
        AquaCli.Run(t, "build", "mathlib/tests/case.aqua", "--root", "mathlib/tests", "-o", "case.wasm").Succeeded();
        Assert.Contains(".aquarius/mathlib/main.aqua", WasmApplication.Load(t.FilePath("case.wasm")).Scripts.Keys);
        Assert.Equal("42\n3\n", WasmTestHost.RunWasm(t.FilePath("case.wasm")).Succeeded().Output);
        Assert.Equal("42\n3\n", AquaCli.Run(t, "run", "mathlib/tests/case.aqua").Succeeded().Output);
        var again = AquaCli.Run(t, "new", "mathlib", "--type", "library", "-o", "mathlib");
        Assert.Equal(1, again.Code); Assert.Contains("already exists", again.Error);
        Assert.Equal(2, AquaCli.Run(t, "new", "other", "--type", "plugin", "-o", "other").Code);
        AquaCli.Run(t, "bottle", "init", "--name", "工具", "--type", "library", "--project", "tools").Succeeded();
        Assert.Equal(new[] { "工具" }, ProjectManifest.Load(t.FilePath("tools")).Library!.Modules.Single().Names);
    }

    [RuntimeFact]
    public void CliCreatesInstallsBuildsRunsAndUninstallsVersion2Libraries()
    {
        using var t = new BottleDirectory();
        AquaCli.Run(t, "new", "demo", "-o", "app").Succeeded();
        AquaCli.Run(t, "new", "mathlib", "--type", "library", "-o", "app/vendor/mathlib").Succeeded();
        t.Write("app/main.aqua", "變數 m=匯入(\"mathlib\");印出(m.add(20,22));印出(m.加法(1,2));");
        AquaCli.Run(t, "bottle", "install", "app/vendor/mathlib", "--project", "app", "--version", ">= 0.1, < 0.2").Succeeded();
        Assert.Equal(">= 0.1, < 0.2", ProjectManifest.Load(t.FilePath("app")).Dependencies["mathlib"].Version);
        AquaCli.Run(t, "build", "app", "-o", "app.wasm", "--locked", "--offline").Succeeded();
        Assert.Equal(".aquarius/mathlib/main.aqua", WasmApplication.Load(t.FilePath("app.wasm")).Program.Metadata.ImportTable["mathlib"]);
        Assert.Equal("42\n3\n", AquaCli.Run(t, "run", "app.wasm").Succeeded().Output);
        Assert.Equal("42\n3\n", AquaCli.Run(t, "run", "app").Succeeded().Output);
        AquaCli.Run(t, "bottle", "uninstall", "mathlib", "--project", "app").Succeeded();
        Assert.Empty(ProjectManifest.Load(t.FilePath("app")).Dependencies);
        Assert.Single(new BottleCache(t.FilePath("cache")).List("mathlib"));
        // Without the dependency the import names no module.
        var missing = AquaCli.Run(t, "run", "app");
        Assert.NotEqual(0, missing.Code); Assert.Contains("mathlib", missing.Output + missing.Error);
    }

    [RuntimeFact]
    public void ProgramsInsideALibraryFolderBuildAsPlainProgramsThatImportTheLibrary()
    {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string greeting = GreetingLibraries(t);
        AquaCli.Run(t, "build", "greeting/tests/case.aqua", "--root", "greeting/tests", "-o", "case.wasm").Succeeded();
        var package = WasmApplication.Load(t.FilePath("case.wasm"));
        Assert.Equal("case.aqua", package.EntryPoint);
        Assert.Contains(".aquarius/greeting/aqua/Greeting.aqua", package.Scripts.Keys); Assert.Contains(".aquarius/numbers/aqua/Numbers.aqua", package.Scripts.Keys);
        Assert.Equal("你好, 測試 42\n", WasmTestHost.RunWasm(t.FilePath("case.wasm")).Succeeded().Output);
        Assert.Equal("你好, 測試 42\n", AquaCli.Run(t, "run", "greeting/tests/case.aqua").Succeeded().Output);
        // The [library] manifest is not a project: without a program there is nothing to build or run.
        Assert.Equal(2, AquaCli.RunIn(t, null, greeting, "build", "-o", "x.wasm").Code);
        var library = AquaCli.Run(t, "build", "greeting", "-o", "x.wasm");
        Assert.Equal(1, library.Code); Assert.Contains("is a library", library.Error);
        Assert.Equal(1, AquaCli.Run(t, "run", "greeting").Code);
        Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(greeting, t.FilePath("x.wasm"), cache: cache));
        Assert.False(File.Exists(t.FilePath("x.wasm")));
        // What its programs can import: the library itself and its dependencies.
        var set = ProjectBuilder.Inspect(ProjectManifest.Load(greeting), cache);
        Assert.Equal("greeting", set.OwnerOf("問候語")!.Name); Assert.Equal("numbers", set.OwnerOf("Numbers")!.Name);
    }

    [Fact]
    public void LibraryBuildValidatesItsArgumentsAndManifest()
    {
        using var t = new BottleDirectory();
        GreetingLibraries(t);
        Application(t, "app", "app", "印出(1);\n");
        NativeToolchain.WriteTinyLibrary(t.FilePath("tiny"));
        WriteLibrary(t, "nocmake", "nocmake", "[native]\nmodules = [\"NoCMake\"]\nsource = \"native\"\n");
        WriteLibrary(t, "nosource", "nosource", "[native]\nmodules = [\"NoSource\"]\n");
        // Tool paths are only used once a build starts; these checks come first.
        var tools = new Dictionary<string, string> { ["AQUARIUS_CMAKE"] = "missing-cmake", ["AQUARIUS_NINJA"] = "missing-ninja" };
        Assert.Equal(2, AquaCli.Run(t, "library").Code);
        Assert.Equal(2, AquaCli.Run(t, "library", "pack", "tiny").Code);
        Assert.Equal(2, AquaCli.Run(t, "library", "build", "tiny", "--targets").Code);
        Assert.Equal(2, AquaCli.Run(t, "library", "build", "tiny", "--targets", "web", "--targets", "web").Code);
        Assert.Equal(2, AquaCli.Run(t, "library", "build", "tiny", "--verbose").Code);
        var unknown = AquaCli.Run(t, tools, "library", "build", "tiny", "--targets", "windows-arm64");
        Assert.Equal(2, unknown.Code); Assert.Contains("Unknown target windows-arm64", unknown.Error);
        // Native binaries are built on their own operating system.
        string foreign = OperatingSystem.IsWindows() ? "linux-x64" : "windows-x64";
        var cross = AquaCli.Run(t, tools, "library", "build", "tiny", "--targets", foreign);
        Assert.Equal(2, cross.Code); Assert.Contains($"Build {foreign} binaries on", cross.Error);
        foreach (var (folder, error) in new[] {
                     ("greeting", "has no native part to build"), ("nosource", "has no native part to build"), ("app", "is not a library manifest"),
                     ("nocmake", "has no CMakeLists.txt") }) {
            var result = AquaCli.Run(t, tools, "library", "build", folder);
            Assert.Equal(1, result.Code); Assert.Contains(error, result.Error);
        }
        Assert.Equal(1, AquaCli.Run(t, "library", "build", "missing").Code);
        Assert.False(Directory.Exists(t.FilePath("tiny/build")));
        Assert.False(Directory.Exists(t.FilePath("tiny/native/windows-x64")));
    }

    [NativeToolchainFact]
    public void CppLibrariesExportAnEntryPointTheRuntimeCanLoad()
    {
        using var t = new BottleDirectory();
        string folder = NativeToolchain.WriteTinyLibrary(t.FilePath("cpp"));
        string cmake = Path.Combine(folder, "native", "CMakeLists.txt");
        File.WriteAllText(cmake, File.ReadAllText(cmake).Replace("project(aqua_tiny C)", "project(aqua_tiny C CXX)").Replace("src/tiny.c", "src/tiny.cpp"));
        File.Move(Path.Combine(folder, "native", "src", "tiny.c"), Path.Combine(folder, "native", "src", "tiny.cpp"));
        AquaCli.Run(t, NativeToolchain.BuildEnvironment, "library", "build", folder, "--targets", "windows-x64").Succeeded();
        Assert.Equal("42\ntiny native\n", AquaCli.Run(t, "run", Path.Combine(folder, "tests", "case.aqua")).Succeeded().Output);
    }

    [NativeToolchainFact]
    public void ExternalLibrariesBuildTheirNativePartAndRunWithIt()
    {
        // aqua library build tiny --targets windows-x64, with CMake and Ninja from AQUARIUS_CMAKE / AQUARIUS_NINJA.
        string tiny = NativeToolchain.BuiltTinyLibrary;
        string dll = Path.Combine(tiny, "native", "windows-x64", "aqua_tiny.dll");
        Assert.True(File.Exists(dll));
        Assert.True(Directory.Exists(Path.Combine(tiny, "build", "windows-x64")));
        using var t = new BottleDirectory();
        // A test program inside the library loads the library's binary for the host.
        Assert.Equal("42\ntiny native\n", AquaCli.Run(t, "run", Path.Combine(tiny, "tests", "case.aqua")).Succeeded().Output);
        // The same through the native runtime: the library set names the binary to pass as --library.
        var libraries = ProjectBuilder.Inspect(ProjectManifest.Load(tiny));
        Assert.Equal(dll, libraries.Find("tiny")!.NativeBinary("windows-x64"));
        string wasm = t.FilePath("case.wasm");
        WasmApplication.Compile([Path.Combine(tiny, "tests", "case.aqua")], wasm, Path.Combine(tiny, "tests"), libraries: libraries);
        var application = WasmApplication.Load(wasm);
        Assert.Equal(new[] { "tiny" }, application.NativeLibraries);
        var files = NativeRuntime.LibraryFiles(application, libraries);
        Assert.Equal(new[] { dll }, files);
        Assert.Equal("42\ntiny native\n", WasmTestHost.RunWasm(wasm, libraries: files).Succeeded().Output);
        var without = WasmTestHost.RunWasm(wasm);
        Assert.NotEqual(0, without.ExitCode); Assert.Contains("aqua_tiny.dll", without.Output + without.Error);
        // The runtime describes the external library's module for editors.
        string catalog = NativeRuntime.Catalog(files);
        Assert.Contains("\"TinyNative\"", catalog); Assert.Contains("兩倍", catalog);

        // An application installs it from Bottle; aqua run loads the cached binary.
        Application(t, "app", "app", "變數 小 = 匯入(\"小原生\");\n印出(小.兩倍(4));\n", platforms: "[\"windows\"]");
        AquaCli.Run(t, "bottle", "install", tiny, "--project", "app").Succeeded();
        var snapshot = new BottleCache(t.FilePath("cache"));
        Assert.Equal(new[] { "aqua.toml", "native/windows-x64/aqua_tiny.dll" }, Files(snapshot.ContentRoot(snapshot.List("tiny").Single())));
        Assert.Equal("8\n", AquaCli.Run(t, "run", "app").Succeeded().Output);
        AquaCli.Run(t, "build", "app", "-o", "app/dist/app.wasm").Succeeded();
        Assert.Equal("8\n", AquaCli.Run(t, "run", "app/dist/app.wasm").Succeeded().Output);
    }
}
