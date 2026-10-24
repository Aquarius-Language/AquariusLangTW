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
        // Drain both streams on dedicated threads, even after the retained prefix fills (avoiding pipe deadlocks).
        // Thread-pool continuations would queue behind unrelated blocked work in a busy process and miss the bound below.
        static (Thread Thread, StringBuilder Text, Exception?[] Error) Capture(StreamReader reader) {
            var text = new StringBuilder(); var error = new Exception?[1];
            var thread = new Thread(() => {
                try {
                    var buffer = new char[8192]; int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) != 0) {
                        int retained = Math.Min(count, TerminalRuntime.MaxOutputCharacters - text.Length);
                        if (retained > 0) text.Append(buffer, 0, retained);
                    }
                } catch (Exception e) when (e is IOException or ObjectDisposedException) { error[0] = e; }
            }) { IsBackground = true, Name = "Terminal output" };
            thread.Start(); return (thread, text, error);
        }
        var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
        bool timedOut = !process.WaitForExit(request.TimeoutMs);
        try {
            if (timedOut) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } process.WaitForExit(); }
            // Descendants can inherit pipes after their parent exits. Bound collection as well.
            var bound = Stopwatch.StartNew(); int limit = Math.Max(1000, request.TimeoutMs);
            if (!stdout.Thread.Join(limit) || !stderr.Thread.Join(Math.Max(0, limit - (int)bound.ElapsedMilliseconds)))
                throw new TimeoutException("Child output streams did not close.");
            if ((stdout.Error[0] ?? stderr.Error[0]) is Exception failure) throw new IOException("Could not read the command's output.", failure);
            return new(timedOut ? -1 : process.ExitCode, stdout.Text.ToString(), stderr.Text.ToString(), timedOut);
        } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
