using System.Diagnostics;
using System.Text.Json;
using AquariusLang.Bottle;
using AquariusLang.Cli;
using AquariusLang.Wasm;
using AquariusTests.Packaging;

namespace AquariusTests.CommandLine;

/// <summary>The <c>aqua</c> command surface (build, run, new, repl and their errors), run in this process through
/// <see cref="CompilerCommandLine.Run"/>; <c>aqua run</c> starts the host runtime pack.</summary>
[Collection(ConsoleCollection.Name)]
public class CliTest
{
    private static CliResult Aqua(TestFolder t, params string[] args) => AquaCommand.Run(t.Path, args);

    [RuntimeTheory]
    [InlineData("變數 工具=匯入(\"tools.aqua\");印出(工具.add(20,22));工具.加法(1,2);", "var add, 加法 = function(a,b) { return a+b; };")]
    [InlineData("var tools=import(\"tools.aqua\");print(tools.加法(20,22));tools.add(1,2);", "變數 加法, add = 函式(甲,乙) { 回傳 甲+乙; };")]
    [InlineData("var 工具=匯入(\"tools.aqua\");印出(工具.add(20,22));工具.加法(1,2);", "變數 add, 加法 = function(a,乙) { 回傳 a+乙; };")]
    public void DifferentSyntaxesInteroperateAcrossModulesAndSourceFreeExecution(string main, string library)
    {
        using var t = new TestFolder();
        string entry = t.Write("main.aqua", main), module = t.Write("tools.aqua", library);
        var built = Aqua(t, "build", entry, module, "--root", t.Path, "-o", "app.wasm");
        Assert.True(built.Code == 0, built.Error);
        File.Delete(entry); File.Delete(module);
        var run = Aqua(t, "run", "app.wasm");
        Assert.Equal(0, run.Code); Assert.Equal("", run.Error);
        Assert.Equal("42\n3\n", run.Output);
    }

    [RuntimeFact]
    public void BuildWritesTheWasmBesideTheWorkingDirectoryAndRunsItWithoutSources()
    {
        using var t = new TestFolder();
        t.Write("main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 印出(\"星泉運行\"); m.值;"); t.Write("lib/工具.aqua", "變數 值=42;");
        var built = Aqua(t, "build", "main.aqua", "lib/工具.aqua");
        Assert.Equal(0, built.Code); Assert.Equal("", built.Error);
        Assert.Equal($"Compiled 2 script(s) to {t.FilePath("main.wasm")}\n", built.Output);
        Assert.DoesNotContain("星泉運行", built.Output); // Building never runs the program.
        Assert.Equal(["main.aqua", "lib/工具.aqua"], WasmApplication.Load(t.FilePath("main.wasm")).Scripts.Keys);
        Directory.Delete(t.FilePath("lib"), true); File.Delete(t.FilePath("main.aqua"));
        var run = Aqua(t, "run", "main.wasm");
        Assert.Equal(0, run.Code); Assert.Equal("星泉運行\n42\n", run.Output);
    }

    [RuntimeFact]
    public void RunCompilesAndRunsASourceWithoutLeavingFiles()
    {
        using var t = new TestFolder();
        t.Write("程式 folder/main.aqua", "印出(\"星泉\"); 變數 f = 函式(x) { x * 2; }; f(21);");
        var run = Aqua(t, "run", "程式 folder/main.aqua");
        Assert.Equal(0, run.Code); Assert.Equal("", run.Error);
        Assert.Equal("星泉\n42\n", run.Output);
        Assert.Equal(["main.aqua"], Directory.GetFiles(t.FilePath("程式 folder")).Select(Path.GetFileName));
        var failed = Aqua(t, "run", "程式 folder/main.aqua", "extra");
        Assert.Equal(0, failed.Code);
        File.WriteAllText(t.FilePath("程式 folder/main.aqua"), "不存在;");
        var error = Aqua(t, "run", "程式 folder/main.aqua");
        Assert.Equal(1, error.Code); Assert.Equal("ERROR: Identifier not found: 不存在\n", error.Output);
        Assert.Equal(["main.aqua"], Directory.GetFiles(t.FilePath("程式 folder")).Select(Path.GetFileName));
    }

