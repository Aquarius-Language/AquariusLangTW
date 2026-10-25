using System.Reflection.PortableExecutable;
using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Build.Targets;

namespace AquariusTests.Bottle;

/// <summary>Bottle with manifest_version 1 projects and libraries (sources imported by path from /bottles/NAME/), the
/// manifest contract shared by both versions, locks, the cache and the command line.</summary>
public class BottleTest {
    private static string Manifest(string name, string type = "library", string version = "0.18.0", string dependencies = "", string assets = "", string platforms = "[\"windows\", \"linux\", \"web\", \"android\"]") => $$"""
        manifest_version = 1
        [project]
        name = "{{name}}"
        type = "{{type}}"
        version = "{{version}}"
        entry = "main.aqua"
        sources = ["main.aqua", "src"]
        platforms = {{platforms}}
        [dependencies]
        {{dependencies}}
        {{assets}}
        """;
    internal static string Project(BottleDirectory t, string directory, string name, string type = "library", string version = "0.18.0", string dependencies = "", string assets = "", string? platforms = null) {
        t.Write(directory + "/main.aqua", "變數 value = 42;");
        t.Write(directory + "/src/helper.aqua", "變數 helper = 1;");
        return t.Write(directory + "/aqua.toml", Manifest(name, type, version, dependencies, assets, platforms ?? "[\"windows\", \"linux\", \"web\", \"android\"]"));
    }

    [Theory]
    [InlineData("> 0.17, <= 0.21", "0.18.0", true)]
    [InlineData("> 0.17, <= 0.21", "0.21.1", false)]
    [InlineData(">= 1.2.3, < 2.0", "1.2.3+build.5", true)]
    [InlineData(">= 1.2.3, < 2.0", "1.3.0-beta.1", false)]
    [InlineData(">= 1.3.0-beta.2, < 2", "1.3.0-beta.11", true)]
    [InlineData("= 1.0.0", "1.0.0+foo", true)]
    public void SemanticVersionsRespectRangesAndPrereleaseOptIn(string requirement, string version, bool expected) => Assert.Equal(expected, new VersionRequirement(requirement).Matches(BottleVersion.Parse(version)));

