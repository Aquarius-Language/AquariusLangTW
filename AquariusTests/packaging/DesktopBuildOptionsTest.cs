using System.Reflection.PortableExecutable;
using AquariusLang.Build;
using AquariusLang.Cli;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public class DesktopBuildOptionsTest {
    internal static byte[] Template => File.ReadAllBytes(typeof(CompilerCommandLine).Assembly.Location);
    internal static Subsystem ReadSubsystem(string path) {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.PEHeader!.Subsystem;
    }

    [Theory]
    [InlineData(null, Subsystem.WindowsCui)]
    [InlineData("false", Subsystem.WindowsCui)]
    [InlineData("true", Subsystem.WindowsGui)]
    public void OnlyOptedInWindowsExportsChangeSubsystemAndKeepTheTemplateAndPayload(string? value, Subsystem expected) {
        using var t = new ApplicationTestDirectory();
        byte[] template = Template;
        ExecutableBundleTest.Pack(t, "packs/win-x64", "win-x64", false, templateBytes: template);
        string source = t.Write("main.aqua", "42;"), wasm = t.FilePath("app.wasm"), output = t.FilePath("app.exe");
        WasmApplication.Compile([source], wasm, t.Path);
        var options = value == null ? null : new Dictionary<string, string> { [SelfContainedExecutableTarget.HideTerminalOption] = value };
        new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs")).Build(new(wasm, output, Options: options));
        Assert.Equal(expected, ReadSubsystem(output));
        Assert.Equal(template, File.ReadAllBytes(t.FilePath("packs/win-x64/host.exe")));
        using var executable = File.OpenRead(output);
        Assert.Equal("main.aqua", ExecutableBundle.Load(executable).EntryPoint);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("invalid")]
    public void InvalidTemplateOrOptionPreservesOutputAndRemovesTheStage(string value) {
        using var t = new ApplicationTestDirectory();
        ExecutableBundleTest.Pack(t, "packs/win-x64", "win-x64", false);
        string source = t.Write("main.aqua", "42;"), wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([source], wasm, t.Path);
        string output = t.Write("dist/app.exe", "previous output");
        var options = new Dictionary<string, string> { [SelfContainedExecutableTarget.HideTerminalOption] = value };
        var target = new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
        var error = Record.Exception(() => target.Build(new(wasm, output, Options: options)));
        if (value == "invalid") Assert.IsType<ArgumentException>(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Single(Directory.GetFiles(t.FilePath("dist")));
    }

    [Fact] public void OlderPacksStillBuildConsoleAppsButExplainWhyGuiAppsNeedAnUpdatedHost() {
        using var t = new ApplicationTestDirectory();
        ExecutableBundleTest.Pack(t, "packs/win-x64", "win-x64", false, hideTerminal: false);
        string source = t.Write("main.aqua", "42;"), wasm = t.FilePath("app.wasm"), output = t.FilePath("app.exe");
        WasmApplication.Compile([source], wasm, t.Path);
        var target = new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
        target.Build(new(wasm, output));
        byte[] previous = File.ReadAllBytes(output);
        var error = Assert.Throws<InvalidDataException>(() => target.Build(new(wasm, output,
            Options: new Dictionary<string, string> { [SelfContainedExecutableTarget.HideTerminalOption] = "true" })));
        Assert.Contains("publish-apphost.ps1", error.Message);
        Assert.Equal(previous, File.ReadAllBytes(output));
    }
}