    [RuntimeFact]
    public void RunningASourceImportsTheModulesBesideIt()
    {
        // aqua run main.aqua runs a program whose modules are files next to it, as the documented examples do.
        using var t = new TestFolder();
        t.Write("main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 變數 n=匯入(目前工作目錄 + \"/other.aqua\"); m.值 + n.值;");
        t.Write("lib/工具.aqua", "變數 值=40;"); t.Write("other.aqua", "變數 值=2;");
        var run = Aqua(t, "run", "main.aqua");
        Assert.True(run.Code == 0, run.Output + run.Error);
        Assert.Equal("42\n", run.Output);
    }

    [RuntimeFact]
    public void StarshipExpeditionRunsThroughAquaRunAsSource()
    {
        // The README runs the example from its source; its module is imported by 目前工作目錄 + "/航行工具.aqua".
        using var t = new TestFolder();
        string starship = Path.Combine(AquariusLang.Compiler.AquariusInstallation.Require().Root, "examples", "starship_expedition");
        foreach (string file in Directory.GetFiles(starship)) File.Copy(file, t.FilePath(Path.GetFileName(file)));
        var result = Aqua(t, "run", "main.aqua");
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(File.ReadAllText(Path.Combine(starship, "expected-output.txt")).Replace("\r\n", "\n"), result.Output);
    }

    [RuntimeFact]
    public void RunPassesArgumentsToTheApplicationAsLaunchFiles()
    {
        using var t = new TestFolder();
        t.Write("啟動 file.txt", "a"); t.Write("b.txt", "b");
        t.Write("main.aqua", "變數 l = 匯入(\"Application\").啟動檔案(); 印出(長度(l)); 迴圈 (變數 i = 0; i < 長度(l); i++) { 印出(l[i].name); }");
        t.Write("other.aqua", "印出(\"OTHER\", 長度(匯入(\"Application\").LaunchFiles()));");
        Assert.Equal(0, Aqua(t, "build", "main.aqua", "other.aqua", "-o", "app.wasm").Code);
        Assert.Equal("2\n啟動 file.txt\nb.txt\n", Aqua(t, "run", "app.wasm", "啟動 file.txt", "b.txt").Output);
        Assert.Equal("0\n", Aqua(t, "run", "app.wasm").Output);
        Assert.Equal("OTHER 1\n", Aqua(t, "run", "app.wasm", "--entry", "other.aqua", "b.txt").Output);
        Assert.Equal("OTHER 1\n", Aqua(t, "run", "--entry", "OTHER.AQUA", "app.wasm", "b.txt").Output);
        Assert.Equal("1\nb.txt\n", Aqua(t, "run", "app.wasm", "--", "b.txt").Output);
        Assert.Equal("1\n啟動 file.txt\n", Aqua(t, "run", "main.aqua", "啟動 file.txt").Output);
    }

    [RuntimeFact]
    public void EntryOverrideWorksDuringCompilationExecutionAndWebExport()
    {
        using var t = new TestFolder(); t.Write("main.aqua", "1;"); t.Write("lib/other.aqua", "42;");
        Assert.Equal(0, Aqua(t, "build", "main.aqua", "lib/other.aqua", "--entry", "lib/other.aqua").Code);
        Assert.Equal("42\n", Aqua(t, "run", "main.wasm").Output);
        Assert.Equal("1\n", Aqua(t, "run", "main.wasm", "--entry", "main.aqua").Output);
        var missing = Aqua(t, "run", "main.wasm", "--entry", "missing.aqua");
        Assert.Equal(1, missing.Code); Assert.Contains("missing.aqua", missing.Error);
        if (WebFactAttribute.Missing() != null) return;
        Assert.Equal(0, Aqua(t, "build", "main.wasm", "--target", "web", "-o", "web", "--entry", "main.aqua").Code);
        using var json = JsonDocument.Parse(File.ReadAllText(t.FilePath("web/aquarius.json")));
        Assert.Equal("main.aqua", json.RootElement.GetProperty("entry").GetString());
    }

