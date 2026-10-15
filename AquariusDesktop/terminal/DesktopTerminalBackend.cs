using System.Diagnostics;
using System.Text;
using AquariusLang.Terminal;

namespace AquariusLang.Desktop.Terminal;

public sealed class DesktopTerminalBackend : ITerminalBackend {
    public string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsFreeBSD() ? "freebsd" : "linux";
    public bool IsSupported => true;
    public TerminalResult Run(TerminalRequest request) {
        if (request.TimeoutMs is < 1 or > 3600000) throw new ArgumentOutOfRangeException(nameof(request.TimeoutMs));
        var info = new ProcessStartInfo(request.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (request.WorkingDirectory != null) info.WorkingDirectory = request.WorkingDirectory;
        foreach (string arg in request.Arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch process.");
        process.StandardInput.Close();
        // Drain both streams concurrently even after the retained prefix fills, avoiding pipe deadlocks.
        static async Task<string> Capture(StreamReader reader) {
            var text = new StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = await reader.ReadAsync(buffer)) != 0) {
                int retained = Math.Min(count, TerminalRuntime.MaxOutputCharacters - text.Length);
                if (retained > 0) text.Append(buffer, 0, retained);
            }
            return text.ToString();
        }
        var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
        bool timedOut = !process.WaitForExit(request.TimeoutMs);
        try {
            if (timedOut) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } process.WaitForExit(); }
            // Descendants can inherit pipes after their parent exits. Bound collection as well.
            if (!Task.WhenAll(stdout, stderr).Wait(Math.Max(1000, request.TimeoutMs))) throw new TimeoutException("Child output streams did not close.");
            return new(timedOut ? -1 : process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), timedOut);
        } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
