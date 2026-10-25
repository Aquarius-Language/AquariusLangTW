using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusTests.Packaging;

namespace AquariusTests.Targets;

// Executables copy the host runtime pack (tens of megabytes); keep that disk work away from the other process tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExecutableCollection { public const string Name = "Desktop executables"; }

/// <summary>The windows and linux targets (<see cref="ExecutableTarget"/>): the runtime pack's launcher with the
/// application appended as an overlay, the pack's other files and external libraries' binaries beside it.</summary>
[Collection(ExecutableCollection.Name)]
public class ExecutableTargetTest
{
    private static string Platform => OperatingSystem.IsWindows() ? "windows" : "linux";
    private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";
    private static ExecutableTarget HostTarget() => new(Platform, AquariusInstallation.HostTarget);
    internal static Subsystem ReadSubsystem(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.PEHeader!.Subsystem;
    }
    private static string Compile(TestFolder t, string source, LibrarySet? libraries = null)
    {
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("main.aqua", source)], wasm, t.Path, libraries: libraries);
        return wasm;
    }
    private static RunResult Launch(string executable, params string[] arguments) =>
        Processes.Run(executable, Path.GetDirectoryName(executable)!, arguments, environment: new Dictionary<string, string> {
            // The executable needs nothing from the compiler: no .NET, no PATH.
            ["DOTNET_ROOT"] = Path.Combine(Path.GetDirectoryName(executable)!, "no-dotnet"), ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["PATH"] = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : "/usr/bin:/bin",
        });

    [ExecutableFact]
    public void RelocatedExecutableRunsTheSelectedEntryWithModulesAndAssetsWithoutSourcesOrWasm()
    {
        using var t = new TestFolder();
        var sources = new[] {
            t.Write("source/main.aqua", "不存在;"),
            t.Write("source/lib/工具.aqua", "變數 值=42;"),
            t.Write("source/other.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 變數 f=匯入(\"Files\"); [m.值, f.讀取資產文字(\"assets/文字.txt\", \"utf-8\")];"),
        };
        t.Write("source/assets/文字.txt", "封裝成功");
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile(sources, wasm, t.FilePath("source"), [t.FilePath("source/assets")]);
        HostTarget().Build(new WasmBuildRequest(wasm, t.FilePath("dist/app" + Extension), "OTHER.AQUA"));
        Directory.Delete(t.FilePath("source"), true); File.Delete(wasm);
        Directory.Move(t.FilePath("dist"), t.FilePath("搬移 folder"));
        string executable = t.FilePath("搬移 folder/星泉 app" + Extension);
        File.Move(t.FilePath("搬移 folder/app" + Extension), executable);
        var files = Directory.GetFileSystemEntries(t.FilePath("搬移 folder")).Order().ToArray();
        var run = Launch(executable);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Equal("[42, 封裝成功]\n", run.Output); Assert.Equal("", run.Error);
        Assert.Equal(files, Directory.GetFileSystemEntries(t.FilePath("搬移 folder")).Order().ToArray()); // Nothing is extracted.
        using var stream = File.OpenRead(executable);
        Assert.Equal("other.aqua", ExecutableBundle.Load(stream).EntryPoint);
    }

    [ExecutableTheory]
    [InlineData(null, Subsystem.WindowsCui)]
    [InlineData("false", Subsystem.WindowsCui)]
    [InlineData("true", Subsystem.WindowsGui)]
    [InlineData("on", Subsystem.WindowsGui)]
    public void OnlyHideTerminalChangesTheSubsystemAndKeepsTheTemplateAndPayload(string? value, Subsystem expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TestFolder();
        string wasm = Compile(t, "印出(\"無終端視窗 42\");");
        string template = Path.Combine(AquariusInstallation.Require().RuntimePack("windows-x64"), "aquarius.exe");
        byte[] before = SHA256.HashData(File.ReadAllBytes(template));
        var options = value == null ? null : new Dictionary<string, string> { [ExecutableTarget.HideTerminalOption] = value };
        string output = t.FilePath("app.exe"), console = t.FilePath("console/app.exe");
        new ExecutableTarget("windows", "windows-x64").Build(new WasmBuildRequest(wasm, output, Options: options));
        new ExecutableTarget("windows", "windows-x64").Build(new WasmBuildRequest(wasm, console));
        Assert.Equal(expected, ReadSubsystem(output));
        Assert.Equal(Subsystem.WindowsCui, ReadSubsystem(template));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(template)));
        using (var executable = File.OpenRead(output)) Assert.Equal("main.aqua", ExecutableBundle.Load(executable).EntryPoint);
        // Only the two subsystem bytes differ from the console build.
        byte[] a = File.ReadAllBytes(output), b = File.ReadAllBytes(console);
        Assert.Equal(a.Length, b.Length);
        Assert.InRange(Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]), 0, 2);
        // A GUI executable still writes to redirected standard streams.
        var run = Launch(output);
        Assert.True(run.ExitCode == 0, run.Output + run.Error); Assert.Equal("無終端視窗 42\n", run.Output);
    }

    [Theory]
    [InlineData("windows", "invalid")]
    [InlineData("linux", "true")]
    public void InvalidHideTerminalOptionsPreserveTheOutput(string platform, string value)
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "42;");
        string output = t.Write("dist/app" + (platform == "windows" ? ".exe" : ""), "previous output");
        var target = new ExecutableTarget(platform, platform + "-x64");
        Assert.Throws<ArgumentException>(() => target.Build(new WasmBuildRequest(wasm, output, Options: new Dictionary<string, string> { [ExecutableTarget.HideTerminalOption] = value })));
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Single(Directory.GetFileSystemEntries(t.FilePath("dist")));
    }

    [Theory]
    [InlineData("entry")][InlineData("wasm")][InlineData("extension")][InlineData("input")][InlineData("missing")]
    public void FailedBuildsPreservePreviousOutputAndLeaveNoStagingFiles(string failure)
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "42;");
        string output = t.Write(failure == "extension" ? "dist/app.bin" : "dist/app.exe", "previous output");
        if (failure == "wasm") File.WriteAllText(wasm, "invalid archive");
        var target = failure == "input" ? new ExecutableTarget("linux", "linux-x64") : new ExecutableTarget("windows", "windows-x64");
        string input = failure == "missing" ? t.FilePath("missing.wasm") : wasm;
        if (failure == "input") output = wasm;
        byte[] before = File.ReadAllBytes(output);
        var error = Record.Exception(() => target.Build(new WasmBuildRequest(input, output, failure == "entry" ? "missing.aqua" : null)));
        switch (failure) {
            case "entry" or "wasm": Assert.IsType<InvalidDataException>(error); break;
            case "extension" or "input": Assert.IsType<ArgumentException>(error); break;
            default: Assert.IsAssignableFrom<IOException>(error); break;
        }
        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(t.FilePath("dist"), "*.tmp"));
    }

    [ExecutableFact]
    public void BuildsReplaceOnlyTheRequestedFile()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "印出(\"DO-NOT-RUN\");");
        string output = t.Write("dist/星泉 app" + Extension, "previous output"); t.Write("dist/keep.txt", "unrelated");
        HostTarget().Build(new WasmBuildRequest(wasm, output));
        using (var stream = File.OpenRead(output)) Assert.Equal("main.aqua", ExecutableBundle.Load(stream).EntryPoint);
        Assert.Equal("unrelated", File.ReadAllText(t.FilePath("dist/keep.txt")));
        Assert.Empty(Directory.GetFiles(t.FilePath("dist"), "*.tmp"));
    }

    [ExecutableFact]
    public void RuntimeErrorsAndDamagedPayloadsExitWithOne()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "不存在;"), executable = t.FilePath("app" + Extension);
        HostTarget().Build(new WasmBuildRequest(wasm, executable));
        var run = Launch(executable);
        Assert.Equal(1, run.ExitCode); Assert.Equal("ERROR: Identifier not found: 不存在\n", run.Output);
        // Damage a copy: Windows scanners may briefly hold the executable that just ran.
        string damaged = t.FilePath("damaged" + Extension);
        File.Copy(executable, damaged);
        using (var file = new FileStream(damaged, FileMode.Open, FileAccess.ReadWrite)) { file.Position = file.Length - 100; file.WriteByte(0x5a); }
        run = Launch(damaged);
        Assert.Equal(1, run.ExitCode);
        Assert.Matches("(?i)damaged|corrupt|checksum|invalid|unsupported", run.Error);
        Assert.Equal("", run.Output);
    }

    [ExecutableFact]
    public void ExternalLibraryBinariesAreCopiedIntoTheLibrariesFolder()
    {
        using var t = new TestFolder();
        string host = AquariusInstallation.HostTarget;
        var set = LibrarySet.Default.With([Fixtures.ExternalLibrary(t, host)]);
        string wasm = Compile(t, "匯入(\"星雲\"); 匯入(\"Audio\");", set);
        Assert.Equal(["audio", "nebula"], WasmApplication.Load(wasm).NativeLibraries);
        string output = t.FilePath("dist/app" + Extension);
        HostTarget().Build(new WasmBuildRequest(wasm, output, Libraries: set));
        string file = Library.NativeFile("nebula", host);
        Assert.Equal($"dummy {host} binary of nebula", File.ReadAllText(t.FilePath("dist/libraries/" + file)));
        // Default libraries are part of the runtime pack.
        Assert.Equal([file], Directory.GetFiles(t.FilePath("dist/libraries")).Select(Path.GetFileName));
    }

    [ExecutableFact]
    public void MissingExternalBinariesFailBeforeReplacingTheOutput()
    {
        using var t = new TestFolder();
        var set = LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "web")]);
        string wasm = Compile(t, "匯入(\"NebulaNative\");", set);
        string output = t.Write("dist/app" + Extension, "previous output");
        var error = Assert.Throws<IOException>(() => HostTarget().Build(new WasmBuildRequest(wasm, output, Libraries: set)));
        Assert.Contains("aqua library build", error.Message); Assert.Contains("nebula", error.Message);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.False(Directory.Exists(t.FilePath("dist/libraries")));
    }

    [Fact]
    public void LinuxExecutablesKeepTheElfLauncherAndAreExecutable()
    {
        using var t = new TestFolder();
        string wasm = ExecutableBundleTest.Wasm(t);
        var installation = AquariusInstallation.Require();
        string output = t.Write("dist/星泉 app", "previous output");
        var target = new ExecutableTarget("linux", "linux-x64");
        if (!File.Exists(Path.Combine(installation.RuntimePack("linux-x64"), "aquarius"))) {
            var missing = Assert.Throws<IOException>(() => target.Build(new WasmBuildRequest(wasm, output)));
            Assert.Contains("linux-x64", missing.Message);
            Assert.Equal("previous output", File.ReadAllText(output));
            return;
        }
        target.Build(new WasmBuildRequest(wasm, output, "LIB/工具.AQUA"));
        using var stream = File.OpenRead(output);
        byte[] header = new byte[4]; stream.ReadExactly(header);
        Assert.Equal("\u007fELF"u8.ToArray(), header);
        Assert.Equal("lib/工具.aqua", ExecutableBundle.Load(stream).EntryPoint);
        if (!OperatingSystem.IsWindows()) {
            var mode = File.GetUnixFileMode(output);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute) && mode.HasFlag(UnixFileMode.GroupExecute) && mode.HasFlag(UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public void TargetRegistryReportsSupportedTargetsAndOptions()
    {
        var targets = BuildTargets.Default;
        Assert.Equal(["android", "linux", "web", "windows"], targets.Names.Order(StringComparer.Ordinal));
        Assert.Equal("windows-x64", Assert.IsType<ExecutableTarget>(targets.Resolve("windows")).RuntimeTarget);
        Assert.Equal("linux-x64", Assert.IsType<ExecutableTarget>(targets.Resolve("linux")).RuntimeTarget);
        Assert.IsType<WebTarget>(targets.Resolve("web")); Assert.IsType<AndroidTarget>(targets.Resolve("android"));
        var unknown = Assert.Throws<ArgumentException>(() => targets.Resolve("native"));
        Assert.Contains("windows", unknown.Message); Assert.Contains("wasm", unknown.Message);
        Assert.Contains(ExecutableTarget.HideTerminalOption, targets.Resolve("windows").OptionNames);
        Assert.DoesNotContain(ExecutableTarget.HideTerminalOption, targets.Resolve("linux").OptionNames);
        Assert.Empty(targets.Resolve("web").OptionNames);
        Assert.Equal([AndroidTarget.ApplicationIdOption, AndroidTarget.NameOption, AndroidTarget.KeystoreOption, AndroidTarget.KeyAliasOption],
            targets.Resolve("android").OptionNames);
        Assert.Throws<ArgumentException>(() => new BuildTargets([new WebTarget(), new WebTarget()]));
    }
}
