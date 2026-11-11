using System.Text.RegularExpressions;

namespace AquariusLang.Build.Libraries;

/// <summary>An <c>[export.&lt;platform&gt;]</c> table: overrides for one platform (validated against its
/// <see cref="INativeLibraryPlatform"/>).</summary>
public sealed record NativeLibraryPlatformSettings(IReadOnlyList<string>? Kinds = null, IReadOnlyList<string>? Bindings = null,
    string? Package = null, IReadOnlyList<string>? Targets = null, IReadOnlyList<string>? ImportLibraries = null, string? Module = null,
    string? JavaPackage = null, string? JavaClass = null, string? BundleId = null);

/// <summary>
/// How a project's native library outputs are named and what they include: the root manifest's <c>[export]</c> table, or
/// defaults for a build that names <c>--kind</c> without one (docs/native-libraries.md).
/// </summary>
/// <param name="Name">The file base name (<c>geometry</c>: geometry.dll, libgeometry.so, geometry.h).</param>
/// <param name="Prefix">The C symbol prefix (<c>geo_</c>); null for <c>Name + "_"</c>.</param>
/// <param name="Kinds">The kinds every platform builds unless its table says otherwise; null for <c>["shared"]</c>.</param>
/// <param name="Module">A <c>[library]</c>'s facade module (its <c>[[modules]]</c> entry) whose exports are exposed.</param>
/// <param name="Version">The project's version; its major number is the Linux SONAME's.</param>
public sealed record NativeLibrarySettings(string Name, string? Prefix = null, IReadOnlyList<string>? Kinds = null, string? Module = null,
    IReadOnlyDictionary<string, NativeLibraryPlatformSettings>? Platforms = null, string? Version = null)
{
    public static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z0-9_]*$");
    public static readonly Regex PrefixPattern = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>Settings for a build without <c>[export]</c>: the name in lower case, everything else by default.</summary>
    public static NativeLibrarySettings Default(string name, string? version = null)
    {
        string clean = new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        if (clean.Length == 0 || !char.IsAsciiLetter(clean[0])) clean = "lib" + clean;
        return new(clean, Version: version);
    }

    public string SymbolPrefix => Prefix ?? Name + "_";
    public NativeLibraryPlatformSettings For(string platform) => Platforms?.GetValueOrDefault(platform) ?? new();
    public IReadOnlyList<string> KindsFor(string platform) => For(platform).Kinds ?? Kinds ?? ["shared"];
    public IReadOnlyList<string> BindingsFor(INativeLibraryPlatform platform) => For(platform.Platform).Bindings ?? platform.DefaultBindings;
    /// <summary>The major version (0 without a version).</summary>
    public int Major => Version != null && int.TryParse(Version.Split('.')[0], out int major) ? major : 0;
}
