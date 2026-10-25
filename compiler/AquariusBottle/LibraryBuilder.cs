using System.Diagnostics;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLang.Bottle;

/// <summary>
/// <c>aqua library build [folder] [--targets windows-x64,web,...]</c>: builds an external library's native part
/// ([native] source, a CMake folder using aqua_add_library) with the pinned toolchain of the SDK
/// (sdk/cmake/toolchains/&lt;target&gt;.cmake) and places the binaries where installs and builds take them from:
/// native/&lt;target&gt;/aqua_&lt;name&gt;.dll, libaqua_&lt;name&gt;.so or &lt;name&gt;.wasm. Build trees go to build/&lt;target&gt;/.
/// </summary>
public static class LibraryBuilder {
    public const string Usage = "aqua library build [folder] [--targets windows-x64,linux-x64,android-arm64-v8a,android-x86_64,web]";

    public static IReadOnlyList<string> Build(string folder, IReadOnlyList<string>? targets = null, Action<string>? log = null) {
        var manifest = ProjectManifest.Load(folder);
        var library = manifest.Library ?? throw new InvalidDataException($"{manifest.FilePath} is not a library manifest (manifest_version = 2 with [library]).");
        if (library.NativeName == null || library.NativeSource == null)
            throw new InvalidDataException($"{library.Name} has no native part to build: add [native] name, modules and source (the CMake folder).");
        string source = library.SourcePath(library.NativeSource);
        if (!File.Exists(Path.Combine(source, "CMakeLists.txt"))) throw new IOException($"[native] source has no CMakeLists.txt: {source}");
        targets ??= [AquariusInstallation.HostTarget];
        foreach (string target in targets)
            if (!AquariusInstallation.Targets.Contains(target)) throw new ArgumentException($"Unknown target {target}; use {string.Join(", ", AquariusInstallation.Targets)}.");
        var installation = AquariusInstallation.Require();
        string cmake = Tool("AQUARIUS_CMAKE", "cmake"), ninja = Tool("AQUARIUS_NINJA", "ninja");
        var environment = new Dictionary<string, string>();
        if (!installation.IsRepository) {
            // A distribution keeps the pins in scripts/toolchain.json beside the SDK; toolchains install per user.
            environment["AQUARIUS_SDK_ROOT"] = installation.Root;
            if (Environment.GetEnvironmentVariable("AQUARIUS_NATIVE_BUILD") == null)
                environment["AQUARIUS_NATIVE_BUILD"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aquarius", "native-build");
        }
        var built = new List<string>();
        foreach (string target in targets) {
            if (target.StartsWith("linux-", StringComparison.Ordinal) && !OperatingSystem.IsLinux()) throw new ArgumentException($"Build {target} binaries on Linux (on Windows, run aqua in WSL).");
            if (target.StartsWith("windows-", StringComparison.Ordinal) && !OperatingSystem.IsWindows()) throw new ArgumentException($"Build {target} binaries on Windows.");
            string toolchain = Path.Combine(installation.Sdk, "cmake", "toolchains", target + ".cmake");
            if (!File.Exists(toolchain)) throw new IOException($"The SDK has no toolchain file for {target}: {toolchain}");
            string build = Path.Combine(manifest.Root, "build", target);
            log?.Invoke($"Building {library.Name} for {target}...");
            Run(cmake, ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja, "-DCMAKE_TOOLCHAIN_FILE=" + toolchain,
                "-DCMAKE_BUILD_TYPE=Release"], environment);
            Run(cmake, ["--build", build], environment);
            string file = Library.NativeFile(library.NativeName, target);
            string? output = Directory.EnumerateFiles(build, file, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new IOException($"The build produced no {file}: the CMakeLists.txt must call aqua_add_library({library.NativeName} ...).");
            string destination = Path.Combine(manifest.Root, "native", target, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            AquariusLang.FileSystem.TransientFileLocks.Retry(() => File.Copy(output, destination, true));
            built.Add(destination);
        }
        return built;
    }

    private static string Tool(string variable, string name) {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured) return configured;
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(directory, exe))) return Path.Combine(directory, exe);
        throw new IOException($"{name} was not found on PATH; install it (CMake 3.20+ and Ninja) or set {variable}.");
    }

    private static void Run(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment) {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {program}.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            string text = (output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult()).Trim();
            if (text.Length > 16384) text = text[^16384..];
            throw new IOException($"{Path.GetFileName(program)} failed (exit {process.ExitCode}).\n{text}");
        }
    }
}
