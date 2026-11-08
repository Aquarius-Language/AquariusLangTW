using System.Text.Json;
using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Packaging;
using AquariusTests.Packaging;

namespace AquariusTests.Targets;

/// <summary>A test that builds iOS applications: a Mac with the runtime pack of <paramref name="target"/>.</summary>
public sealed class IosFactAttribute : FactAttribute
{
    public IosFactAttribute(string target = "ios-simulator-arm64")
    {
        if (!OperatingSystem.IsMacOS()) Skip = "iOS applications are built on a Mac.";
        else if (AquariusInstallation.Current is not { } installation || !File.Exists(Path.Combine(installation.RuntimePack(target), "libaquarius.a")))
            Skip = $"Needs the {target} runtime pack (scripts/build-runtime.ps1 -Targets {target}).";
    }
}

/// <summary>The ios target (<see cref="IosTarget"/>): an ahead-of-time compiled, signed .app for the simulator or devices.</summary>
[Collection(ExecutableCollection.Name)]
public class IosTargetTest
{
    private static string Compile(TestFolder t, string source)
    {
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("main.aqua", source)], wasm, t.Path);
        return wasm;
    }
    private static RunResult Tool(string program, params string[] arguments) => Processes.Run(program, Path.GetTempPath(), arguments, timeoutMs: 120_000);
    private static Dictionary<string, string> Simulator => new() { [IosTarget.SimulatorOption] = "true" };

    [Fact]
    public void InfoPlistDeclaresTheSceneAndThePlatform()
    {
        string plist = IosTarget.InfoPlist("星泉 & Co", "org.example.app", "星泉", simulator: true);
        Assert.Contains("<key>CFBundleExecutable</key><string>星泉 &amp; Co</string>", plist);
        Assert.Contains("<string>iPhoneSimulator</string>", plist);
        Assert.Contains("<key>UISceneDelegateClassName</key><string>AquaSceneDelegate</string>", plist);
        Assert.Contains("<string>iPhoneOS</string>", IosTarget.InfoPlist("App", "org.example.app", "App", simulator: false));
    }

    [Fact]
    public void BuildsCheckTheirOptionsBeforeTouchingTheOutput()
    {
        using var t = new TestFolder();
        var target = new IosTarget();
        Assert.Contains("*.app", Assert.Throws<ArgumentException>(() => target.Build(new WasmBuildRequest(t.FilePath("app.wasm"), t.FilePath("App")))).Message);
        var profile = new Dictionary<string, string>(Simulator) { [IosTarget.ProvisioningProfileOption] = t.Write("app.mobileprovision", "x") };
        Assert.Contains("device", Assert.Throws<ArgumentException>(() => target.Build(new WasmBuildRequest(t.FilePath("app.wasm"), t.FilePath("App.app"), Options: profile))).Message);
        Assert.Throws<ArgumentException>(() => target.Build(new WasmBuildRequest(Compile(t, "印出(1);"), t.FilePath("App.app"),
            Options: new Dictionary<string, string>(Simulator) { [IosTarget.BundleIdOption] = "no dots" })));
        Assert.False(Directory.Exists(t.FilePath("App.app")));
    }

    [IosFact]
    public void SimulatorAppsAreCompiledAheadOfTimeSignedAndCarryTheirProgram()
    {
        using var t = new TestFolder();
        string app = t.FilePath("dist/星泉.app");
        new IosTarget().Build(new WasmBuildRequest(Compile(t, "印出(\"IOS_OK\", 是iOS());"), app, Options: Simulator));
        string executable = Path.Combine(app, "星泉");
        Assert.True(File.Exists(executable));
        using (var payload = File.OpenRead(Path.Combine(app, IosTarget.Payload))) Assert.Equal("main.aqua", ExecutableBundle.Load(payload).EntryPoint);
        var verified = Tool("/usr/bin/codesign", "--verify", "--strict", "--verbose=2", app);
        Assert.True(verified.ExitCode == 0, verified.Output + verified.Error);
        Assert.Contains("platform IOSSIMULATOR", Tool("/usr/bin/xcrun", "vtool", "-show-build", executable).Output);
        // Rebuilding replaces the bundle.
        new IosTarget().Build(new WasmBuildRequest(Compile(t, "印出(2);"), app, Options: Simulator));
        Assert.Equal(0, Tool("/usr/bin/codesign", "--verify", "--strict", app).ExitCode);
    }

    [IosFact("ios-arm64")]
    public void DeviceAppsTargetIos()
    {
        using var t = new TestFolder();
        string app = t.FilePath("Device.app");
        new IosTarget().Build(new WasmBuildRequest(Compile(t, "印出(1);"), app));
        Assert.Contains("platform IOS\n", Tool("/usr/bin/xcrun", "vtool", "-show-build", Path.Combine(app, "Device")).Output);
        Assert.Contains("<string>iPhoneOS</string>", File.ReadAllText(Path.Combine(app, "Info.plist")));
    }

    /// <summary>Runs on a booted iOS simulator (boot one with xcrun simctl boot): the compiled program's output is the
    /// desktop runtime's.</summary>
    [IosFact]
    public void SimulatorAppsPrintWhatTheDesktopRuntimePrints()
    {
        var booted = Tool("/usr/bin/xcrun", "simctl", "list", "devices", "booted", "--json");
        string? udid = JsonDocument.Parse(booted.Output).RootElement.GetProperty("devices").EnumerateObject()
            .SelectMany(runtime => runtime.Value.EnumerateArray()).Select(d => d.GetProperty("udid").GetString()).FirstOrDefault();
        if (udid == null) return;   // nothing to run on: the build tests above still ran
        using var t = new TestFolder();
        const string Source = "變數 fib = 函式(n: 整數): 整數 { 如果 (n < 2) { 回傳 n; } 回傳 fib(n - 1) + fib(n - 2); };\n" +
            "印出(fib(20), [1, 2.5, \"星泉\"], {\"a\": 1}, 5000000000L * 3L);\n印出(是iOS(), 是MacOS());";
        string wasm = Compile(t, Source), app = t.FilePath("Run.app");
        const string Id = "org.aquariuslang.tests.run";
        new IosTarget().Build(new WasmBuildRequest(wasm, app, Options: new Dictionary<string, string>(Simulator) { [IosTarget.BundleIdOption] = Id }));
        Assert.Equal(0, Tool("/usr/bin/xcrun", "simctl", "install", udid, app).ExitCode);
        try {
            var run = Tool("/usr/bin/xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", udid, Id);
            string output = string.Join("\n", run.Output.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith(Id + ":")));
            Assert.Equal("6765 [1, 2.5, 星泉] {a: 1} 15000000000\n真 假\n", output);
        } finally { Tool("/usr/bin/xcrun", "simctl", "uninstall", udid, Id); }
    }
}
