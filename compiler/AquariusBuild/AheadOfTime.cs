using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquariusLang.Compiler;

namespace AquariusLang.Build;

/// <summary>
/// <c>aqua build --target windows|linux --optimize</c> (CLAUDE.md rule 11): the application's .wasm, translated to C by
/// the pinned wasm2c (WABT) and compiled by the pinned Clang through the runtime pack's aot/ project into
/// &lt;executable&gt;.aot.dll / .aot.so (the target's shared library suffix), which the pack's aquarius-aot launcher runs
/// (runtime/platform/desktop/engine_aot.c).
/// The executable still embeds the same .wasm (its metadata and the library's SHA-256 check). The toolchain comes from
/// scripts/toolchain.json and is installed on first use by scripts/install-toolchain, each archive SHA-256 checked.
/// </summary>
public static partial class AheadOfTime
{
    /// <summary>Writes the library for the module at <paramref name="wasm"/> beside the executable <paramref name="executable"/>.</summary>
    public static string BuildLibrary(string wasm, string executable, string runtimeTarget, string pack)
    {
        var target = RuntimeTarget.Get(runtimeTarget);
        AquariusInstallation.RequireBuildableHere(runtimeTarget, "--optimize executables");
        string output = Path.ChangeExtension(Path.GetFullPath(executable), null) + ".aot" + target.SharedLibrarySuffix;
        return Compile(wasm, output, runtimeTarget, Path.Combine(pack, "aot"));
    }

    /// <summary>Translates the module at <paramref name="wasm"/> with wasm2c and builds it with the pack's CMake
    /// <paramref name="project"/> (aot/: a desktop library; app/: an iOS executable; embed/: a native library output) into
    /// <paramref name="output"/>, passing the project its own <paramref name="definitions"/> too.</summary>
    public static string Compile(string wasm, string output, string runtimeTarget, string project, IReadOnlyDictionary<string, string>? definitions = null)
    {
        var installation = AquariusInstallation.Require();
        var environment = new Dictionary<string, string>();
        string native = Environment.GetEnvironmentVariable("AQUARIUS_NATIVE_BUILD") is { Length: > 0 } configured ? configured
            : installation.IsRepository ? Path.Combine(installation.Root, ".native-build")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aquarius", "native-build");
        if (!installation.IsRepository) { environment["AQUARIUS_SDK_ROOT"] = installation.Root; environment["AQUARIUS_NATIVE_BUILD"] = native; }

        using var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(installation.Root, "scripts", "toolchain.json")));
        // The tools run on this host.
        string host = AquariusInstallation.HostPlatform, exe = OperatingSystem.IsWindows() ? ".exe" : "";
        string Directory(string tool) => Path.Combine(native, "toolchains", pins.RootElement.GetProperty(tool).GetProperty("directory").GetString()!.Replace("{platform}", host));
        string wabt = Directory("wabt"), ninja = Path.Combine(Directory("ninja"), "ninja" + exe);
        string cmake = Path.Combine(Directory("cmake"), pins.RootElement.GetProperty("cmake").GetProperty(host).GetProperty("bin").GetString()!, "cmake" + exe);
        string wasm2c = Path.Combine(wabt, "bin", "wasm2c" + exe);
        if (!File.Exists(wasm2c) || !File.Exists(cmake) || !File.Exists(ninja)) InstallToolchain(installation, runtimeTarget, environment);

        string work = Path.Combine(Path.GetTempPath(), "aquarius-aot-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(work);
        try {
            string source = Path.Combine(work, "application");
            Run(wasm2c, [wasm, "-n", "app", "-o", source + ".c"], environment);
            File.WriteAllText(source + ".c", Repair(File.ReadAllText(source + ".c")));
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wasm))).ToLowerInvariant();
            string toolchain = Path.Combine(installation.Sdk, "cmake", "toolchains", runtimeTarget + ".cmake");
            string build = Path.Combine(work, "build");
            Run(cmake, ["-S", project, "-B", build, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja,
                "-DCMAKE_TOOLCHAIN_FILE=" + toolchain, "-DCMAKE_BUILD_TYPE=Release", "-DAQUA_AOT_SOURCE=" + Forward(source),
                "-DAQUA_AOT_DIGEST=" + digest, "-DAQUA_WASM2C=" + Forward(Path.Combine(wabt, "share", "wabt", "wasm2c")),
                "-DAQUA_WASM2C_INCLUDE=" + Forward(Path.Combine(wabt, "include")), "-DAQUA_AOT_OUTPUT=" + Forward(output),
                .. (definitions ?? new Dictionary<string, string>()).Select(d => $"-D{d.Key}={Forward(d.Value)}")], environment);
            Run(cmake, ["--build", build], environment);
            if (!File.Exists(output)) throw new IOException($"The ahead-of-time build produced no {output}.");
            return output;
        } finally {
            try { System.IO.Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>WABT 1.0.42's wasm2c splits a function's local declaration with its stack-variable declarations in the
    /// tail-call variant of some functions (`u32   u32 var_i0, ...;` ... then `var_l4 = 0, ...;`); this puts them back in
    /// order. Code without the defect is unchanged.</summary>
    internal static string Repair(string c) => SplitDeclaration().Replace(c, "\n  $2\n  $1 $3");
    [GeneratedRegex(@"\n  (\w+)   (\w+ var_i0[^\n]*;(?:\n  \w+ var_[ijfd]0[^\n]*;)*)\n(var_l)")]
    private static partial Regex SplitDeclaration();

    private static string Forward(string path) => path.Replace('\\', '/');

    private static void InstallToolchain(AquariusInstallation installation, string target, IReadOnlyDictionary<string, string> environment)
    {
        string scripts = Path.Combine(installation.Root, "scripts");
        if (OperatingSystem.IsWindows())
            Run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scripts, "install-toolchain.ps1"), "-Targets", target], environment);
        else Run("bash", [Path.Combine(scripts, "install-toolchain.sh"), target], environment);
    }

    private static void Run(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
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
