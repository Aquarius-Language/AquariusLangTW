using System.Diagnostics;
using System.Text;
using AquariusLang.Build;
using AquariusLang.Cli;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusTests.Packaging;

/// <summary>A fresh folder for one test's files, removed afterwards.</summary>
internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = WasmTestHost.Directory();
    public string Write(string relative, string text)
    {
        string file = FilePath(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }
    public string Write(string relative, byte[] bytes)
    {
        string file = FilePath(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
        return file;
    }
    public string FilePath(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public void Dispose()
    {
        // Windows scanners may briefly hold a just-run executable; a leftover temporary folder is harmless.
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Tests that replace the process's console streams or current directory (the in-process <c>aqua</c> command)
/// run one at a time, never beside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection { public const string Name = "Aquarius command line"; }

internal sealed record CliResult(int Code, string Output, string Error);

/// <summary>Runs the <c>aqua</c> command in this process (<see cref="CompilerCommandLine.Run"/>) with its working directory,
/// standard input and output redirected; <c>aqua run</c> output comes through <see cref="NativeRuntime.CaptureOutput"/>.
/// Only for tests in <see cref="ConsoleCollection"/>.</summary>
internal static class AquaCommand
{
    private static readonly object gate = new();
    public static CliResult Run(string directory, params string[] args) => RunWithInput(directory, "", args);
    public static CliResult RunWithInput(string directory, string input, params string[] args)
    {
        lock (gate) {
            TextWriter output = Console.Out, error = Console.Error;
            TextReader reader = Console.In;
            string current = Environment.CurrentDirectory;
            bool capture = NativeRuntime.CaptureOutput;
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            try {
                Console.SetOut(stdout); Console.SetError(stderr); Console.SetIn(new StringReader(input));
                Environment.CurrentDirectory = directory;
                NativeRuntime.CaptureOutput = true;
                int code = CompilerCommandLine.Run(args);
                return new CliResult(code, Lf(stdout.ToString()), Lf(stderr.ToString()));
            } finally {
                Console.SetOut(output); Console.SetError(error); Console.SetIn(reader);
                Environment.CurrentDirectory = current;
                NativeRuntime.CaptureOutput = capture;
            }
        }
    }
    private static string Lf(string text) => text.Replace("\r\n", "\n");
}

/// <summary>Runs a program file (an exported executable, a build tool) and collects its output.</summary>
internal static class Processes
{
    public static RunResult Run(string program, string directory, IEnumerable<string>? arguments = null, int timeoutMs = 120_000,
        IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        var start = new ProcessStartInfo(program) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (input != null) start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string argument in arguments ?? []) start.ArgumentList.Add(argument);
        foreach (var pair in environment ?? new Dictionary<string, string>()) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (input != null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit(timeoutMs)) { process.Kill(true); throw new TimeoutException($"{program} did not finish in {timeoutMs} ms."); }
        return new RunResult(process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult().Replace("\r\n", "\n"));
    }

    /// <summary>The first of these programs found on PATH, or null.</summary>
    public static string? OnPath(params string[] names)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string name in names) {
                string candidate = System.IO.Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}

/// <summary>The fixture libraries of these tests (packaging/fixtures), and external libraries made on the fly.</summary>
internal static class Fixtures
{
    public static string Root => System.IO.Path.Combine(AquariusInstallation.Require().Root, "compiler", "AquariusTests", "packaging", "fixtures");
    /// <summary>A manifest_version 2 library of Aquarius-source modules (stars, comets, desktop_only).</summary>
    public static Library Library(string name) => AquariusLang.Libraries.Library.Load(System.IO.Path.Combine(Root, name));
    /// <summary>The fixture libraries alone (no default libraries).</summary>
    public static LibrarySet Set(params string[] names) => new(names.Select(Library));
    /// <summary>The default libraries plus fixture libraries.</summary>
    public static LibrarySet WithDefaults(params Library[] libraries) => LibrarySet.Default.With(libraries);

    /// <summary>Writes an external library (a Bottle package, manifest_version 2) named nebula: a native part registering
    /// NebulaNative|星雲原生 with dummy prebuilt binaries for the given targets, an Aquarius-source module Nebula|星雲 that
    /// uses it, and an Android permission. The binaries are never run.</summary>
    public static Library ExternalLibrary(TestFolder folder, params string[] targets)
    {
        folder.Write("nebula/aqua.toml", """
            manifest_version = 2

            [library]
            name = "nebula"
            version = "1.2.0"
            description = "External library fixture"
            license = "MIT"
            platforms = ["windows", "linux", "android", "web"]

            [native]
            name = "nebula"
            modules = ["NebulaNative|星雲原生"]
            source = "native"

            [[modules]]
            names = "Nebula|星雲"
            source = "aqua/Nebula.aqua"

            [android]
            permissions = ["android.permission.RECORD_AUDIO"]
            """);
        folder.Write("nebula/aqua/Nebula.aqua", "變數 原生 = 匯入(\"NebulaNative\");\n變數 名稱 = \"星雲\";\n");
        foreach (string target in targets)
            folder.Write($"nebula/native/{target}/{AquariusLang.Libraries.Library.NativeFile("nebula", target)}", "dummy " + target + " binary of nebula");
        var library = AquariusLang.Bottle.ProjectManifest.Load(folder.FilePath("nebula")).Library!;
        Assert.True(library.External);
        return library;
    }
}

/// <summary>A fact that needs the host runtime pack and a host that builds desktop executables (Windows or Linux x64).</summary>
public sealed class ExecutableFactAttribute : FactAttribute
{
    public ExecutableFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Skip = "Executables are built on Windows or Linux x64.";
        else if (!WasmTestHost.Available) Skip = WasmTestHost.MissingRuntime;
    }
}

/// <summary>A theory that needs the host runtime pack (see <see cref="ExecutableFactAttribute"/>).</summary>
public sealed class ExecutableTheoryAttribute : TheoryAttribute
{
    public ExecutableTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Skip = "Executables are built on Windows or Linux x64.";
        else if (!WasmTestHost.Available) Skip = WasmTestHost.MissingRuntime;
    }
}

/// <summary>A fact that builds websites: skipped when the web runtime pack or the web glue is missing.</summary>
public sealed class WebFactAttribute : FactAttribute
{
    public static string? Missing()
    {
        var installation = AquariusInstallation.Current;
        if (installation == null) return "Needs an AquariusLang installation (AQUARIUS_HOME).";
        if (!File.Exists(Path.Combine(installation.RuntimePack("web"), "aquarius.wasm")) || !File.Exists(Path.Combine(installation.RuntimePack("web"), "libc.so")))
            return "Needs the web runtime pack (scripts/build-runtime.ps1 -Targets web).";
        if (!File.Exists(Path.Combine(installation.WebGlue, "index.mjs"))) return "Needs web-glue/.";
        return null;
    }
    public WebFactAttribute() { if (Missing() is { } reason) Skip = reason; }
    /// <summary>Whether the web runtime pack has a default library's side module (packs may hold only some libraries).</summary>
    public static bool HasSideModule(string library) =>
        AquariusInstallation.Current is { } installation && File.Exists(Path.Combine(installation.RuntimePack("web"), "libraries", library + ".wasm"));
}