    [RuntimeFact]
    public void OneRelocatedWasmRunsAndExportsThroughTheCliWithSpacesAndUnicode()
    {
        using var t = new TestFolder();
        string main = t.Write("原始 folder/星泉 main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 印出(m.add(20,22)); 匯入(\"Files\").讀取資產文字(\"assets/文字.txt\", \"utf-8\");");
        string module = t.Write("原始 folder/lib/工具.aqua", "變數 加法, add=函式(a,b){回傳 a+b;};");
        t.Write("原始 folder/assets/文字.txt", "封裝成功");
        var built = Aqua(t, "build", main, module, "--root", "原始 folder", "--assets", "原始 folder/assets", "-o", "包 folder/星泉 app.wasm");
        Assert.Equal(0, built.Code); Assert.Equal("", built.Error); Assert.DoesNotContain("封裝成功", built.Output);
        Directory.Delete(t.FilePath("原始 folder"), true);
        var run = Aqua(t, "run", "包 folder/星泉 app.wasm");
        Assert.Equal(0, run.Code); Assert.Equal("42\n封裝成功\n", run.Output); Assert.Equal("", run.Error);
        // The program reads its asset through the application library (Files): the web pack must have its side module.
        if (WebFactAttribute.Missing() != null || !WebFactAttribute.HasSideModule("application")) return;
        var web = Aqua(t, "build", "包 folder/星泉 app.wasm", "--target", "web", "-o", "網站 folder");
        Assert.Equal(0, web.Code); Assert.Equal("", web.Error); Assert.Equal($"Website built: {t.FilePath("網站 folder")}\n", web.Output);
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("網站 folder/aquarius.json")));
        Assert.Equal("星泉 main.aqua", doc.RootElement.GetProperty("entry").GetString());
    }

    [WebFact]
    public void SourcesAndAssetsBuildDirectlyIntoAWebsiteWithoutAnIntermediateArtifact()
    {
        using var t = new TestFolder();
        t.Write("src/main.aqua", "變數 m=匯入(\"lib/工具.aqua\");m.值;");
        t.Write("src/lib/工具.aqua", "變數 值=42;"); t.Write("src/assets/字.txt", "中文");
        var result = Aqua(t, "build", "src/main.aqua", "src/lib/工具.aqua", "--root", "src", "--assets", "src/assets", "--target", "web", "-o", "web");
        Assert.True(result.Code == 0, result.Error);
        Assert.Empty(Directory.GetFiles(t.Path, "*.wasm"));
        var application = WasmApplication.Load(t.FilePath("web/program.wasm"));
        Assert.Equal(2, application.Scripts.Count); Assert.Contains("assets/字.txt", application.Assets.Keys);
        Assert.True(application.Program.Metadata.ExternalAssets);
    }

    [ExecutableFact]
    public void BuildsAWindowsExecutableWithAHiddenTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TestFolder();
        t.Write("main.aqua", "印出(\"無終端視窗 42\");");
        var build = Aqua(t, "build", "main.aqua", "--target", "windows", "-o", "app.exe", "--hide-terminal", "true");
        Assert.True(build.Code == 0, build.Output + build.Error);
        Assert.Equal($"Executable built: {t.FilePath("app.exe")}\n", build.Output);
        Assert.Empty(Directory.GetFiles(t.Path, "*.wasm"));
        Assert.Equal(System.Reflection.PortableExecutable.Subsystem.WindowsGui, Targets.ExecutableTargetTest.ReadSubsystem(t.FilePath("app.exe")));
        var run = Processes.Run(t.FilePath("app.exe"), t.Path);
        Assert.Equal(0, run.ExitCode); Assert.Equal("無終端視窗 42\n", run.Output); Assert.Equal("", run.Error);
        Assert.Equal(0, Aqua(t, "build", "main.aqua", "--target", "windows", "-o", "console.exe", "--hide-terminal", "false").Code);
        Assert.Equal(System.Reflection.PortableExecutable.Subsystem.WindowsCui, Targets.ExecutableTargetTest.ReadSubsystem(t.FilePath("console.exe")));
    }

    [Theory]
    [InlineData("unknown")][InlineData("build")][InlineData("run")][InlineData("run", "--entry")]
    [InlineData("build", "main.aqua", "--target", "native")]
    [InlineData("build", "main.wasm", "--target", "web")]
    [InlineData("build", "main.wasm", "--target", "windows")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--root", ".")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--assets", "assets")]
    [InlineData("build", "main.wasm")]
    [InlineData("build", "main.aqua", "readme.txt")]
    [InlineData("build", "main.aqua", "-o")][InlineData("build", "main.aqua", "--assets")]
    [InlineData("build", "main.aqua", "--root", ".", "--root", ".")]
    [InlineData("build", "main.aqua", "-o", "a.wasm", "-o", "b.wasm")]
    [InlineData("build", "main.aqua", "--platform", "web")]
    [InlineData("build", "main.aqua", "--locked")]
    [InlineData("repl", "extra")][InlineData("build", "--unknown")]
    [InlineData("build", "main.aqua", "--hide-terminal", "true")]
    [InlineData("build", "main.wasm", "--target", "web", "-o", "web", "--hide-terminal", "true")]
    [InlineData("build", "main.wasm", "--target", "linux", "-o", "app", "--hide-terminal", "true")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--hide-terminal")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--hide-terminal", "true", "--hide-terminal", "false")]
    [InlineData("build", "main.wasm", "--target", "web", "-o", "web", "--application-id", "org.example.app")]
    [InlineData("new")][InlineData("new", "app", "--type", "plugin")]
    [InlineData("library")][InlineData("library", "build", "--targets")]
    public void InvalidArgumentsHaveUsageExitCodeAndNoArtifacts(params string[] args)
    {
        using var t = new TestFolder(); t.Write("main.aqua", "42;"); t.Write("readme.txt", "x");
        var result = Aqua(t, args);
        Assert.Equal(2, result.Code); Assert.Contains("aqua build", result.Error);
        Assert.Empty(Directory.GetFiles(t.Path, "*.wasm")); Assert.False(Directory.Exists(t.FilePath("web")));
        Assert.Equal(["main.aqua", "readme.txt"], Directory.GetFileSystemEntries(t.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("build", "missing.aqua")][InlineData("run", "missing.wasm")][InlineData("run", "missing.aqua")]
    [InlineData("build", "bad.aqua")][InlineData("run", "bad.wasm")][InlineData("run", "bad.aqua")][InlineData("run", "old.wasm")]
    [InlineData("build", "bad.wasm", "--target", "web", "-o", "web")]
    [InlineData("build", "old.wasm", "--target", "web", "-o", "web")]
    [InlineData("build", "bad.wasm", "--target", "windows", "-o", "app.exe")]
    [InlineData("build", "bad.wasm", "--target", "android", "-o", "app.apk")]
    [InlineData("new", "bad name")]
    public void CompilationAndIoFailuresAreHandledWithoutStackTraces(params string[] args)
    {
        using var t = new TestFolder(); t.Write("bad.aqua", "變數 = ;"); t.Write("bad.wasm", "invalid archive");
        t.Write("old.wasm", Packaging.WasmArtifactTest.Abi2Artifact());
        var result = Aqua(t, args);
        Assert.Equal(1, result.Code); Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Unhandled exception", result.Error); Assert.DoesNotContain("   at ", result.Error);
        if (args.Contains("old.wasm")) Assert.Contains("Rebuild it from source", result.Error);
        if (args.Contains("bad.aqua")) Assert.Contains("bad.aqua", result.Error);
        Assert.False(Directory.Exists(t.FilePath("web"))); Assert.False(File.Exists(t.FilePath("app.exe")));
    }

    [RuntimeFact]
    public void HelpReplExplicitTargetAndDashFilenamesWork()
    {
        using var t = new TestFolder(); t.Write("-main.aqua", "42;");
        var help = Aqua(t, "--help");
        Assert.Equal(0, help.Code);
        foreach (string usage in new[] { "aqua build", "aqua run", "aqua repl", "aqua new", "--target web", "--hide-terminal", "aqua library build" }) Assert.Contains(usage, help.Output);
        Assert.Equal(help.Output, Aqua(t).Output);
        Assert.Equal(help.Output, Aqua(t, "build", "--help").Output);
        var repl = AquaCommand.RunWithInput(t.Path, "40 + 2;\n", "repl");
        Assert.Equal(0, repl.Code); Assert.Contains("AquariusLang REPL", repl.Output); Assert.Contains(">> 42\n", repl.Output);
        Assert.Equal(0, Aqua(t, "build", "--target", "wasm", "--", "-main.aqua").Code);
        Assert.True(File.Exists(t.FilePath("-main.wasm")));
        Assert.Equal("42\n", Aqua(t, "run", "--", "-main.wasm").Output);
    }

    [RuntimeFact]
    public void NewCreatesProjectsAndLibrariesThatBuildAndRun()
    {
        using var t = new TestFolder();
        string? bottles = Environment.GetEnvironmentVariable("AQUA_BOTTLE_HOME");
        Environment.SetEnvironmentVariable("AQUA_BOTTLE_HOME", t.FilePath("bottles"));
        try {
            var created = Aqua(t, "new", "星泉app");
            Assert.Equal(0, created.Code); Assert.Contains("Created executable project", created.Output);
            var project = ProjectManifest.Load(t.FilePath("星泉app"));
            Assert.Equal(2, project.ManifestVersion); Assert.Equal("星泉app", project.Name); Assert.Equal("main.aqua", project.Entry);
            Assert.Equal("Hello, Aquarius!\n", Aqua(t, "run", "星泉app").Output);
            var built = Aqua(t, "build", "星泉app");
            Assert.Equal(0, built.Code);
            Assert.Equal("Hello, Aquarius!\n", Aqua(t, "run", t.FilePath("星泉app/dist/星泉app.wasm")).Output);
            var again = Aqua(t, "new", "星泉app");
            Assert.Equal(1, again.Code); Assert.Contains("already exists", again.Error);

            // A library: its programs (tests/) import it by name; a library itself is not an application.
            Assert.Equal(0, Aqua(t, "new", "mylib", "--type", "library", "-o", "函式庫").Code);
            var library = ProjectManifest.Load(t.FilePath("函式庫")).Library!;
            Assert.Equal(["mylib"], library.Modules.Single().Names);
            t.Write("函式庫/tests/case.aqua", "匯入(\"mylib\").add(40, 2);");
            Assert.Equal("42\n", Aqua(t, "run", "函式庫/tests/case.aqua").Output);
            Assert.Equal(0, Aqua(t, "build", "函式庫/tests/case.aqua", "--root", "函式庫/tests", "-o", "case.wasm").Code);
            var program = WasmProgram.Load(t.FilePath("case.wasm"));
            Assert.Equal(".aquarius/mylib/main.aqua", program.Metadata.ImportTable["mylib"]);
            Assert.Equal("42\n", Aqua(t, "run", "case.wasm").Output);
            var notAnApplication = Aqua(t, "build", "函式庫");
            Assert.Equal(1, notAnApplication.Code); Assert.Contains("is a library", notAnApplication.Error);
        } finally { Environment.SetEnvironmentVariable("AQUA_BOTTLE_HOME", bottles); }
    }

    [RuntimeFact]
    public void ExternalProgramFailuresAreLanguageErrors()
    {
        using var t = new TestFolder(); t.Write("main.aqua", "執行檔案(\"aquarius-no-such-executable-839019\", []);");
        var result = Aqua(t, "run", "main.aqua");
        Assert.Equal(1, result.Code); Assert.StartsWith("ERROR: execFile / 執行檔案:", result.Output);
    }

    [RuntimeFact]
    public void SupportingPythonScriptRunsFromAPackagedAssetWithSpacesAfterSourceRemoval()
    {
        if (ResolvePython() is not { } python) return; // Optional: needs Python 3.
        using var t = new TestFolder();
        t.Write("src/main.aqua", $"執行檔案(\"{python.Replace('\\', '/')}\", [目前工作目錄 + \"/tools folder/worker.py\", \"hello\", \"world wide\"]);");
        t.Write("src/tools folder/worker.py", "import sys\nprint('PACKAGED-PYTHON', sys.argv[1:])\n");
        Assert.Equal(0, Aqua(t, "build", "src/main.aqua", "--root", "src", "--assets", "src/tools folder", "-o", "app.wasm").Code);
        Directory.Delete(t.FilePath("src"), true);
        var result = Aqua(t, "run", "app.wasm");
        Assert.Equal(0, result.Code); Assert.Equal("", result.Error);
        Assert.Contains("PACKAGED-PYTHON ['hello', 'world wide']", result.Output);
        Assert.Equal(["app.wasm"], Directory.GetFileSystemEntries(t.Path).Select(Path.GetFileName));
    }

    private static string? ResolvePython()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("AQUARIUS_PYTHON") is { Length: > 0 } configured) candidates.Add(configured);
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" })
                candidates.Add(Path.Combine(directory.Trim('"'), name));
        foreach (string candidate in candidates.Distinct()) {
            // Windows Store aliases start but lose arguments and output.
            if (!File.Exists(candidate) || candidate.Replace('/', '\\').Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) continue;
            try {
                var probe = Processes.Run(candidate, Path.GetTempPath(), ["-c", "import sys; print('AQUARIUS-PYTHON-3' if sys.version_info.major == 3 else 'unsupported')"], timeoutMs: 10_000);
                if (probe.ExitCode == 0 && probe.Output.Trim() == "AQUARIUS-PYTHON-3") return Path.GetFullPath(candidate);
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException) { }
        }
        return null;
    }
}

