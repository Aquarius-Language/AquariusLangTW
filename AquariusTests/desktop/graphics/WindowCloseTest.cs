using System.Diagnostics;
using System.Text;
using AquariusLang.Desktop.runtime;

namespace AquariusLang.Desktop.Graphics;

public sealed class WindowsWgpuTheoryAttribute : TheoryAttribute {
    public WindowsWgpuTheoryAttribute() {
        if (!OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable("AQUARIUS_WGPU_TESTS") != "1")
            Skip = "Set AQUARIUS_WGPU_TESTS=1 on Windows with a wgpu adapter and GLFW bridge.";
    }
}

public class WindowCloseTest {
    [WindowsWgpuTheory]
    [InlineData("Accept")]
    [InlineData("Cancel")]
    [InlineData("Defer")]
    public async Task IdleSketchProcessesCloseRequestsAndExitsAfterAcceptance(string decision) {
        using var directory = new ApplicationTestDirectory();
        directory.Write("main.aqua", $$"""
            變數 p=匯入("Processing");變數 windows=匯入("Window");變數 window=0;變數 requests=0;
            p.run(函式(){p.size(64,64);window=windows.Current();p.noLoop();},函式(){
                變數 events=window.Poll();
                迴圈(變數 i=0;i<長度(events);i++) {
                    如果(events[i].type=="closeRequested") {
                        requests++;
                        如果(requests==1){window.ResolveClose("{{decision}}");}
                        否則{window.ResolveClose("Accept");}
                        如果(requests==1){印出("CLOSE-1");}否則{印出("CLOSE-2");}
                    }
                }
                p.background(0);
                如果(p.frameCount==1){印出("READY");}
            });
            印出("FINISHED");
            """);
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(ScriptRunner).Assembly.Location);
        start.ArgumentList.Add(directory.FilePath("main.aqua"));
        start.Environment.Remove("AQUARIUS_GRAPHICS_FRAMES");
        start.Environment.Remove("AQUARIUS_GRAPHICS_CAPTURE");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        try {
            await ReadUntil(process, "READY");
            // Let the first frame finish so the close arrives while noLoop is idle.
            await Task.Delay(150);
            Assert.True(process.CloseMainWindow());
            await ReadUntil(process, "CLOSE-1");
            if (decision != "Accept") {
                await Task.Delay(150);
                Assert.False(process.HasExited);
                Assert.True(process.CloseMainWindow());
                await ReadUntil(process, "CLOSE-2");
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("FINISHED", await process.StandardOutput.ReadToEndAsync());
            Assert.Equal("", await errors);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task ReadUntil(Process process, string expected) {
        var received = new StringBuilder();
        while (true) {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(line != null, $"Application exited before {expected}. Output: {received}");
            received.AppendLine(line);
            if (line == expected) return;
        }
    }
}
