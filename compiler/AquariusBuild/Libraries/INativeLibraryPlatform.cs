using AquariusLang.Compiler;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// What native library outputs (docs/native-libraries.md) a platform offers: the kinds it can build, the bindings it can
/// write, how outputs are packaged and the extra keys its <c>[export.&lt;platform&gt;]</c> table takes. One class per
/// platform in Platforms/; a new target's platform adds one here and an <c>embed.cmake</c> in its platform layer. File
/// naming stays in <see cref="RuntimeTarget"/>.
/// </summary>
public interface INativeLibraryPlatform
{
    /// <summary>The platform as <see cref="RuntimeTarget.Platform"/> names it.</summary>
    string Platform { get; }
    /// <summary>Allowed kinds ("shared", "static", "object").</summary>
    IReadOnlyList<string> Kinds { get; }
    /// <summary>Bindings the platform can write and those it writes when the manifest names none.</summary>
    IReadOnlyList<string> Bindings { get; }
    IReadOnlyList<string> DefaultBindings { get; }
    /// <summary>Allowed packages; the first is the default.</summary>
    IReadOnlyList<string> Packages { get; }
    /// <summary>Keys of <c>[export.&lt;platform&gt;]</c> beyond kinds, bindings, package and targets.</summary>
    IReadOnlyList<string> ManifestKeys { get; }
    /// <summary>Whether this compiler builds the platform's outputs yet (the phases of docs/native-libraries.md).</summary>
    bool Builds { get; }
    /// <summary>Whether the platform's outputs have been built and run on its own hardware. A build of an unverified
    /// platform says so (docs/native-libraries.md lists what verifying it takes).</summary>
    bool Verified { get; }
}

/// <summary>The platform descriptors, one per built platform of <see cref="RuntimeTarget.All"/>.</summary>
public static class NativeLibraryPlatforms
{
    public static readonly IReadOnlyList<string> KindNames = ["shared", "static", "object"];
    public static readonly IReadOnlyList<string> BindingNames = ["c", "cpp", "rust", "swift", "java", "javascript"];
    public static readonly IReadOnlyList<string> CommonKeys = ["kinds", "bindings", "package", "targets"];

    public static IReadOnlyList<INativeLibraryPlatform> All { get; } = [
        new Platforms.WindowsLibraryPlatform(), new Platforms.LinuxLibraryPlatform(), new Platforms.MacOSLibraryPlatform(),
        new Platforms.IosLibraryPlatform(), new Platforms.AndroidLibraryPlatform(), new Platforms.WebLibraryPlatform()
    ];

    public static INativeLibraryPlatform? Find(string platform) => All.FirstOrDefault(p => p.Platform == platform);
}
