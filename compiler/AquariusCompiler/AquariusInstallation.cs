using System;
using System.IO;
using System.Linq;

namespace AquariusLang.Compiler;

/// <summary>
/// Where the compiler finds what it builds applications from. A release (dist/aqua/) keeps everything beside the
/// compiler; a repository checkout keeps it at its source locations:
/// <code>
///                      release (dist/aqua/)      repository
///   default libraries  libraries/&lt;name&gt;/          libraries/&lt;name&gt;/
///   runtime packs      runtime/&lt;target&gt;/           .native-build/install/&lt;target&gt;/  (scripts/build-runtime.ps1)
///   web glue           web-glue/                 web-glue/
///   library SDK        sdk/                      runtime/sdk/
/// </code>
/// <c>AQUARIUS_HOME</c> selects another release or checkout; <c>AQUARIUS_RUNTIME_PACKS</c> another folder of runtime
/// packs (one sub-folder per target).
/// </summary>
public sealed class AquariusInstallation
{
    /// <summary>Every target a runtime pack exists for (<see cref="RuntimeTarget.Built"/>; docs/adding-a-target.md adds one).</summary>
    public static readonly string[] Targets = RuntimeTarget.Built.Select(t => t.Name).ToArray();

    public string Root { get; }
    public bool IsRepository { get; }
    public string Libraries { get; }
    public string WebGlue { get; }
    public string Sdk { get; }
    public string RuntimePacks { get; }

    private AquariusInstallation(string root, bool repository)
    {
        Root = root; IsRepository = repository;
        Libraries = Path.Combine(root, "libraries");
        WebGlue = Path.Combine(root, "web-glue");
        Sdk = repository ? Path.Combine(root, "runtime", "sdk") : Path.Combine(root, "sdk");
        RuntimePacks = Environment.GetEnvironmentVariable("AQUARIUS_RUNTIME_PACKS") is { Length: > 0 } packs ? Path.GetFullPath(packs)
            : repository ? Path.Combine(Environment.GetEnvironmentVariable("AQUARIUS_NATIVE_BUILD") is { Length: > 0 } native
                ? Path.GetFullPath(native) : Path.Combine(root, ".native-build"), "install")
            : Path.Combine(root, "runtime");
    }

    private static readonly Lazy<AquariusInstallation?> current = new(Locate);
    /// <summary>The installation of this compiler, or null when neither a release layout nor a checkout surrounds it.</summary>
    public static AquariusInstallation? Current => current.Value;
    public static AquariusInstallation Require() => Current ?? throw new IOException(
        "The AquariusLang runtime files were not found beside the compiler. Install the complete aqua distribution, or set AQUARIUS_HOME to it or to a repository checkout.");

    /// <summary>The installation rooted at a release or checkout folder, or null if it is neither.</summary>
    public static AquariusInstallation? At(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(directory, "runtime", "CMakeLists.txt")) && Directory.Exists(Path.Combine(directory, "compiler")))
            return new AquariusInstallation(directory, true);
        if (Directory.Exists(Path.Combine(directory, "libraries")) && Directory.Exists(Path.Combine(directory, "runtime")) &&
            Directory.Exists(Path.Combine(directory, "web-glue")))
            return new AquariusInstallation(directory, false);
        return null;
    }

    private static AquariusInstallation? Locate()
    {
        if (Environment.GetEnvironmentVariable("AQUARIUS_HOME") is { Length: > 0 } home)
            return At(home) ?? throw new IOException($"AQUARIUS_HOME ({home}) is neither an aqua distribution nor a repository checkout.");
        for (string? directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
            if (At(directory) is { } found) return found;
        // Tools shipped elsewhere (the editor extension's language server) use the aqua on PATH.
        string aqua = OperatingSystem.IsWindows() ? "aqua.exe" : "aqua";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(directory, aqua)) && At(directory) is { } distribution) return distribution;
        return null;
    }

    /// <summary>The runtime pack folder of a target (it may not exist yet).</summary>
    public string RuntimePack(string target) => Path.Combine(RuntimePacks, target);

    /// <summary>The runtime pack folder of a target, verified to hold the files a build needs.</summary>
    public string RequireRuntimePack(string target, params string[] files)
    {
        string pack = RuntimePack(target);
        foreach (string file in files)
            if (!File.Exists(Path.Combine(pack, file)))
                throw new IOException($"The {target} runtime pack is missing {file} (looked in {pack}). " + (IsRepository
                    ? $"Build it with scripts/build-runtime.ps1 -Targets {target}."
                    : "Install the complete aqua distribution."));
        return pack;
    }

    /// <summary>The platform of the machine the compiler runs on, as scripts/toolchain.json names its toolchain downloads
    /// (windows-x64, linux-x64, macos-arm64, ...).</summary>
    public static string HostPlatform { get; } = (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown")
        + "-" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The runtime target of the machine the compiler runs on, for <c>aqua run</c>: its platform, when a runtime
    /// pack exists for it.</summary>
    public static string HostTarget => RuntimeTarget.Find(HostPlatform) is { Planned: false, IsDesktop: true } target ? target.Name
        : throw new PlatformNotSupportedException($"AquariusLang has no runtime for this host ({HostPlatform}) yet; it runs on {string.Join(", ", RuntimeTarget.Built.Where(t => t.IsDesktop).Select(t => t.Name))}.");

    /// <summary>Whether a target is a desktop operating system: its native binaries build and run only on that host.</summary>
    public static bool IsDesktop(string target) => RuntimeTarget.Get(target).IsDesktop;

    /// <summary>Throws unless this host can build native binaries for <paramref name="target"/> (<see cref="RuntimeTarget.BuildsOn"/>:
    /// a desktop target on itself, a mobile or web one on the hosts it lists, every host when it lists none).</summary>
    public static void RequireBuildableHere(string target, string what)
    {
        var runtime = RuntimeTarget.Get(target);
        if (!runtime.BuildsOn(HostPlatform))
            throw new ArgumentException($"{what} for {target} build on {(runtime.Hosts is { } hosts ? string.Join(" or ", hosts) : "a " + target)} host" +
                $" (this one is {HostPlatform}{(target == "linux-x64" ? "; on Windows, build Linux ones in WSL" : "")}).");
    }
}
