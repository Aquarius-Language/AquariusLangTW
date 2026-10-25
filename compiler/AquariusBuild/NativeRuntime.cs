using System.Diagnostics;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Packaging;

namespace AquariusLang.Build;

/// <summary>
/// The runtime pack of the machine the compiler runs on (windows-x64 or linux-x64): it runs applications for
/// <c>aqua run</c>, the REPL and the tests, and describes its modules for editor tooling:
/// <code>aquarius [--library file ...] app.wasm [--entry script.aqua] [argument ...]
/// aquarius [--library file ...] --catalog</code>
/// </summary>
public static class NativeRuntime
{
    public static string Launcher()
    {
        string target = AquariusInstallation.HostTarget;
        string launcher = OperatingSystem.IsWindows() ? "aquarius.exe" : "aquarius";
        return Path.Combine(AquariusInstallation.Require().RequireRuntimePack(target, launcher), launcher);
    }

    /// <summary>The external libraries' native binaries an application needs on this machine, for --library. Default
    /// libraries are part of the runtime; a library the compiler does not know is left to the runtime's own search
    /// (a libraries/ folder beside the application).</summary>
    public static IReadOnlyList<string> LibraryFiles(WasmApplication application, LibrarySet? libraries)
    {
        if (libraries == null) return [];
        string target = AquariusInstallation.HostTarget;
        var files = new List<string>();
        foreach (string name in application.NativeLibraries) {
            var library = libraries.Libraries.FirstOrDefault(l => l.NativeName == name);
            if (library is not { External: true }) continue;
            files.Add(library.NativeBinary(target) ?? throw new IOException(
                $"The library {library.Name} has no {target} binary (native/{target}/{Library.NativeFile(name, target)} in {library.Root}). Build it with 'aqua library build {library.Root} --targets {target}'."));
        }
        return files;
    }

    public static ProcessStartInfo StartInfo(string wasm, string? entry = null, IEnumerable<string>? arguments = null, IEnumerable<string>? libraryFiles = null)
    {
        var start = new ProcessStartInfo(Launcher()) { UseShellExecute = false };
        foreach (string file in libraryFiles ?? []) { start.ArgumentList.Add("--library"); start.ArgumentList.Add(Path.GetFullPath(file)); }
        start.ArgumentList.Add(Path.GetFullPath(wasm));
        if (entry != null) { start.ArgumentList.Add("--entry"); start.ArgumentList.Add(entry); }
        foreach (string argument in arguments ?? []) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>When set (tests, embedding hosts), <see cref="Run"/> passes the application's output through
    /// <see cref="Console.Out"/> and <see cref="Console.Error"/> instead of letting it inherit this process's streams.</summary>
    public static bool CaptureOutput { get; set; }

    /// <summary>Runs an application with this process's standard streams and returns its exit code.</summary>
    public static int Run(string wasm, string? entry = null, IEnumerable<string>? arguments = null, IEnumerable<string>? libraryFiles = null)
    {
        var start = StartInfo(wasm, entry, arguments, libraryFiles);
        if (CaptureOutput) {
            start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the AquariusLang runtime.");
        if (CaptureOutput) {
            var error = process.StandardError.ReadToEndAsync();
            Console.Out.Write(process.StandardOutput.ReadToEnd().Replace("\r\n", "\n"));
            Console.Error.Write(error.GetAwaiter().GetResult().Replace("\r\n", "\n"));
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The JSON catalog of every module the runtime (plus the given external libraries) registers.</summary>
    public static string Catalog(IEnumerable<string>? libraryFiles = null)
    {
        var start = new ProcessStartInfo(Launcher()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string file in libraryFiles ?? []) { start.ArgumentList.Add("--library"); start.ArgumentList.Add(Path.GetFullPath(file)); }
        start.ArgumentList.Add("--catalog");
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the AquariusLang runtime.");
        var error = process.StandardError.ReadToEndAsync();
        string json = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new IOException("aquarius --catalog timed out."); }
        if (process.ExitCode != 0) throw new IOException("aquarius --catalog failed: " + error.GetAwaiter().GetResult().Trim());
        return json;
    }
}