/// <summary>The real <c>aqua</c> entry point in its own process: UTF-8 standard streams and the runtime inheriting them.</summary>
public class CliProcessTest
{
    private static RunResult Aqua(string directory, string? input, params string[] args)
    {
        string assembly = typeof(CompilerCommandLine).Assembly.Location;
        string apphost = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "aqua.exe" : "aqua");
        bool direct = File.Exists(apphost);
        string program = direct ? apphost : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return Processes.Run(program, directory, direct ? args : [assembly, .. args], timeoutMs: 60_000, input: input);
    }

    [RuntimeFact]
    public void BuildAndRunWriteUtf8ThroughTheRealCommand()
    {
        using var t = new TestFolder(); t.Write("main.aqua", "印出(\"星泉 ✓\"); 42;");
        var built = Aqua(t.Path, null, "build", "main.aqua");
        Assert.Equal(0, built.ExitCode); Assert.Equal("", built.Error); Assert.Contains("Compiled 1 script(s)", built.Output);
        var run = Aqua(t.Path, null, "run", "main.wasm");
        Assert.Equal(0, run.ExitCode); Assert.Equal("星泉 ✓\n42\n", run.Output); Assert.Equal("", run.Error);
        var failed = Aqua(t.Path, null, "run", "missing.wasm");
        Assert.Equal(1, failed.ExitCode); Assert.DoesNotContain("Unhandled exception", failed.Error);
    }

    [RuntimeFact]
    public void PipedReplInputIsReadAsUtf8()
    {
        using var t = new TestFolder();
        var repl = Aqua(t.Path, "變數 甲 = 40;\n甲 + 2;\n印出(\"星泉\");\n", "repl");
        Assert.Equal(0, repl.ExitCode);
        Assert.Contains(">> 42\n", repl.Output); Assert.Contains(">> 星泉\n", repl.Output);
        Assert.DoesNotContain("ILLEGAL", repl.Output);
    }
}
