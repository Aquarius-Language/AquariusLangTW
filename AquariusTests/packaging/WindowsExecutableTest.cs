using System.Diagnostics;
using System.Text;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public sealed class WindowsExecutableFactAttribute : FactAttribute {
    public WindowsExecutableFactAttribute() {
        if (!OperatingSystem.IsWindows()) Skip = "Windows executable tests require Windows.";
        else if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "build-targets", "win-x64", "host.exe")))
            Skip = "Run scripts/publish-apphost.ps1, then rebuild the tests to install the runtime pack.";
    }
}

public class WindowsExecutableTest {
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

    [WindowsExecutableFact] public void RelocatedSingleExeImportsAssetsAndSelectedEntryWithoutSourcesWasmOrDotnet() {
        using var t = new ApplicationTestDirectory();
        t.Write("source/main.aqua", "不存在;");
        t.Write("source/lib/工具.aqua", "變數 值=42;");
        t.Write("source/other.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 變數 g=匯入(\"GL\"); [m.值,g.ReadText(目前工作目錄+\"/assets/文字.txt\")];");
        t.Write("source/assets/文字.txt", "封裝成功");
        var compile = UnifiedCliSmokeTest.Run(t.Path, "build", "source/main.aqua", "source/lib/工具.aqua", "source/other.aqua", "--root", "source", "--assets", "source/assets", "-o", "app.wasm");
        Assert.True(compile.Code == 0, compile.Error);
        var build = UnifiedCliSmokeTest.Run(t.Path, "build", "app.wasm", "--target", "windows", "-o", "app.exe", "--entry", "OTHER.AQUA");
        Assert.True(build.Code == 0, build.Output + build.Error); Assert.DoesNotContain("封裝成功", build.Output);
        Directory.Delete(t.FilePath("source"), true); File.Delete(t.FilePath("app.wasm"));
        Directory.CreateDirectory(t.FilePath("搬移 folder"));
        string executable = t.FilePath("搬移 folder/星泉 app.exe"); File.Move(t.FilePath("app.exe"), executable);
        var run = Run(executable, t.Path);
        Assert.True(run.Code == 0, run.Output + run.Error); Assert.Equal("[42, 封裝成功]\n", run.Output); Assert.Empty(run.Error);
        Assert.Single(Directory.GetFiles(t.FilePath("搬移 folder"))); Assert.Empty(Directory.GetDirectories(t.FilePath("搬移 folder")));
    }

    [WindowsExecutableFact] public void SingleExeLoadsNativeGraphicsPhysicsAndImageCodecsAndAcceptsLaunchFiles() {
        using var t = new ApplicationTestDirectory();
        t.Write("main.aqua", """
            變數 glfw=匯入("GLFW"); glfw.Init(); glfw.Terminate();
            變數 images=匯入("Images"); 變數 image=images.Create(1,1,[255,0,0,255]);
            變數 encoded=images.Encode(image,"Png",{}); 變數 decoded=images.Decode(encoded,0);
            變數 j=匯入("Jolt"); 變數 w=j.CreateWorld([0,-10,0]); w.Dispose();
            變數 app=匯入("Application");
            變數 launchFiles=app.LaunchFiles(); 變數 launched=launchFiles[0];
            [decoded.width,launched.name];
            """);
        var compile = UnifiedCliSmokeTest.Run(t.Path, "build", "main.aqua", "-o", "app.wasm");
        Assert.True(compile.Code == 0, compile.Output + compile.Error);
        var build = UnifiedCliSmokeTest.Run(t.Path, "build", "app.wasm", "--target", "windows", "-o", "app.exe");
        Assert.True(build.Code == 0, build.Output + build.Error);
        File.Delete(t.FilePath("main.aqua")); File.Delete(t.FilePath("app.wasm"));
        string launch = t.Write("啟動 file.txt", "input");
        var run = Run(t.FilePath("app.exe"), t.Path, launch);
        Assert.True(run.Code == 0, run.Output + run.Error); Assert.Empty(run.Error); Assert.Equal("[1, 啟動 file.txt]\n", run.Output);
    }

    [WindowsExecutableFact] public void RuntimeErrorsAndDamagedPayloadsReturnOneWithoutStackTraces() {
        using var t = new ApplicationTestDirectory(); t.Write("main.aqua", "不存在;");
        Assert.Equal(0, UnifiedCliSmokeTest.Run(t.Path, "build", "main.aqua").Code);
        Assert.Equal(0, UnifiedCliSmokeTest.Run(t.Path, "build", "main.wasm", "--target", "windows", "-o", "app.exe").Code);
        var run = Run(t.FilePath("app.exe"), t.Path); Assert.Equal(1, run.Code); Assert.Contains("Identifier not found", run.Output);
        // Corrupt a copy: Windows scanners may briefly retain a handle to the executable just run.
        File.Copy(t.FilePath("app.exe"), t.FilePath("damaged.exe"));
        using (var file = new FileStream(t.FilePath("damaged.exe"), FileMode.Open, FileAccess.ReadWrite)) {
            file.Position = file.Length - 64; file.WriteByte(99);
        }
        run = Run(t.FilePath("damaged.exe"), t.Path); Assert.Equal(1, run.Code); Assert.Contains("Unsupported Aquarius executable", run.Error); Assert.DoesNotContain("Unhandled exception", run.Error);
    }
}
