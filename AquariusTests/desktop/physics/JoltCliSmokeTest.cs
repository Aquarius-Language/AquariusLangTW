using System.Diagnostics;
using System.Text;
using AquariusLang.Desktop.runtime;

namespace AquariusLang.Desktop.Physics;

[Collection("Jolt physics")]
public class JoltCliSmokeTest {
    private static (int Code, string Output, string Error) Run(string workingDirectory, params string[] arguments) {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(ScriptRunner).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Jolt CLI smoke test did not finish in 30 seconds."); }
        return (process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult());
    }

    [Theory, InlineData(false), InlineData(true)]
    public void ExampleExecutesAsSourceAndSourceFreeWasmInSeparateProcesses(bool chinese) {
        string example = Path.Combine(AppContext.BaseDirectory, "examples", "jolt_physics", "main.aqua");
        using var temp = new ApplicationTestDirectory();
        string text = File.ReadAllText(example);
        string source = temp.Write("main.aqua", chinese ? AquariusTests.BilingualTestSource.Chinese(text) : text);
        var result = Run(temp.Path, source);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal("JOLT_SMOKE_OK\n真\n", result.Output); Assert.Empty(result.Error);
        result = Run(temp.Path, "-c", "main.aqua");
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        File.Delete(source);
        result = Run(temp.Path, "main.wasm");
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal("JOLT_SMOKE_OK\n真\n", result.Output); Assert.Empty(result.Error);
    }

    [Fact]
    public void InvalidPhysicsInputFailsSourceAndWasmWithExitCodeOne() {
        using var temp = new ApplicationTestDirectory();
        temp.Write("bad.aqua", JoltTest.World + "w.Step(0,1);");
        var source = Run(temp.Path, "bad.aqua");
        Assert.Equal(1, source.Code); Assert.Contains("Jolt.Step", source.Output); Assert.Empty(source.Error);
        Assert.Equal(0, Run(temp.Path, "-c", "bad.aqua").Code);
        var wasm = Run(temp.Path, "bad.wasm");
        Assert.Equal(1, wasm.Code); Assert.Equal(source.Output, wasm.Output); Assert.Empty(wasm.Error);
    }

    [Fact]
    public void ExampleMirrorMatchesAndDisassemblesWithoutNativeInitialization() {
        string example = Path.Combine(AppContext.BaseDirectory, "examples", "jolt_physics", "main.aqua");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AquariusLang.sln"))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Equal(File.ReadAllBytes(Path.Combine(root!.FullName, "examples", "jolt_physics", "main.aqua")), File.ReadAllBytes(example));
        var result = Run(AppContext.BaseDirectory, "--disassemble", example);
        Assert.Equal(0, result.Code); Assert.NotEmpty(result.Output); Assert.Empty(result.Error);
    }
}
