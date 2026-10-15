using System.Diagnostics;
using AquariusLang.Bottle;
using AquariusLang.Cli;
using AquariusLang.Desktop.runtime;
using AquariusLang.Packaging;
using AquariusLang.Web;

namespace AquariusTests.Bottle;

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
    private static string Project(ApplicationTestDirectory t, string directory, string name, string type = "library", string version = "0.18.0", string dependencies = "", string assets = "", string? platforms = null) {
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
    [Fact] public void ExecutablesCanOmitVersionAndTomlSupportsMultilineArraysAndComments() {
        var project = ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), "# project\n[project]\nname = '我的應用'\ntype = 'executable'\nsources = [\n 'main.aqua', # file\n]\n");
        Assert.Null(project.Version); Assert.Equal("我的應用", project.Name);
    }
    [Fact] public void NestedLibrariesAndAssetsAreCachedAndBuildAfterSourcesDisappear() {
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", dependencies: "greeting = { path='vendor/greeting', version='> 0.17, <= 0.21' }");
        Project(t, "app/vendor/greeting", "greeting", dependencies: "numbers = { path='deps/numbers/aqua.toml', version='0.18.0' }", assets: "[[assets]]\nsource='assets'\n[[assets]]\nsource='native/plugin.dll'\ntarget='native/plugin.dll'");
        Project(t, "app/vendor/greeting/deps/numbers", "numbers");
        t.Write("app/vendor/greeting/assets/message.txt", "cached greeting"); t.Write("app/vendor/greeting/native/plugin.dll", "native fixture");
        t.Write("app/main.aqua", "變數 g=匯入(\"/bottles/greeting/main.aqua\");g.value;");
        var manager = new BottleManager(cache); var resolved = manager.Restore(ProjectManifest.Load(app));
        Assert.Equal(2, resolved.Packages.Count); Assert.False(File.Exists(t.FilePath("app/vendor/greeting/aqua.lock.json")));
        var inspector = ProjectInspector.Libraries(ProjectManifest.Load(app), cache); Assert.Equal(2, inspector.Count);
        Directory.Delete(t.FilePath("app/vendor"), true);
        string output = t.FilePath("build/app.wasm"); ProjectBuilder.Compile(app, output, locked: true, offline: true, cache: cache);
        var package = WasmApplication.Load(output);
        Assert.Contains("bottles/greeting/main.aqua", package.Scripts.Keys); Assert.Contains("bottles/numbers/main.aqua", package.Scripts.Keys);
        Assert.Contains("bottles/greeting/assets/message.txt", package.Assets.Keys); Assert.Contains("native/plugin.dll", package.Assets.Keys);
        Assert.DoesNotContain("bottles/greeting/deps/numbers/main.aqua", package.Scripts.Keys);
    }
    [Fact] public void LocksHydrateAnEmptyCacheAndRemainUnchanged() {
        using var t = new ApplicationTestDirectory(); string app = Project(t, "app", "app", "executable", dependencies: "lib={path='../lib',version='0.18.0'}"); Project(t, "lib", "lib");
        new BottleManager(new BottleCache(t.FilePath("cache-one"))).Restore(ProjectManifest.Load(app));
        string before = File.ReadAllText(t.FilePath("app/aqua.lock.json"));
        var restored = new BottleManager(new BottleCache(t.FilePath("cache-two"))).Restore(ProjectManifest.Load(app), locked: true, offline: true);
        Assert.Single(restored.Packages); Assert.Equal(before, File.ReadAllText(t.FilePath("app/aqua.lock.json")));
    }
    [Fact] public void UpdatesKeepOldVersionsAndRejectMutatingPublishedContents() {
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
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
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
        string library = Project(t, "lib", "lib", dependencies: "child={path='../child',version='0.18.0'}"); Project(t, "child", "child");
        manager.Install(library, ".", global: true);
        string app = Project(t, "app", "app", "executable", dependencies: "lib='>= 0.18, < 0.19'");
        Directory.Delete(t.FilePath("lib"), true); Directory.Delete(t.FilePath("child"), true);
        Assert.Equal(2, manager.Restore(ProjectManifest.Load(app), offline: true).Packages.Count);
    }
    [Fact] public void ProjectsCanPinDifferentCompatibleChildrenOfTheSameCachedLibraryVersion() {
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache")); var manager = new BottleManager(cache);
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
        using var t = new ApplicationTestDirectory(); var manager = new BottleManager(new BottleCache(t.FilePath("cache")));
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
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", dependencies: "lib={path='../lib',version='0.18.0'}", platforms: "['windows']"); Project(t, "lib", "lib", platforms: "['windows']");
        Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), "web", cache: cache));
        string output = t.FilePath("app.wasm"); ProjectBuilder.Compile(app, output, cache: cache);
        Assert.Throws<InvalidDataException>(() => WebsiteCompiler.BuildWasm(output, t.FilePath("web")));
        Assert.False(Directory.Exists(t.FilePath("web")));
    }
    [Fact] public void AssetMappingsRejectCollisionsAndFilterPlatformSpecificNativeFiles() {
        using var t = new ApplicationTestDirectory(); var cache = new BottleCache(t.FilePath("cache"));
        string app = Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='plugin.dll'\ntarget='native/plugin.dll'\nplatforms=['windows']"); t.Write("app/plugin.dll", "binary fixture");
        Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("generic.wasm"), cache: cache));
        ProjectBuilder.Compile(app, t.FilePath("web.wasm"), "web", cache: cache); Assert.Empty(WasmApplication.Load(t.FilePath("web.wasm")).Assets);
        ProjectBuilder.Compile(app, t.FilePath("windows.wasm"), "windows", cache: cache); Assert.Contains("native/plugin.dll", WasmApplication.Load(t.FilePath("windows.wasm")).Assets.Keys);
        Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='one.txt'\ntarget='same.txt'\n[[assets]]\nsource='two.txt'\ntarget='same.txt'"); t.Write("app/one.txt", "1"); t.Write("app/two.txt", "2");
        Assert.Contains("Conflicting", Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), cache: cache)).Message);
        Project(t, "app", "app", "executable", assets: "[[assets]]\nsource='one.txt'\ntarget='hidden.aqua'");
        Assert.Contains(".aqua", Assert.Throws<InvalidDataException>(() => ProjectBuilder.Compile(app, t.FilePath("bad.wasm"), cache: cache)).Message);
    }
    [Fact] public void CliCreatesInstallsBuildsRunsAndUninstallsWithAnIsolatedGlobalCache() {
        using var t = new ApplicationTestDirectory();
        AssertSuccess(Run(t, "new", "demo", "-o", "app")); AssertSuccess(Run(t, "new", "mathlib", "--type", "library", "-o", "app/vendor/mathlib"));
        t.Write("app/main.aqua", "變數 m=匯入(\"/bottles/mathlib/main.aqua\");印出(m.add(20,22));");
        AssertSuccess(Run(t, "bottle", "install", "app/vendor/mathlib/aqua.toml", "--project", "app", "--version", ">= 0.1, < 0.2"));
        AssertSuccess(Run(t, "build", "app", "-o", "app.wasm", "--locked", "--offline"));
        var run = Run(t, "run", "app.wasm"); AssertSuccess(run); Assert.Equal("42\n", run.Output.Replace("\r\n", "\n"));
        AssertSuccess(Run(t, "bottle", "uninstall", "mathlib", "--project", "app")); Assert.Empty(ProjectManifest.Load(t.FilePath("app")).Dependencies);
        Assert.NotEmpty(new BottleCache(t.FilePath("cache")).List());
        t.Write("app/aqua.toml", File.ReadAllText(t.FilePath("app/aqua.toml")).Replace("name = \"demo\"", "name = \"bad name\""));
        Assert.Equal(1, Run(t, "build", "app/main.aqua", "-o", "invalid.wasm").Code); Assert.False(File.Exists(t.FilePath("invalid.wasm")));
    }
    [Fact] public void FailedInstallRollsBackProjectAndLockAndCustomManifestNamesWork() {
        using var t = new ApplicationTestDirectory(); var manager = new BottleManager(new BottleCache(t.FilePath("cache")));
        string app = Project(t, "app", "app", "executable", platforms: "['web']"); string lib = Project(t, "lib", "lib", platforms: "['windows']");
        string custom = t.FilePath("lib/library.toml"); File.Move(lib, custom);
        string before = File.ReadAllText(app);
        Assert.Throws<InvalidDataException>(() => manager.Install(custom, app)); Assert.Equal(before, File.ReadAllText(app)); Assert.False(File.Exists(t.FilePath("app/aqua.lock.json")));
        Assert.Equal("lib", ProjectManifest.Load(manager.Cache.ContentRoot(manager.Cache.List().Single())).Name);
    }
    private static void AssertSuccess((int Code, string Output, string Error) result) => Assert.True(result.Code == 0, result.Error);
    private static (int Code, string Output, string Error) Run(ApplicationTestDirectory directory, params string[] args) {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { WorkingDirectory = directory.Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["AQUA_BOTTLE_HOME"] = directory.FilePath("cache"); start.ArgumentList.Add(typeof(CompilerCommandLine).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Bottle CLI timed out."); }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
