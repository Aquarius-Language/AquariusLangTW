using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using AquariusLang.Cli;
using AquariusLang.Compiler;

namespace AquariusTests.Bottle;

/// <summary>A scratch folder for one Bottle test, removed afterwards.</summary>
internal sealed class BottleDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquarius-bottle-" + Guid.NewGuid().ToString("N"));
    public BottleDirectory() => Directory.CreateDirectory(Path);
    public string FilePath(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
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
    public void Dispose()
    {
        // A scanner may still hold a just-written file for a moment.
        for (int attempt = 0; attempt < 5; attempt++) {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}

/// <summary>What one run of the aqua command line printed and returned.</summary>
internal sealed record CliResult(int Code, string Output, string Error)
{
    public CliResult Succeeded() { Assert.True(Code == 0, $"exit {Code}\n{Output}\n{Error}"); return this; }
}

/// <summary>Runs the aqua command line (aqua.dll beside the tests) as its own process, with the Bottle cache in the
/// test folder's cache/ unless the environment says otherwise.</summary>
internal static class AquaCli
{
    public static CliResult Run(BottleDirectory directory, params string[] args) => RunIn(directory, null, null, args);
    public static CliResult Run(BottleDirectory directory, IReadOnlyDictionary<string, string>? environment, params string[] args) => RunIn(directory, environment, null, args);

    public static CliResult RunIn(BottleDirectory directory, IReadOnlyDictionary<string, string>? environment, string? workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = workingDirectory ?? directory.Path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["AQUA_BOTTLE_HOME"] = directory.FilePath("cache");
        start.ArgumentList.Add(typeof(CompilerCommandLine).Assembly.Location);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000)) { process.Kill(true); throw new TimeoutException("The aqua command line timed out: " + string.Join(' ', args)); }
        return new CliResult(process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult().Replace("\r\n", "\n"));
    }
}

/// <summary>Runtime packs made of placeholder files, selected with AQUARIUS_RUNTIME_PACKS, so export tests need no real
/// runtime build: the build targets only check and copy the pack's files.</summary>
internal static class SyntheticPacks
{
    /// <summary>A Windows console PE image to stand in for aquarius.exe (the test needs only its headers).</summary>
    public static byte[] PeTemplate => File.ReadAllBytes(typeof(CompilerCommandLine).Assembly.Location);

    public static Dictionary<string, string> Create(BottleDirectory t, string folder, params (string Target, string File, byte[] Bytes)[] files)
    {
        foreach (var (target, file, bytes) in files) t.Write($"{folder}/{target}/{file}", bytes);
        return new Dictionary<string, string> { ["AQUARIUS_RUNTIME_PACKS"] = t.FilePath(folder) };
    }

    /// <summary>The placeholder web runtime pack (the web glue still comes from the installation).</summary>
    public static (string, string, byte[])[] Web => [("web", "aquarius.wasm", "web runtime"u8.ToArray()), ("web", "libc.so", "libc"u8.ToArray())];

    public static Subsystem ReadSubsystem(string executable)
    {
        using var stream = File.OpenRead(executable);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.PEHeader!.Subsystem;
    }
}

/// <summary>
/// The tools <c>aqua library build</c> needs for a windows-x64 binary: CMake and Ninja (AQUARIUS_CMAKE / AQUARIUS_NINJA,
/// PATH, or C:/Strawberry/c/bin) and the pinned LLVM toolchain of the installation (scripts/install-toolchain.ps1).
/// </summary>
internal static class NativeToolchain
{
    public static string? CMake => Find("AQUARIUS_CMAKE", "cmake");
    public static string? Ninja => Find("AQUARIUS_NINJA", "ninja");

