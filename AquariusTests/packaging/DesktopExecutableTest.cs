using System.Diagnostics;
using System.Text;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public sealed class DesktopExecutableFactAttribute : FactAttribute {
    public DesktopExecutableFactAttribute() {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Skip = "Executable tests require Windows or Linux x64.";
        else if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "build-targets", OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", OperatingSystem.IsWindows() ? "host.exe" : "host")))
            Skip = "Run scripts/publish-apphost.ps1 (Windows) or scripts/publish-apphost.sh (Linux), then rebuild the tests to install the runtime pack.";
    }
}

// Both platforms hash and copy the full self-contained runtime. Keep this disk
// workload separate from the other process and timing-sensitive tests.
[CollectionDefinition("Desktop executables", DisableParallelization = true)]
public sealed class DesktopExecutableCollection { }

[Collection("Desktop executables")]
public class DesktopExecutableTest {
    private static string Target => OperatingSystem.IsWindows() ? "windows" : "linux";
    private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";
    private static (int Code, string Output, string Error) Run(string executable, string directory, params string[] args) {
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // The relocated artifact is the only file in its directory. External .NET roots and PATH are unavailable.
        start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "no-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["PATH"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Standalone application did not finish."); }
        return (process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult());
    }

    [DesktopExecutableFact] public void RelocatedSingleExeImportsAssetsAndSelectedEntryWithoutSourcesWasmOrDotnet() {
        using var t = new ApplicationTestDirectory();
        t.Write("source/main.aqua", "不存在;");
        t.Write("source/lib/工具.aqua", "變數 值=42;");
        t.Write("source/other.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 變數 g=匯入(\"GL\"); [m.值,g.ReadText(目前工作目錄+\"/assets/文字.txt\")];");
        t.Write("source/assets/文字.txt", "封裝成功");
        var compile = UnifiedCliSmokeTest.Run(t.Path, "build", "source/main.aqua", "source/lib/工具.aqua", "source/other.aqua", "--root", "source", "--assets", "source/assets", "-o", "app.wasm");
        Assert.True(compile.Code == 0, compile.Error);
        var build = UnifiedCliSmokeTest.Run(t.Path, "build", "app.wasm", "--target", Target, "-o", "app" + Extension, "--entry", "OTHER.AQUA");
        Assert.True(build.Code == 0, build.Output + build.Error); Assert.DoesNotContain("封裝成功", build.Output);
        Directory.Delete(t.FilePath("source"), true); File.Delete(t.FilePath("app.wasm"));
        Directory.CreateDirectory(t.FilePath("搬移 folder"));
        string executable = t.FilePath("搬移 folder/星泉 app" + Extension); File.Move(t.FilePath("app" + Extension), executable);
        var run = Run(executable, t.Path);
        Assert.True(run.Code == 0, run.Output + run.Error); Assert.Equal("[42, 封裝成功]\n", run.Output); Assert.Empty(run.Error);
        Assert.Single(Directory.GetFiles(t.FilePath("搬移 folder"))); Assert.Empty(Directory.GetDirectories(t.FilePath("搬移 folder")));
    }

    [DesktopExecutableFact] public void SingleExeLoadsNativeGraphicsImageCodecsAndThirdPartyModuleAndAcceptsLaunchFiles() {
        using var t = new ApplicationTestDirectory();
        t.Write("main.aqua", """
            變數 glfw=匯入("GLFW"); glfw.Init(); glfw.Terminate();
            變數 images=匯入("Images"); 變數 image=images.Create(1,1,[255,0,0,255]);
            變數 encoded=images.Encode(image,"Png",{}); 變數 decoded=images.Decode(encoded,0);
            變數 dynamic=匯入("DynamicLibrary");
            變數 library=dynamic.Load(目前工作目錄+"/plugin.mjs");
            變數 message=library.Call("echo","封裝模組"); library.Dispose();
            變數 app=匯入("Application");
            變數 launchFiles=app.LaunchFiles(); 變數 launched=launchFiles[0];
            [decoded.width,message,launched.name];
            """);
        t.Write("plugin.mjs", "export function aqua_library_abi(){return 1;}export function aqua_create_library(){return {echo:bytes=>bytes};}");
        var compile = UnifiedCliSmokeTest.Run(t.Path, "build", "main.aqua", "--assets", "plugin.mjs", "-o", "app.wasm");
        Assert.True(compile.Code == 0, compile.Output + compile.Error);
        var build = UnifiedCliSmokeTest.Run(t.Path, "build", "app.wasm", "--target", Target, "-o", "app" + Extension);
        Assert.True(build.Code == 0, build.Output + build.Error);
        File.Delete(t.FilePath("main.aqua")); File.Delete(t.FilePath("app.wasm")); File.Delete(t.FilePath("plugin.mjs"));
        string launch = t.Write("啟動 file.txt", "input");
        var run = Run(t.FilePath("app" + Extension), t.Path, launch);
        Assert.True(run.Code == 0, run.Output + run.Error); Assert.Empty(run.Error); Assert.Equal("[1, 封裝模組, 啟動 file.txt]\n", run.Output);
    }

    [DesktopExecutableFact] public void RuntimeErrorsAndDamagedPayloadsReturnOneWithoutStackTraces() {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "不存在;");
        Assert.Equal(0, UnifiedCliSmokeTest.Run(t.Path, "build", "main.aqua").Code);
        Assert.Equal(0, UnifiedCliSmokeTest.Run(t.Path, "build", "main.wasm", "--target", Target, "-o", "app" + Extension).Code);
        var run = Run(t.FilePath("app" + Extension), t.Path); Assert.Equal(1, run.Code); Assert.Contains("Identifier not found", run.Output);
        // Corrupt a copy: Windows scanners may briefly retain a handle to the executable just run.
        File.Copy(t.FilePath("app" + Extension), t.FilePath("damaged" + Extension));
        using (var file = new FileStream(t.FilePath("damaged" + Extension), FileMode.Open, FileAccess.ReadWrite)) {
            file.Position = file.Length - 64; file.WriteByte(99);
        }
        run = Run(t.FilePath("damaged" + Extension), t.Path); Assert.Equal(1, run.Code); Assert.Contains("Unsupported Aquarius executable", run.Error); Assert.DoesNotContain("Unhandled exception", run.Error);
    }
}
