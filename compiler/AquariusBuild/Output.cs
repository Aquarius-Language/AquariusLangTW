using System.Diagnostics;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Packaging;

namespace AquariusLang.Build;

/// <summary>Shared steps of the build targets: reading the input application, finding the libraries it needs and
/// replacing outputs only once a build has completed.</summary>
internal static class Output
{
    public static string FullPath(string path) => Path.GetFullPath(path);

    /// <summary>The application to export, checked for the target platform and the selected entry.</summary>
    public static (WasmApplication Application, string Entry) Read(WasmBuildRequest request, string platform)
    {
        string input = Path.GetFullPath(request.Input);
        if (!input.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Builds for a platform need one .wasm application.");
        if (input.Equals(Path.GetFullPath(request.Output), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output must differ from the input .wasm.");
        var application = WasmApplication.Load(input);
        application.CheckPlatform(platform);
        return (application, application.ResolveScript(request.EntryPoint ?? application.EntryPoint));
    }

    /// <summary>The libraries an application's metadata lists, each with its manifest when the compiler knows it.</summary>
    public static IReadOnlyList<(string Name, Library? Library)> NeededLibraries(WasmApplication application, WasmBuildRequest request)
    {
        var libraries = request.Libraries ?? LibrarySet.Default;
        return application.NativeLibraries.Select(name => (name, libraries.Libraries.FirstOrDefault(l => l.NativeName == name))).ToArray();
    }

    /// <summary>The native binary of an external library for a target. Default libraries are part of the runtime pack.</summary>
    public static string? ExternalBinary(string name, Library? library, string target)
    {
        if (library is not { External: true }) return null;
        return library.NativeBinary(target) ?? throw new IOException(
            $"The library {library.Name} has no {target} binary (expected native/{target}/{Library.NativeFile(name, target)} in {library.Root}). Build it with 'aqua library build {library.Root} --targets {target}'.");
    }

    /// <summary>Builds a folder output in a sibling staging folder, then swaps it in. A folder that exists must have
    /// been written by this target before (it holds <paramref name="marker"/>) or be empty.</summary>
    public static void ReplaceDirectory(string output, string marker, Action<string> build)
    {
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("The output must be a named folder.");
        if (File.Exists(output)) throw new ArgumentException($"The output must be a folder: {output}");
        if (Directory.Exists(output)) {
            if ((File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("The output folder must not be a link.");
            if (Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(Path.Combine(output, marker)))
                throw new ArgumentException($"The output folder {output} holds other files. Choose an empty or new folder.");
        }
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".aquarius-stage-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".aquarius-backup-" + Guid.NewGuid().ToString("N"));
        bool committed = false;
        try {
            Directory.CreateDirectory(stage);
            build(stage);
            File.WriteAllText(Path.Combine(stage, marker), "Written by the AquariusLang compiler; rebuilding replaces this folder.\n");
            if (Directory.Exists(output)) AquariusLang.FileSystem.TransientFileLocks.Retry(() => Directory.Move(output, backup), accessDenied: true);
            try { AquariusLang.FileSystem.TransientFileLocks.Retry(() => Directory.Move(stage, output), accessDenied: true); committed = true; }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, output); throw; }
        } finally {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (committed && Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    /// <summary>Writes a file output through a sibling staging file.</summary>
    public static void ReplaceFile(string output, Action<string> write)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("The output must be a regular file, not a folder or link.");
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try {
            write(stage);
            AquariusLang.FileSystem.TransientFileLocks.Retry(() => File.Move(stage, output, overwrite: true));
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>Runs a build tool and fails with its output when it fails.</summary>
    public static string Run(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        bool batch = OperatingSystem.IsWindows() && program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(batch ? "cmd.exe" : program) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        if (batch) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(program); }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment ?? new Dictionary<string, string>()) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {program}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000)) { process.Kill(entireProcessTree: true); throw new IOException($"{Path.GetFileName(program)} timed out."); }
        string text = (output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult()).Trim();
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(program)} failed (exit {process.ExitCode}).\n{text}");
        return text;
    }
}
