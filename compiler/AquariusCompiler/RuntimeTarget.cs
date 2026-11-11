using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.Compiler;

/// <summary>How a target's applications run: on a desktop operating system (its native binaries build and run on a host
/// of the same target), on a phone or tablet, or in a browser.</summary>
public enum TargetKind { Desktop, Mobile, Web }

/// <summary>
/// One runtime target: a runtime pack (.native-build/install/&lt;name&gt;, runtime/&lt;name&gt; in a release), a CMake
/// toolchain file (runtime/sdk/cmake/toolchains/&lt;name&gt;.cmake) and a platform layer
/// (runtime/platform/&lt;platform&gt;). <see cref="All"/> is the compiler's copy of scripts/toolchain.json's targets.list,
/// which the scripts and CMake read; the architecture tests keep the two equal. A <see cref="Planned"/> target is
/// reserved (its name is known and reported as not built yet) until its toolchain file, platform layer and build target
/// exist; docs/adding-a-target.md lists the steps.
/// </summary>
/// <param name="Name">The target (windows-x64, android-arm64-v8a, web, ...).</param>
/// <param name="Platform">Its operating system as library manifests, application metadata and <c>AQUA_OS_*</c> name it.</param>
/// <param name="Kind">Desktop, mobile or web.</param>
/// <param name="DotnetRuntime">A desktop host's .NET runtime identifier, for publishing aqua there.</param>
/// <param name="Hosts">The host platforms that can build it; null for the default (itself for desktop targets, every
/// host for mobile and web ones).</param>
/// <param name="Planned">Reserved but not built yet.</param>
public sealed record RuntimeTarget(string Name, string Platform, TargetKind Kind, string? DotnetRuntime = null,
    IReadOnlyList<string>? Hosts = null, bool Planned = false)
{
    public static IReadOnlyList<RuntimeTarget> All { get; } = [
        new("windows-x64", "windows", TargetKind.Desktop, "win-x64"),
        new("linux-x64", "linux", TargetKind.Desktop, "linux-x64"),
        new("android-arm64-v8a", "android", TargetKind.Mobile),
        new("android-x86_64", "android", TargetKind.Mobile),
        new("web", "web", TargetKind.Web),
        new("macos-arm64", "macos", TargetKind.Desktop, "osx-arm64"),
        new("ios-arm64", "ios", TargetKind.Mobile, Hosts: ["macos-arm64"]),
        new("ios-simulator-arm64", "ios", TargetKind.Mobile, Hosts: ["macos-arm64"]),
    ];

    /// <summary>The targets that are built (have runtime packs), in table order.</summary>
    public static IReadOnlyList<RuntimeTarget> Built { get; } = All.Where(t => !t.Planned).ToArray();

    /// <summary>The target named <paramref name="name"/>, planned or built, or null.</summary>
    public static RuntimeTarget? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>The built target named <paramref name="name"/>; planned and unknown names throw an error that says so.</summary>
    public static RuntimeTarget Get(string name) => Find(name) switch {
        { Planned: false } target => target,
        { Planned: true } => throw new ArgumentException($"The {name} target is planned but not built yet (docs/adding-a-target.md)."),
        null => throw new ArgumentException($"Unknown target {name}; use {string.Join(", ", Built.Select(t => t.Name))}.")
    };

    public bool IsDesktop => Kind == TargetKind.Desktop;
    private bool Apple => Platform is "macos" or "ios";

    /// <summary>The suffix of the target's programs (aquarius.exe on Windows).</summary>
    public string ExecutableSuffix => Platform == "windows" ? ".exe" : "";

    /// <summary>The suffix of the target's shared libraries (.dll, .dylib, .so; .wasm side modules on the web).</summary>
    public string SharedLibrarySuffix => Platform switch { "windows" => ".dll", "web" => ".wasm", _ when Apple => ".dylib", _ => ".so" };

    /// <summary>A native library's file as aqua_add_library builds it and runtime packs and applications name it:
    /// aqua_&lt;name&gt;.dll on Windows, &lt;name&gt;.wasm on the web, libaqua_&lt;name&gt; with the shared library suffix
    /// elsewhere.</summary>
    public string NativeFile(string nativeName) => Platform switch {
        "windows" => $"aqua_{nativeName}.dll",
        "web" => $"{nativeName}.wasm",
        _ => $"libaqua_{nativeName}{SharedLibrarySuffix}"
    };

    /// <summary>A native library output's shared library (docs/native-libraries.md): geometry.dll on Windows,
    /// lib&lt;name&gt; with the shared library suffix elsewhere; on Linux the file is the versioned
    /// <see cref="VersionedSharedLibraryName"/> and this name a link to it.</summary>
    public string SharedLibraryName(string name) => Platform == "windows" ? name + ".dll" : "lib" + name + SharedLibrarySuffix;

    /// <summary>The SONAME of a native library output where the platform versions shared libraries (libgeometry.so.1 on
    /// Linux), else null.</summary>
    public string? VersionedSharedLibraryName(string name, int major) => Platform == "linux" ? $"lib{name}.so.{major}" : null;

    /// <summary>The folder of a native library output's shared library: bin on Windows (beside the programs that load
    /// it), lib elsewhere.</summary>
    public string SharedLibraryFolder => Platform == "windows" ? "bin" : "lib";

    /// <summary>A native library output's static library, lib&lt;name&gt;.a (MinGW's naming on Windows, which keeps it apart
    /// from the MSVC import library &lt;name&gt;.lib).</summary>
    public string StaticLibraryName(string name) => $"lib{name}.a";

    /// <summary>A native library output's prelinked object, &lt;name&gt;.o.</summary>
    public string ObjectName(string name) => name + ".o";

    /// <summary>The import libraries a native library output's DLL comes with, by kind: MinGW's lib&lt;name&gt;.dll.a and
    /// MSVC's &lt;name&gt;.lib on Windows; none elsewhere.</summary>
    public IReadOnlyList<(string Kind, string File)> ImportLibraryNames(string name) =>
        Platform == "windows" ? [("mingw", $"lib{name}.dll.a"), ("msvc", $"{name}.lib")] : [];

    /// <summary>Whether a host (a desktop platform such as windows-x64 or macos-arm64) can build this target's native
    /// binaries.</summary>
    public bool BuildsOn(string host) => Hosts?.Contains(host) ?? (!IsDesktop || host == Name);
}
