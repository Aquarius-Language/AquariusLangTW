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
        new("ios-arm64", "ios", TargetKind.Mobile, Hosts: ["macos-arm64"], Planned: true),
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

    /// <summary>Whether a host (a desktop platform such as windows-x64 or macos-arm64) can build this target's native
    /// binaries.</summary>
    public bool BuildsOn(string host) => Hosts?.Contains(host) ?? (!IsDesktop || host == Name);
}
