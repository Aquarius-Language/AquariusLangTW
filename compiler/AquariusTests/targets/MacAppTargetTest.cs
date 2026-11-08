using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Packaging;
using AquariusTests.Packaging;

namespace AquariusTests.Targets;

/// <summary>The macos target (<see cref="ExecutableTarget"/> for macos-arm64): a .app bundle whose launcher stays the
/// runtime pack's signed, unmodified one, with the application's overlay alone in Contents/Resources, so the bundle
/// passes <c>codesign --verify --strict</c> (an overlay appended to a Mach-O breaks its signature).</summary>
[Collection(ExecutableCollection.Name)]
public class MacAppTargetTest
{
    private static ExecutableTarget Mac() => new("macos", "macos-arm64");
    private static string Compile(TestFolder t, string source)
    {
        string wasm = t.FilePath("app.wasm");
        WasmApplication.Compile([t.Write("main.aqua", source)], wasm, t.Path);
        return wasm;
    }
    private static RunResult Launch(string app, params string[] arguments)
    {
        string executable = Path.Combine(app, "Contents", "MacOS", Path.GetFileNameWithoutExtension(app));
        return Processes.Run(executable, Path.GetDirectoryName(app)!, arguments, environment: new Dictionary<string, string> {
            ["DOTNET_ROOT"] = Path.Combine(Path.GetDirectoryName(app)!, "no-dotnet"), ["PATH"] = "/usr/bin:/bin",
        });
    }
    private static RunResult Verify(string app) =>
        Processes.Run("/usr/bin/codesign", Path.GetDirectoryName(app)!, ["--verify", "--deep", "--strict", "--verbose=2", app]);

    [Fact]
    public void ThePayloadAloneLoadsLikeAnAppendedOverlay()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "印出(1);");
        using var payload = new MemoryStream();
        using (var input = File.OpenRead(wasm)) ExecutableBundle.WritePayload(input, payload, "main.aqua");
        Assert.True(ExecutableBundle.HasProgram(payload));
        Assert.Equal("main.aqua", ExecutableBundle.Load(payload).EntryPoint);
        // The same bytes follow a launcher in an appended executable.
        using var appended = new MemoryStream();
        using (var input = File.OpenRead(wasm)) ExecutableBundle.Write(new MemoryStream([1, 2, 3]), input, appended, "main.aqua");
        Assert.Equal(payload.ToArray(), appended.ToArray()[3..]);
    }

    [Fact]
    public void InfoPlistNamesTheLauncherAndEscapesTheName()
    {
        string plist = ExecutableTarget.InfoPlist("星泉 & <Co>");
        Assert.Contains("<key>CFBundleExecutable</key><string>星泉 &amp; &lt;Co&gt;</string>", plist);
        Assert.Contains("<key>CFBundleIdentifier</key><string>org.aquariuslang.app.------Co-</string>", plist);
        Assert.Contains("<key>CFBundlePackageType</key><string>APPL</string>", plist);
    }

    [Fact]
    public void MacOutputsMustBeApps()
    {
        using var t = new TestFolder();
        var error = Assert.Throws<ArgumentException>(() => Mac().Build(new WasmBuildRequest(t.FilePath("app.wasm"), t.FilePath("dist/app"))));
        Assert.Contains("*.app", error.Message);
    }

    [MacAppFact]
    public void AppsAreSignedRunWithoutTheCompilerAndRebuildInPlace()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "印出(\"星泉 on\", 是MacOS());");
        string app = t.FilePath("dist/星泉 App.app");
        Mac().Build(new WasmBuildRequest(wasm, app));
        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "星泉 App")));
        Assert.True(File.Exists(Path.Combine(app, ExecutableTarget.AppPayload)));
        Assert.Contains("<string>星泉 App</string>", File.ReadAllText(Path.Combine(app, "Contents", "Info.plist")));
        var verified = Verify(app);
        Assert.True(verified.ExitCode == 0, verified.Output + verified.Error);
        File.Delete(wasm);
        var run = Launch(app);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Equal("星泉 on 真\n", run.Output);

        // Rebuilding replaces the bundle; a folder this target did not write is refused.
        wasm = Compile(t, "印出(2);");
        Mac().Build(new WasmBuildRequest(wasm, app));
        Assert.Equal("2\n", Launch(app).Output);
        Assert.Equal(0, Verify(app).ExitCode);
        string foreign = t.FilePath("foreign.app");
        t.Write("foreign.app/keep.txt", "mine");
        Assert.Throws<ArgumentException>(() => Mac().Build(new WasmBuildRequest(wasm, foreign)));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
    }

    [MacAppFact]
    public void OptimizedAppsMatchTheRegularOnesAndStaySigned()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, "變數 fib = 函式(n: 整數): 整數 { 如果 (n < 2) { 回傳 n; } 回傳 fib(n - 1) + fib(n - 2); };\n印出(fib(22), [1, 2], \"文字\");");
        string regular = t.FilePath("regular/App.app"), optimized = t.FilePath("optimized/App.app");
        Mac().Build(new WasmBuildRequest(wasm, regular));
        Mac().Build(new WasmBuildRequest(wasm, optimized, Options: new Dictionary<string, string> { [ExecutableTarget.OptimizeOption] = "true" }));
        Assert.True(File.Exists(Path.Combine(optimized, "Contents", "MacOS", "App.aot.dylib")));
        var expected = Launch(regular);
        var run = Launch(optimized);
        Assert.Equal((0, "17711 [1, 2] 文字\n"), (expected.ExitCode, expected.Output));
        Assert.Equal((expected.ExitCode, expected.Output), (run.ExitCode, run.Output));
        var verified = Verify(optimized);
        Assert.True(verified.ExitCode == 0, verified.Output + verified.Error);
    }

    [MacAppFact]
    public void ADamagedPayloadFailsTheSignatureAndTheLauncherRefusesIt()
    {
        using var t = new TestFolder();
        string app = t.FilePath("App.app");
        Mac().Build(new WasmBuildRequest(Compile(t, "印出(1);"), app));
        string payload = Path.Combine(app, ExecutableTarget.AppPayload);
        using (var file = new FileStream(payload, FileMode.Open, FileAccess.ReadWrite)) { file.Position = file.Length - 100; file.WriteByte(0x5a); }
        Assert.NotEqual(0, Verify(app).ExitCode);
        var run = Launch(app);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("damaged", run.Error);
        Assert.Equal("", run.Output);
    }
}
