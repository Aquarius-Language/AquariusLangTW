using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AquariusREPL.runtime;

/// <summary>Runs the actual desktop entry point in a separate process, including UTF-8 output and exit status.</summary>
public class BottleCliSmokeTest {
    private static (int Code, string Output, string Error) Run(string workingDirectory, params string[] arguments) {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(ScriptRunner).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Aquarius CLI did not finish in 30 seconds."); }
        return (process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult());
    }

    [Fact]
    public void NativeAppHostCanCompileAndExecuteABottle() {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", "42;");
        string assembly = typeof(ScriptRunner).Assembly.Location;
        string executable = Path.Combine(Path.GetDirectoryName(assembly)!,
            Path.GetFileNameWithoutExtension(assembly) + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable), $"Desktop apphost missing: {executable}");
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = temp.Path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-cr"); start.ArgumentList.Add("main.aqua");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Desktop apphost did not finish."); }
        Assert.Equal(0, process.ExitCode);
        Assert.EndsWith("42\n", output.GetAwaiter().GetResult().Replace("\r\n", "\n"));
        Assert.Equal("", error.GetAwaiter().GetResult());
        Assert.True(File.Exists(temp.FilePath("main.bottle")));
    }

    [Fact]
    public void CompileOnlyPreservesRequestedPathsAndDoesNotExecuteAnyScripts() {
        using var temp = new BottleTestDirectory();
        temp.Write("test1.aqua", "印出(\"MAIN-RAN\"); 42;");
        temp.Write("test/test2.aqua", "印出(\"MODULE-RAN\"); 2;");
        temp.Write("test/test3.aqua", "3;");
        var compiled = Run(temp.Path, "-c", "./test1.aqua", "./test/test2.aqua", "./test/test3.aqua");
        Assert.Equal(0, compiled.Code);
        Assert.DoesNotContain("MAIN-RAN", compiled.Output);
        Assert.DoesNotContain("MODULE-RAN", compiled.Output);
        Assert.Equal("", compiled.Error);
        using var zip = ZipFile.OpenRead(temp.FilePath("test1.bottle"));
        Assert.Equal(new[] { "bottle.json", "test1.rius", "test/test2.rius", "test/test3.rius" }, zip.Entries.Select(entry => entry.FullName));
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("-cr")]
    public void CompileAndLaterRunWithSpacesAndUnicodePathsAfterRemovingSources(string flag) {
        using var temp = new BottleTestDirectory();
        string main = temp.Write("source folder/星泉 main.aqua", "變數 m=匯入(\"test folder/航行工具.aqua\"); 印出(\"星泉運行\"); m.值;");
        string module = temp.Write("source folder/test folder/航行工具.aqua", "變數 值=42;");
        var compiled = Run(temp.Path, flag, "--root", "source folder", "-o", "星泉 bottle.bottle", main, module);
        Assert.Equal(0, compiled.Code);
        Assert.Equal("", compiled.Error);
        if (flag == "-cr") Assert.EndsWith("星泉運行\n42\n", compiled.Output);
        else Assert.DoesNotContain("星泉運行", compiled.Output);
        Directory.Delete(temp.FilePath("source folder"), true);
        var executed = Run(temp.Path, "星泉 bottle.bottle");
        Assert.Equal(0, executed.Code);
        Assert.Equal("星泉運行\n42\n", executed.Output);
        Assert.Equal("", executed.Error);
    }

    [Fact]
    public void CompileRunExecutesFirstInputAndSelectedEntryCanRunLater() {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", "42;"); temp.Write("lib/other.aqua", "7;");
        var result = Run(temp.Path, "-cr", "main.aqua", "lib/other.aqua");
        Assert.Equal(0, result.Code);
        Assert.EndsWith("42\n", result.Output);
        Assert.DoesNotContain("\n7\n", result.Output);
        result = Run(temp.Path, "--entry", "lib/other.rius", "main.bottle");
        Assert.Equal(0, result.Code); Assert.Equal("7\n", result.Output);
        Assert.Equal(1, Run(temp.Path, "--entry", "missing.rius", "main.bottle").Code);
    }

    [Theory]
    [InlineData("變數 值 = 42;", 0)]
    [InlineData("印出(\"星泉\");", 0)]
    [InlineData("", 0)]
    [InlineData("未知;", 1)]
    [InlineData("匯入(\"missing.aqua\");", 1)]
    public void SourceAndBottleExitCodesMatchForVoidResultsAndRuntimeErrors(string source, int expected) {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", source);
        Assert.Equal(expected, Run(temp.Path, "main.aqua").Code);
        Assert.Equal(0, Run(temp.Path, "-c", "main.aqua").Code);
        Assert.Equal(expected, Run(temp.Path, "main.bottle").Code);
        Assert.Equal(expected, Run(temp.Path, "-cr", "main.aqua").Code);
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("-cr")]
    [InlineData("--unknown")]
    [InlineData("-c", "-o")]
    [InlineData("-c", "--root")]
    [InlineData("-c", "--unknown", "main.aqua")]
    [InlineData("-c", "-o", "one.bottle", "-o", "two.bottle", "main.aqua")]
    [InlineData("-c", "--root", ".", "--root", ".", "main.aqua")]
    [InlineData("-c", "missing.aqua")]
    [InlineData("main.aqua", "main.aqua")]
    public void InvalidArgumentsFailCleanlyWithoutCreatingAPackage(params string[] arguments) {
        using var temp = new BottleTestDirectory(); temp.Write("main.aqua", "42;");
        var result = Run(temp.Path, arguments);
        Assert.Equal(1, result.Code); Assert.NotEmpty(result.Error);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bottle"));
    }

    [Fact]
    public void SyntaxErrorsAndMalformedArchivesFailCleanly() {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", "變數 = ;");
        Assert.Equal(1, Run(temp.Path, "main.aqua").Code);
        Assert.Equal(1, Run(temp.Path, "-c", "main.aqua").Code);
        Assert.Equal(1, Run(temp.Path, "-cr", "main.aqua").Code);
        Assert.False(File.Exists(temp.FilePath("main.bottle")));
        temp.Write("bad.bottle", "not a zip");
        var result = Run(temp.Path, "bad.bottle");
        Assert.Equal(1, result.Code); Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Unhandled exception", result.Error);
        using (var zip = ZipFile.Open(temp.FilePath("missing-manifest.bottle"), ZipArchiveMode.Create)) zip.CreateEntry("main.rius");
        result = Run(temp.Path, "missing-manifest.bottle");
        Assert.Equal(1, result.Code); Assert.DoesNotContain("Unhandled exception", result.Error);
    }

    [Fact]
    public void HelpReplSourceAndDisassemblyStillWorkAndDashFilenamesCanBeCompiled() {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", "40+2;"); temp.Write("-script.aqua", "42;");
        var help = Run(temp.Path, "--help");
        Assert.Equal(0, help.Code); Assert.Contains("-c|-cr", help.Output); Assert.Contains(".bottle", help.Output);
        Assert.Contains("REPL mode", Run(temp.Path).Output);
        Assert.Equal("42\n", Run(temp.Path, "main.aqua").Output);
        Assert.Contains("Add", Run(temp.Path, "--disassemble", "main.aqua").Output);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bottle"));
        Assert.Equal(0, Run(temp.Path, "-c", "--", "-script.aqua").Code);
        Assert.True(File.Exists(temp.FilePath("-script.bottle")));
    }
}