    [Theory]
    [InlineData("[project]\nname='has space'\ntype='executable'", "whitespace")]
    [InlineData("[project]\nname='lib'\ntype='library'", "version")]
    [InlineData("[project]\nname='lib'\ntype='library'\nversion='1.0'", "semantic version")]
    [InlineData("[project]\nname='lib'\ntype='library'\nversion='1.0.0'", "platforms")]
    [InlineData("[project]\nname='app'\ntype='executable'\nentry='../main.aqua'", "path")]
    [InlineData("[project]\nname='app'\ntype='executable'\n[dependencies]\nlib={path='lib'}", "version")]
    [InlineData("[project]\nname='app'\ntype='executable'\n[dependencies]\nlib={version='1.0.0',git='https://example.com/repo'}", "GitHub")]
    [InlineData("[project]\nname='app'\ntype='executable'\nplatfroms=['web']", "Unknown")]
    [InlineData("[project]\nname='app'\nname='again'", "defined")]
    public void ManifestRejectsInvalidContractsWithSourceLocations(string text, string error) {
        var failure = Assert.Throws<ManifestException>(() => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), text));
        Assert.Contains(error.ToLowerInvariant(), failure.Message.ToLowerInvariant()); Assert.NotEmpty(failure.Issues);
        Assert.All(failure.Issues, issue => Assert.InRange(issue.Offset, 0, text.Length));
    }
    [Fact] public void OnnxCudaIsOffByDefaultAndOptInThroughTheManifest() {
        string Text(string onnx) => "[project]\nname = 'app'\ntype = 'executable'\n" + onnx;
        Assert.False(ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), Text("")).Onnx.Cuda);
        Assert.True(ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), Text("[onnx]\ncuda = true\n")).Onnx.Cuda);
        Assert.False(ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), Text("[onnx]\ncuda = false\n")).Onnx.Cuda);
        Assert.Contains("true or false", Assert.Throws<ManifestException>(() => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), Text("[onnx]\ncuda = 'yes'\n"))).Message);
        Assert.Contains("Unknown onnx key", Assert.Throws<ManifestException>(() => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), Text("[onnx]\ncudaa = true\n"))).Message);
        Assert.Contains("table", Assert.Throws<ManifestException>(() => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), "onnx = true\n" + Text(""))).Message);
    }
    [Fact] public void DesktopTerminalRemainsVisibleUnlessTheManifestOptsIn() {
        string Text(string desktop) => "[project]\nname = 'app'\ntype = 'executable'\n" + desktop;
        ProjectManifest Parse(string text) => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), text);
        Assert.False(Parse(Text("")).Desktop.HideTerminal);
        Assert.False(Parse(Text("[desktop]\n")).Desktop.HideTerminal);
        Assert.True(Parse(Text("[desktop]\nhide_terminal = true\n")).Desktop.HideTerminal);
        Assert.False(Parse(Text("[desktop]\nhide_terminal = false\n")).Desktop.HideTerminal);
        Assert.Contains("true or false", Assert.Throws<ManifestException>(() => Parse(Text("[desktop]\nhide_terminal = 'true'\n"))).Message);
        Assert.Contains("Unknown desktop key", Assert.Throws<ManifestException>(() => Parse(Text("[desktop]\nhide_termnal = true\n"))).Message);
        Assert.Contains("table", Assert.Throws<ManifestException>(() => Parse("desktop = true\n" + Text(""))).Message);
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData("[desktop]\nhide_terminal = true", null, true)]
    [InlineData("[desktop]\nhide_terminal = true", "false", false)]
    [InlineData("[desktop]\nhide_terminal = false", "true", true)]
    public void CliWindowsBuildsUseDesktopSettingsWithExplicitCliPrecedence(string settings, string? cli, bool hidden) {
        using var t = new BottleDirectory();
        string app = Project(t, "app", "app", "executable", assets: settings);
        var environment = SyntheticPacks.Create(t, "packs", [("windows-x64", "aquarius.exe", SyntheticPacks.PeTemplate), .. SyntheticPacks.Web]);
        var args = new List<string> { "build", app, "--target", "windows", "-o", "app.exe" };
        if (cli != null) args.AddRange(["--hide-terminal", cli]);
        AquaCli.Run(t, environment, args.ToArray()).Succeeded();
        Assert.Equal(hidden ? Subsystem.WindowsGui : Subsystem.WindowsCui, SyntheticPacks.ReadSubsystem(t.FilePath("app.exe")));
        // Desktop settings are harmless for intermediate Wasm and browser exports.
        AquaCli.Run(t, "build", app, "-o", "app.wasm").Succeeded();
        AquaCli.Run(t, environment, "build", app, "--target", "web", "-o", "web").Succeeded();
    }
    [Fact] public void CliExportsUseTheManifestOnnxCudaSettingUnlessTheCommandLineOverridesIt() {
        using var t = new BottleDirectory();
        string app = Project(t, "app", "app", "executable", assets: "[onnx]\ncuda = true");
        var environment = SyntheticPacks.Create(t, "packs", [("windows-x64-cuda", "aquarius.exe", "synthetic runtime template windows-x64-cuda"u8.ToArray()), .. SyntheticPacks.Web]);
        AquaCli.Run(t, environment, "build", app, "--target", "windows", "-o", "cuda.exe").Succeeded();
        Assert.StartsWith("synthetic runtime template windows-x64-cuda", File.ReadAllText(t.FilePath("cuda.exe")));
        var standard = AquaCli.Run(t, environment, "build", app, "--target", "windows", "-o", "standard.exe", "--onnx-cuda", "false");
        Assert.Equal(1, standard.Code); Assert.Contains("windows-x64 runtime pack is missing", standard.Error);
        Assert.Equal(2, AquaCli.Run(t, environment, "build", app, "--target", "web", "-o", "site", "--onnx-cuda", "true").Code);
        // Web exports of the same project ignore the desktop-only setting.
        AquaCli.Run(t, environment, "build", app, "--target", "web", "-o", "site").Succeeded();
    }
    [Fact] public void ExecutablesCanOmitVersionAndTomlSupportsMultilineArraysAndComments() {
        var project = ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), "# project\n[project]\nname = '我的應用'\ntype = 'executable'\nsources = [\n 'main.aqua', # file\n]\n");
        Assert.Null(project.Version); Assert.Equal("我的應用", project.Name); Assert.Equal(1, project.ManifestVersion); Assert.False(project.IsModuleLibrary);
    }
    [Fact] public void NestedLibrariesAndAssetsAreCachedAndBuildAfterSourcesDisappear() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", dependencies: "greeting = { path='vendor/greeting', version='> 0.17, <= 0.21' }");
        Project(t, "app/vendor/greeting", "greeting", dependencies: "numbers = { path='deps/numbers/aqua.toml', version='0.18.0' }", assets: "[[assets]]\nsource='assets'\n[[assets]]\nsource='native/plugin.dll'\ntarget='native/plugin.dll'");
        Project(t, "app/vendor/greeting/deps/numbers", "numbers");
        t.Write("app/vendor/greeting/assets/message.txt", "cached greeting"); t.Write("app/vendor/greeting/native/plugin.dll", "native fixture");
        t.Write("app/main.aqua", "變數 g=匯入(\"/bottles/greeting/main.aqua\");g.value;");
        var manager = new BottleManager(cache); var resolved = manager.Restore(ProjectManifest.Load(app));
        Assert.Equal(2, resolved.Packages.Count); Assert.False(File.Exists(t.FilePath("app/vendor/greeting/aqua.lock.json")));
        var inspector = ProjectInspector.Libraries(ProjectManifest.Load(app), cache); Assert.Equal(2, inspector.Count);
        Directory.Delete(t.FilePath("app/vendor"), true);
        string output = t.FilePath("build/app.wasm"); var build = ProjectBuilder.Compile(app, output, locked: true, offline: true, cache: cache);
        var package = WasmApplication.Load(output);
        Assert.Contains("bottles/greeting/main.aqua", package.Scripts.Keys); Assert.Contains("bottles/numbers/main.aqua", package.Scripts.Keys);
        Assert.Contains("bottles/greeting/assets/message.txt", package.Assets.Keys); Assert.Contains("native/plugin.dll", package.Assets.Keys);
        Assert.DoesNotContain("bottles/greeting/deps/numbers/main.aqua", package.Scripts.Keys);
        // Version 1 libraries are sources and assets only: they add no library to link and no native library to load.
        Assert.Null(build.Libraries.Find("greeting")); Assert.Empty(package.NativeLibraries);
    }
    [RuntimeFact] public void Version1LibrariesImportEachOtherByBottlePathsAndRun() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", dependencies: "greeting = { path='../greeting', version='0.18.0' }");
        Project(t, "greeting", "greeting", dependencies: "numbers = { path='../numbers', version='0.18.0' }");
        Project(t, "numbers", "numbers");
        t.Write("numbers/main.aqua", "變數 value = 41;");
        t.Write("greeting/main.aqua", "變數 numbers = 匯入(\"/bottles/numbers/main.aqua\");\n變數 問候, greet = 函式(名) { 回傳 \"你好 \" + 名 + \" \" + String.From(numbers.value + 1); };");
        t.Write("app/main.aqua", "變數 g = 匯入(\"/bottles/greeting/main.aqua\");\n印出(g.greet(\"Aquarius\"));\n印出(g.問候(\"世界\"));");
        string output = t.FilePath("app.wasm"); ProjectBuilder.Compile(app, output, cache: cache);
        Assert.Equal("你好 Aquarius 42\n你好 世界 42\n", WasmTestHost.RunWasm(output).Succeeded().Output);
    }
    [Fact] public void LocksHydrateAnEmptyCacheAndRemainUnchanged() {
        using var t = new BottleDirectory(); string app = Project(t, "app", "app", "executable", dependencies: "lib={path='../lib',version='0.18.0'}"); Project(t, "lib", "lib");
        new BottleManager(new BottleCache(t.FilePath("cache-one"))).Restore(ProjectManifest.Load(app));
        string before = File.ReadAllText(t.FilePath("app/aqua.lock.json"));
        var restored = new BottleManager(new BottleCache(t.FilePath("cache-two"))).Restore(ProjectManifest.Load(app), locked: true, offline: true);
        Assert.Single(restored.Packages); Assert.Equal(before, File.ReadAllText(t.FilePath("app/aqua.lock.json")));
    }
    [Fact] public void UpdatesKeepOldVersionsAndRejectMutatingPublishedContents() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
        string app = Project(t, "app", "app", "executable", dependencies: "lib={path='../lib',version='>= 0.18, < 0.20'}"); string library = Project(t, "lib", "lib");
        manager.Restore(ProjectManifest.Load(app)); string firstLock = File.ReadAllText(t.FilePath("app/aqua.lock.json"));
        File.WriteAllText(library, File.ReadAllText(library).Replace("0.18.0", "0.19.0"));
        Assert.Equal("0.18.0", manager.Restore(ProjectManifest.Load(app)).Packages.Single().Version);
        Assert.Equal("0.19.0", manager.Restore(ProjectManifest.Load(app), update: true).Packages.Single().Version);
        Assert.Equal(2, cache.List().Count());
        t.Write("lib/main.aqua", "變數 value=99;");
        Assert.Contains("immutable", Assert.Throws<InvalidDataException>(() => manager.Restore(ProjectManifest.Load(app), update: true)).Message);
        File.WriteAllText(t.FilePath("app/aqua.lock.json"), firstLock);
        Assert.Equal("0.18.0", manager.Restore(ProjectManifest.Load(app), locked: true).Packages.Single().Version);
        t.Write("cache/packages/lib/0.18.0/content/main.aqua", "tampered");
        Assert.Contains("integrity", Assert.Throws<InvalidDataException>(() => manager.Restore(ProjectManifest.Load(app))).Message);
        cache.Remove("lib", "0.18.0"); Assert.False(Directory.Exists(t.FilePath("cache/packages/lib/0.18.0")));
    }
    [Fact] public void CacheOnlyDependenciesResolveTheirOwnTransitivePins() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
        string library = Project(t, "lib", "lib", dependencies: "child={path='../child',version='0.18.0'}"); Project(t, "child", "child");
        manager.Install(library, ".", global: true);
        string app = Project(t, "app", "app", "executable", dependencies: "lib='>= 0.18, < 0.19'");
        Directory.Delete(t.FilePath("lib"), true); Directory.Delete(t.FilePath("child"), true);
        Assert.Equal(2, manager.Restore(ProjectManifest.Load(app), offline: true).Packages.Count);
    }
    [Fact] public void ProjectsCanPinDifferentCompatibleChildrenOfTheSameCachedLibraryVersion() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
        string parent = Project(t, "parent", "parent", dependencies: "child={path='../child',version='>= 0.18, < 0.20'}"); string child = Project(t, "child", "child");
        string first = Project(t, "one", "one", "executable", dependencies: "parent={path='../parent',version='0.18.0'}");
        manager.Restore(ProjectManifest.Load(first));
        File.WriteAllText(child, File.ReadAllText(child).Replace("0.18.0", "0.19.0"));
        string second = Project(t, "two", "two", "executable", dependencies: "parent={path='../parent',version='0.18.0'}");
        Assert.Contains(manager.Restore(ProjectManifest.Load(second)).Packages, p => p.Name == "child" && p.Version == "0.19.0");
        Assert.Contains(manager.Restore(ProjectManifest.Load(first), locked: true).Packages, p => p.Name == "child" && p.Version == "0.18.0");
        Assert.Contains(manager.Restore(ProjectManifest.Load(second), locked: true).Packages, p => p.Name == "child" && p.Version == "0.19.0");
        Assert.Single(cache.List("parent"));
    }
    [Fact] public void CyclesAndVersionConflictsAreReportedBeforeProducingLocks() {
        using var t = new BottleDirectory(); var manager = new BottleManager(new BottleCache(t.FilePath("cache")));
        string app = Project(t, "app", "app", "executable", dependencies: "a={path='../a',version='0.18.0'}");
        Project(t, "a", "a", dependencies: "b={path='../b',version='0.18.0'}"); Project(t, "b", "b", dependencies: "a={path='../a',version='0.18.0'}");
        Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => manager.Restore(ProjectManifest.Load(app))).Message);
        Assert.False(File.Exists(t.FilePath("app/aqua.lock.json")));
        Project(t, "b", "b", dependencies: "common={path='../common2',version='0.19.0'}"); Project(t, "common1", "common"); Project(t, "common2", "common", version: "0.19.0");
        Project(t, "a", "a", dependencies: "common={path='../common1',version='0.18.0'}");
        Project(t, "app", "app", "executable", dependencies: "a={path='../a',version='0.18.0'}\nb={path='../b',version='0.18.0'}");
        Assert.Contains("Conflicting", Assert.Throws<InvalidDataException>(() => manager.Restore(ProjectManifest.Load(app))).Message);
    }
    [Fact] public void UnsupportedPlatformsRemainBlockedWhenDeployingAnIntermediateWasm() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", dependencies: "lib={path='../lib',version='0.18.0'}", platforms: "['windows']"); Project(t, "lib", "lib", platforms: "['windows']");
        Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), "web", cache: cache));
        string output = t.FilePath("app.wasm"); ProjectBuilder.Compile(app, output, cache: cache);
        // The web target checks the application's platforms before it needs a runtime pack or touches the output.
        Assert.Throws<InvalidDataException>(() => new WebTarget().Build(new WasmBuildRequest(output, t.FilePath("web"))));
        Assert.False(Directory.Exists(t.FilePath("web")));
    }
    [Fact] public void AssetMappingsRejectCollisionsAndFilterPlatformSpecificNativeFiles() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='plugin.dll'\ntarget='native/plugin.dll'\nplatforms=['windows']"); t.Write("app/plugin.dll", "binary fixture");
        Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("generic.wasm"), cache: cache));
        ProjectBuilder.Compile(app, t.FilePath("web.wasm"), "web", cache: cache); Assert.Empty(WasmApplication.Load(t.FilePath("web.wasm")).Assets);
        ProjectBuilder.Compile(app, t.FilePath("windows.wasm"), "windows", cache: cache); Assert.Contains("native/plugin.dll", WasmApplication.Load(t.FilePath("windows.wasm")).Assets.Keys);
        Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='one.txt'\ntarget='same.txt'\n[[assets]]\nsource='two.txt'\ntarget='same.txt'"); t.Write("app/one.txt", "1"); t.Write("app/two.txt", "2");
        Assert.Contains("Conflicting", Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), cache: cache)).Message);
        Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='one.txt'\ntarget='hidden.aqua'");
        Assert.Contains(".aqua", Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), cache: cache)).Message);
    }
    [Fact] public void LibraryPlatformAlternativesShareOneDestinationAndKeepCompleteOfflineSnapshots() {
        using var t = new BottleDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app=Project(t,"app","app","executable",dependencies:"lib={path='../lib',version='0.18.0'}");
        Project(t,"lib","lib",assets:"[[assets]]\nsource='windows.txt'\ntarget='backend.txt'\nplatforms=['windows']\n[[assets]]\nsource='web.txt'\ntarget='backend.txt'\nplatforms=['web']");
        t.Write("lib/windows.txt","native");t.Write("lib/web.txt","wasm");
        new BottleManager(cache).Restore(ProjectManifest.Load(app),"windows");Directory.Delete(t.FilePath("lib"),true);
        foreach(string target in new[]{"windows","web"}) {
            string output=t.FilePath(target+".wasm");ProjectBuilder.Compile(app,output,target,locked:true,offline:true,cache:cache);
            var package=WasmApplication.Load(output);Assert.Single(package.Assets);Assert.Equal(target=="windows"?"native":"wasm",System.Text.Encoding.UTF8.GetString(package.Assets["backend.txt"]));
            Assert.Throws<InvalidDataException>(()=>package.CheckPlatform(target=="windows"?"web":"windows"));
        }
    }
    [Fact] public void CliSelectsAssetPlatformForIntermediateWasmWithoutExportingAnExecutable() {
        using var t=new BottleDirectory();
        string app=Project(t,"app","app","executable",assets:"[[assets]]\nsource='windows.txt'\nplatforms=['windows']");t.Write("app/windows.txt","native asset");
        AquaCli.Run(t,"build",app,"--platform","windows","-o","app.wasm").Succeeded();
        var package=WasmApplication.Load(t.FilePath("app.wasm"));Assert.Contains("windows.txt",package.Assets.Keys);Assert.Throws<InvalidDataException>(()=>package.CheckPlatform("web"));
        Assert.Equal(2,AquaCli.Run(t,"build",app,"--platform","unknown","-o","bad.wasm").Code);
    }
    [RuntimeFact] public void CliCreatesInstallsBuildsRunsAndUninstallsVersion1LibrariesWithAnIsolatedGlobalCache() {
        using var t = new BottleDirectory();
        // aqua new writes a manifest_version 2 application; it still builds version 1 libraries from /bottles.
        AquaCli.Run(t, "new", "demo", "-o", "app").Succeeded();
        Project(t, "app/vendor/mathlib", "mathlib", version: "0.1.0");
        t.Write("app/vendor/mathlib/main.aqua", "變數 加法, add = 函式(a, b) { 回傳 a + b; };");
        t.Write("app/main.aqua", "變數 m=匯入(\"/bottles/mathlib/main.aqua\");印出(m.add(20,22));印出(m.加法(1,2));");
        AquaCli.Run(t, "bottle", "install", "app/vendor/mathlib/aqua.toml", "--project", "app", "--version", ">= 0.1, < 0.2").Succeeded();
        AquaCli.Run(t, "build", "app", "-o", "app.wasm", "--locked", "--offline").Succeeded();
        Assert.Equal("42\n3\n", AquaCli.Run(t, "run", "app.wasm").Succeeded().Output);
        Assert.Equal("42\n3\n", AquaCli.Run(t, "run", "app").Succeeded().Output);
        Assert.Equal("mathlib >= 0.1, < 0.2\n", AquaCli.Run(t, "bottle", "list", "--project", "app").Succeeded().Output);
        AquaCli.Run(t, "bottle", "uninstall", "mathlib", "--project", "app").Succeeded(); Assert.Empty(ProjectManifest.Load(t.FilePath("app")).Dependencies);
        Assert.NotEmpty(new BottleCache(t.FilePath("cache")).List());
        t.Write("app/aqua.toml", File.ReadAllText(t.FilePath("app/aqua.toml")).Replace("name = \"demo\"", "name = \"bad name\""));
        Assert.Equal(1, AquaCli.Run(t, "build", "app/main.aqua", "-o", "invalid.wasm").Code); Assert.False(File.Exists(t.FilePath("invalid.wasm")));
    }
    [Fact] public void FailedInstallRollsBackProjectAndLockAndCustomManifestNamesWork() {
        using var t = new BottleDirectory(); var manager = new BottleManager(new BottleCache(t.FilePath("cache")));
        string app = Project(t, "app", "app", "executable", platforms: "['web']"); string lib = Project(t, "lib", "lib", platforms: "['windows']");
        string custom = t.FilePath("lib/library.toml"); File.Move(lib, custom);
        string before = File.ReadAllText(app);
        Assert.Throws<InvalidDataException>(() => manager.Install(custom, app)); Assert.Equal(before, File.ReadAllText(app)); Assert.False(File.Exists(t.FilePath("app/aqua.lock.json")));
        Assert.Equal("lib", ProjectManifest.Load(manager.Cache.ContentRoot(manager.Cache.List().Single())).Name);
    }
}
