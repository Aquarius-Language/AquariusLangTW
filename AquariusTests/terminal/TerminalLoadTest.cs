using AquariusLang.Desktop.Terminal;
using AquariusLang.Terminal;

namespace AquariusTests.Terminal;

/// <summary>Saturates the process-wide thread pool, so it runs alone.</summary>
[CollectionDefinition("Thread pool saturation", DisableParallelization = true)]
public sealed class ThreadPoolSaturationCollection { }

[Collection("Thread pool saturation")]
public class TerminalLoadTest {
    // A full test run keeps every thread-pool worker blocked for seconds at a time; the pool only adds workers slowly.
    // Output collection must not wait behind that work, or a killed command reports "Child output streams did not close".
    [Fact] public void OutputCollectionDoesNotWaitForTheThreadPool() {
        ThreadPool.GetMinThreads(out int workers, out _);
        // Not disposed: queued items may start only after the test ends, and must then pass straight through.
        var release = new ManualResetEventSlim();
        for (int i = 0; i < workers + 64; i++) ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(), null);
        try {
            var backend = new DesktopTerminalBackend();
            var request = OperatingSystem.IsWindows() ? new TerminalRequest("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep 10"], TimeoutMs: 200) : new TerminalRequest("/bin/sh", ["-c", "sleep 10"], TimeoutMs: 200);
            var result = backend.Run(request);
            Assert.True(result.TimedOut); Assert.Equal(-1, result.ExitCode);
            request = OperatingSystem.IsWindows()
                ? new TerminalRequest("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('hello');[Console]::Error.Write('error');exit 7"])
                : new TerminalRequest("/bin/sh", ["-c", "printf hello; printf error >&2; exit 7"]);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            result = backend.Run(request);
            Assert.Equal((7, "hello", "error", false), (result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Collecting a finished command's output took {clock.Elapsed}.");
        } finally { release.Set(); }
    }
}
