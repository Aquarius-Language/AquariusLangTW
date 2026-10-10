using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AquariusLang.Cli;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public class UnifiedCliSmokeTest {
    [Fact]
    public void SourceModulesAndAssetsBuildDirectlyIntoAWebsiteWithoutAnIntermediateArtifact() {
        using var t=new ApplicationTestDirectory();
        t.Write("src/main.aqua","變數 m=匯入(\"lib/工具.aqua\");m.值;");
        t.Write("src/lib/工具.aqua","變數 值=42;");t.Write("src/assets/字.txt","中文");
        var result=Run(t.Path,"build","src/main.aqua","src/lib/工具.aqua","--root","src","--assets","src/assets","--target","web","-o","web");
        Assert.True(result.Code==0,result.Error);
        Assert.Empty(Directory.GetFiles(t.Path,"*.wasm"));
        var application=WasmApplication.Load(t.FilePath("web/program.wasm"));
        Assert.Equal(2,application.Scripts.Count);Assert.Contains("assets/字.txt",application.Assets.Keys);
        Assert.False(File.Exists(t.FilePath("web/vm.mjs")));
    }
    internal static (int Code, string Output, string Error) Run(string directory, params string[] args) {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(CompilerCommandLine).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("aqua did not finish."); }
        return (process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult());
    }

    [Fact] public void OneRelocatedWasmRunsAndExportsThroughRealCliWithSpacesAndUnicode() {
        using var t = new ApplicationTestDirectory();
        string main = t.Write("原始 folder/星泉 main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 印出(m.add(20,22)); 變數 g=匯入(\"GL\"); g.ReadText(目前工作目錄+\"/assets/文字.txt\");");
        string module = t.Write("原始 folder/lib/工具.aqua", "變數 加法, add=函式(a,b){回傳 a+b;};");
        t.Write("原始 folder/assets/文字.txt", "封裝成功");
        var built = Run(t.Path, "build", main, module, "--root", "原始 folder", "--assets", "原始 folder/assets", "-o", "包 folder/星泉 app.wasm");
        Assert.Equal(0, built.Code); Assert.Equal("", built.Error); Assert.DoesNotContain("封裝成功", built.Output);
        Directory.Delete(t.FilePath("原始 folder"), true);
        var run = Run(t.Path, "run", "包 folder/星泉 app.wasm");
        Assert.Equal(0, run.Code); Assert.Equal("42\n封裝成功\n", run.Output); Assert.Equal("", run.Error);
        var web = Run(t.Path, "build", "包 folder/星泉 app.wasm", "--target", "web", "-o", "網站 folder");
        Assert.Equal(0, web.Code); Assert.Equal("", web.Error); Assert.DoesNotContain("封裝成功", web.Output);
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("網站 folder/program.json")));
        Assert.Equal("星泉 main.aqua", doc.RootElement.GetProperty("entry").GetString());
    }

    [Fact] public void EntryOverrideWorksDuringCompilationExecutionAndWebExport() {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "1;"); t.Write("lib/other.aqua", "42;");
        Assert.Equal(0, Run(t.Path, "build", "main.aqua", "lib/other.aqua", "--entry", "lib/other.aqua").Code);
        Assert.Equal("42\n", Run(t.Path, "run", "main.wasm").Output);
        Assert.Equal("1\n", Run(t.Path, "run", "main.wasm", "--entry", "main.aqua").Output);
        Assert.Equal(0, Run(t.Path, "build", "main.wasm", "--target", "web", "-o", "web", "--entry", "main.aqua").Code);
        Assert.Equal(1, Run(t.Path, "run", "main.wasm", "--entry", "missing.aqua").Code);
    }

    [Theory]
    [InlineData("unknown")][InlineData("build")][InlineData("run")][InlineData("run", "main.aqua")]
    [InlineData("build", "main.aqua", "--target", "native")]
    [InlineData("build", "main.wasm", "--target", "web")]
    [InlineData("build", "main.wasm", "--target", "windows")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--root", ".")]
    [InlineData("build", "main.wasm", "--target", "windows", "-o", "app.exe", "--assets", "assets")]
    [InlineData("build", "main.aqua", "-o")][InlineData("build", "main.aqua", "--assets")]
    [InlineData("build", "main.aqua", "--root", ".", "--root", ".")]
    [InlineData("build", "main.aqua", "-o", "a.wasm", "-o", "b.wasm")]
    [InlineData("run", "main.wasm", "--assets", "assets")]
    [InlineData("repl", "extra")][InlineData("build", "--unknown")]
    public void InvalidArgumentsHaveUsageExitCodeAndNoArtifacts(params string[] args) {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "42;");
        var result = Run(t.Path, args); Assert.Equal(2, result.Code); Assert.Contains("aqua build", result.Error);
        Assert.Empty(Directory.GetFiles(t.Path, "*.wasm")); Assert.False(Directory.Exists(t.FilePath("web")));
    }

    [Theory]
    [InlineData("build", "missing.aqua")][InlineData("run", "missing.wasm")]
    [InlineData("build", "bad.aqua")][InlineData("run", "bad.wasm")]
    [InlineData("build", "bad.wasm", "--target", "web", "-o", "web")]
    [InlineData("build", "bad.wasm", "--target", "windows", "-o", "app.exe")]
    public void CompilationAndIoFailuresAreHandledWithoutStackTraces(params string[] args) {
        using var t = new ApplicationTestDirectory(); t.Write("bad.aqua", "變數 = ;"); t.Write("bad.wasm", "invalid archive");
        var result = Run(t.Path, args); Assert.Equal(1, result.Code); Assert.NotEmpty(result.Error); Assert.DoesNotContain("Unhandled exception", result.Error);
    }

    [Fact] public void HelpReplExplicitTargetAndDashFilenamesWork() {
        using var t = new ApplicationTestDirectory(); t.Write("-main.aqua", "42;");
        Assert.Equal(0, Run(t.Path, "--help").Code); Assert.Contains("REPL mode", Run(t.Path, "repl").Output);
        Assert.Equal(0, Run(t.Path, "build", "--target", "wasm", "--", "-main.aqua").Code);
        Assert.Equal("42\n", Run(t.Path, "run", "--", "-main.wasm").Output);
    }

    [Fact] public void PublishedStyleApphostCanBuildAndRun() {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "42;");
        string executable = Path.Combine(Path.GetDirectoryName(typeof(CompilerCommandLine).Assembly.Location)!, OperatingSystem.IsWindows() ? "aqua.exe" : "aqua");
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = t.Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "build", "main.aqua" }) start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new TimeoutException(); }
        Assert.Equal(0, p.ExitCode); Assert.Equal("", error.GetAwaiter().GetResult()); Assert.Contains("Compiled", output.GetAwaiter().GetResult());
        Assert.Equal("42\n", Run(t.Path, "run", "main.wasm").Output);
    }

    [Fact] public void MissingExternalExecutableReturnsALanguageErrorInsteadOfCrashing() {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "執行檔案(\"aquarius-no-such-executable-839019\", []);");
        Assert.Equal(0, Run(t.Path, "build", "main.aqua").Code);
        var result = Run(t.Path, "run", "main.wasm");
        Assert.Equal(1, result.Code); Assert.Contains("執行檔案", result.Output); Assert.DoesNotContain("Unhandled exception", result.Error);
    }

    [Fact] public void SupportingPythonScriptRunsFromPackagedAssetWithSpacesAfterSourceRemoval() {
        using var t = new ApplicationTestDirectory();
        string python = ResolvePython().Replace('\\', '/').Replace("\"", "\\\"");
        t.Write("src/main.aqua", $"執行檔案(\"{python}\", [目前工作目錄+\"/tools folder/worker.py hello world\"]);");
        t.Write("src/tools folder/worker.py", "import sys\nprint('PACKAGED-PYTHON', sys.argv[1:])\n");
        var built = Run(t.Path, "build", "src/main.aqua", "--root", "src", "--assets", "src/tools folder", "-o", "app.wasm");
        Assert.Equal(0, built.Code); Directory.Delete(t.FilePath("src"), true);
        var result = Run(t.Path, "run", "app.wasm");
        Assert.Equal(0, result.Code); Assert.Contains("PACKAGED-PYTHON ['hello', 'world']", result.Output); Assert.Equal("", result.Error);
    }

    private static string ResolvePython() {
        string? configured = System.Environment.GetEnvironmentVariable("AQUARIUS_PYTHON");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        else {
            // Use a native interpreter: Windows batch shims and Store aliases can
            // start successfully while losing the script's arguments or output.
            foreach (string directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (string name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" })
                    candidates.Add(Path.Combine(directory.Trim('"'), name));
            }
            if (OperatingSystem.IsWindows()) {
                string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                foreach (string root in new[] { Path.Combine(home, ".pyenv", "pyenv-win", "versions"), Path.Combine(local, "Programs", "Python") }) {
                    if (Directory.Exists(root))
                        candidates.AddRange(Directory.GetDirectories(root).OrderByDescending(path => path, StringComparer.Ordinal).Select(path => Path.Combine(path, "python.exe")));
                }
            }
        }
        foreach (string candidate in candidates.Distinct()) {
            if (OperatingSystem.IsWindows() && (Path.GetExtension(candidate) != ".exe" || candidate.Replace('/', '\\').Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(candidate)) continue;
            var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import sys; print('AQUARIUS-PYTHON-3' if sys.version_info.major == 3 else 'unsupported')");
            try {
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) { process.Kill(true); continue; }
                if (process.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() == "AQUARIUS-PYTHON-3") return Path.GetFullPath(candidate);
                error.GetAwaiter().GetResult();
            } catch (System.ComponentModel.Win32Exception) { }
        }
        throw new InvalidOperationException("A working Python 3 interpreter is required. Set AQUARIUS_PYTHON to its executable path, or install it on PATH (Windows pyenv and per-user installations are also detected).");
    }
}