    private static string? Find(string variable, string name)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured && File.Exists(configured)) return configured;
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(directory, exe))) return Path.Combine(directory, exe);
        string strawberry = Path.Combine("C:/Strawberry/c/bin", exe);
        return OperatingSystem.IsWindows() && File.Exists(strawberry) ? strawberry : null;
    }

    /// <summary>Why the windows-x64 native build cannot run here, or null when it can.</summary>
    public static string? Missing
    {
        get {
            if (!OperatingSystem.IsWindows()) return "Builds the windows-x64 native part, which needs a Windows host.";
            if (!WasmTestHost.Available) return WasmTestHost.MissingRuntime;
            if (CMake == null || Ninja == null) return "Needs CMake and Ninja (PATH, AQUARIUS_CMAKE / AQUARIUS_NINJA, or C:/Strawberry/c/bin).";
            var installation = AquariusInstallation.Current;
            if (installation == null) return "Needs an AquariusLang installation (AQUARIUS_HOME).";
            string nativeBuild = Environment.GetEnvironmentVariable("AQUARIUS_NATIVE_BUILD") is { Length: > 0 } configured ? configured
                : installation.IsRepository ? Path.Combine(installation.Root, ".native-build")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aquarius", "native-build");
            string toolchains = Path.Combine(nativeBuild, "toolchains");
            bool llvm = Directory.Exists(toolchains) && Directory.EnumerateDirectories(toolchains, "llvm-*").Any(d => File.Exists(Path.Combine(d, "bin", "clang.exe")));
            bool mingw = Directory.Exists(toolchains) && Directory.EnumerateDirectories(toolchains, "llvm-mingw-*").Any();
            return llvm && mingw ? null : "Needs the pinned LLVM and llvm-mingw toolchain (scripts/install-toolchain.ps1 -Targets windows-x64).";
        }
    }

    public static Dictionary<string, string> BuildEnvironment => new() { ["AQUARIUS_CMAKE"] = CMake!, ["AQUARIUS_NINJA"] = Ninja! };

    /// <summary>Writes a tiny external library with a C native part (module TinyNative|小原生: twice|兩倍, hello|問候) and a
    /// test program that imports it by both names.</summary>
    public static string WriteTinyLibrary(string folder)
    {
        void Write(string relative, string text)
        {
            string file = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        Write("aqua.toml", "manifest_version = 2\n\n[library]\nname = \"tiny\"\nversion = \"1.0.0\"\nplatforms = [\"windows\", \"linux\"]\n\n" +
            "[native]\nname = \"tiny\"\nmodules = [\"TinyNative|小原生\"]\nsource = \"native\"\n");
        Write("native/CMakeLists.txt", "cmake_minimum_required(VERSION 3.20)\nproject(aqua_tiny C)\ninclude(AquariusLibrary)\naqua_add_library(tiny SOURCES src/tiny.c)\n");
        Write("native/src/tiny.c", "#include <aqua/aqua.h>\n" +
            "static void twice(aqua_call *call) { aqua_return_double(call, aqua_arg_number(call, 0) * 2); }\n" +
            "static void hello(aqua_call *call) { aqua_return_cstring(call, \"tiny native\"); }\n" +
            "AQUA_LIBRARY(tiny) {\n" +
            "    aqua_class *module = aqua_define_module(library, \"TinyNative|小原生\");\n" +
            "    aqua_define_function(module, \"twice|兩倍\", twice, \"n\");\n" +
            "    aqua_define_function(module, \"hello|問候\", hello, \"\");\n" +
            "}\n");
        Write("tests/case.aqua", "變數 t = 匯入(\"TinyNative\");\n印出(t.twice(21));\n變數 小 = 匯入(\"小原生\");\n印出(小.問候());\n");
        return folder;
    }

    private static readonly Lazy<string> built = new(() => {
        string folder = WriteTinyLibrary(Path.Combine(Path.GetTempPath(), "aquarius-tiny-library-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(folder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } };
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(CompilerCommandLine).Assembly.Location);
        foreach (string arg in new[] { "library", "build", folder, "--targets", "windows-x64" }) start.ArgumentList.Add(arg);
        foreach (var (name, value) in BuildEnvironment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000)) { process.Kill(true); throw new TimeoutException("aqua library build timed out."); }
        Assert.True(process.ExitCode == 0, $"aqua library build failed ({process.ExitCode}):\n{output.Result}\n{error.Result}");
        return folder;
    });
    /// <summary>The tiny library with its windows-x64 binary built (once per test run).</summary>
    public static string BuiltTinyLibrary => built.Value;
}

/// <summary>A fact that builds a native library part for windows-x64: skipped without CMake, Ninja, the pinned toolchain
/// or the host runtime pack.</summary>
public sealed class NativeToolchainFactAttribute : FactAttribute
{
    public NativeToolchainFactAttribute() { if (NativeToolchain.Missing is { } reason) Skip = reason; }
}

public sealed class LinuxNativeToolchainFactAttribute : FactAttribute
{
    public LinuxNativeToolchainFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Builds and loads a Linux C++ SDK library on a Linux host.";
        else if (!WasmTestHost.Available) Skip = WasmTestHost.MissingRuntime;
        else if (NativeToolchain.CMake == null) Skip = "Needs CMake.";
        else if (Environment.GetEnvironmentVariable("AQUARIUS_LLVM") is not { Length: > 0 } llvm || !File.Exists(Path.Combine(llvm, "bin", "clang++")))
            Skip = "Needs the pinned Linux LLVM toolchain in AQUARIUS_LLVM.";
    }
}
