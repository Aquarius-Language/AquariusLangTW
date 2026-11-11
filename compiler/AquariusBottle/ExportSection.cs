using AquariusLang.Build.Libraries;
using AquariusLang.Compiler;
using Tomlyn.Model;

namespace AquariusLang.Bottle;

/// <summary>
/// The <c>[export]</c> table of aqua.toml and its <c>[export.&lt;platform&gt;]</c> tables (docs/native-libraries.md). Every
/// key and value is checked against the platform descriptors (<see cref="NativeLibraryPlatforms"/>) and the target table;
/// an error names the key's position, as the editor shows it.
/// </summary>
internal static class ExportSection
{
    private static readonly string[] Keys = ["name", "prefix", "kinds", "module"];

    public static NativeLibrarySettings Parse(string text, string file, object value, ProjectManifest manifest)
    {
        if (value is not TomlTable table) throw Error(text, file, "export", "", "[export] must be a table, e.g. [export] name = \"geometry\".");
        string? name = null, prefix = null, module = null;
        IReadOnlyList<string>? kinds = null;
        var platforms = new Dictionary<string, NativeLibraryPlatformSettings>(StringComparer.Ordinal);
        foreach (var (key, item) in table) {
            if (item is TomlTable platformTable) {
                var platform = NativeLibraryPlatforms.Find(key)
                    ?? throw Error(text, file, key, "export", $"Unknown [export] platform '{key}'; use {string.Join(", ", NativeLibraryPlatforms.All.Select(p => p.Platform))}.");
                platforms[key] = ParsePlatform(text, file, platform, platformTable);
                continue;
            }
            switch (key) {
                case "name":
                    name = String(text, file, "export", key, item);
                    if (!NativeLibrarySettings.NamePattern.IsMatch(name)) throw Error(text, file, key, "export", "'name' must start with a letter and use only ASCII letters, digits and underscores.");
                    break;
                case "prefix":
                    prefix = String(text, file, "export", key, item);
                    if (!NativeLibrarySettings.PrefixPattern.IsMatch(prefix)) throw Error(text, file, key, "export", "'prefix' must be a C identifier prefix (ASCII letters, digits and underscores, not starting with a digit).");
                    break;
                case "kinds": kinds = Choices(text, file, "export", key, item, NativeLibraryPlatforms.KindNames); break;
                case "module":
                    module = String(text, file, "export", key, item);
                    if (manifest.Library is not { } library) throw Error(text, file, key, "export", "'module' applies to a [library]: it names the [[modules]] entry whose exports the library exposes.");
                    if (!library.Modules.Any(m => m.Names.Contains(module, StringComparer.Ordinal)))
                        throw Error(text, file, key, "export", $"'module' must name one of the library's [[modules]]: {string.Join(", ", library.Modules.Select(m => m.Names[0]))}.");
                    break;
                default: throw Error(text, file, key, "export", $"Unknown [export] key '{key}'; use {string.Join(", ", Keys)} or an [export.<platform>] table.");
            }
        }
        // A [library] without 'module' exposes its first module, the way an application exposes its entry.
        var settings = name != null ? new NativeLibrarySettings(name, Version: manifest.Version) : NativeLibrarySettings.Default(manifest.Name, manifest.Version);
        return settings with { Prefix = prefix, Kinds = kinds, Module = module, Platforms = platforms };
    }

    private static NativeLibraryPlatformSettings ParsePlatform(string text, string file, INativeLibraryPlatform platform, TomlTable table)
    {
        string section = "export." + platform.Platform;
        var settings = new NativeLibraryPlatformSettings();
        foreach (var (key, item) in table) {
            if (!NativeLibraryPlatforms.CommonKeys.Contains(key) && !platform.ManifestKeys.Contains(key))
                throw Error(text, file, key, section, $"Unknown [{section}] key '{key}'; use {string.Join(", ", NativeLibraryPlatforms.CommonKeys.Concat(platform.ManifestKeys))}.");
            settings = key switch {
                "kinds" => settings with { Kinds = Choices(text, file, section, key, item, platform.Kinds) },
                "bindings" => settings with { Bindings = Choices(text, file, section, key, item, platform.Bindings) },
                "package" => settings with { Package = Choice(text, file, section, key, item, platform.Packages) },
                "targets" => settings with { Targets = Choices(text, file, section, key, item,
                    RuntimeTarget.Built.Where(t => t.Platform == platform.Platform).Select(t => t.Name).ToArray()) },
                "import_libraries" => settings with { ImportLibraries = Choices(text, file, section, key, item, AquariusLang.Build.Libraries.Platforms.WindowsLibraryPlatform.ImportLibraries) },
                "module" => settings with { Module = Identifier(text, file, section, key, item) },
                "java_package" => settings with { JavaPackage = JavaPackage(text, file, section, key, item) },
                "java_class" => settings with { JavaClass = Identifier(text, file, section, key, item) },
                _ => settings
            };
        }
        return settings;
    }

    private static string String(string text, string file, string section, string key, object item) =>
        item is string value && !string.IsNullOrWhiteSpace(value) ? value : throw Error(text, file, key, section, $"'{key}' must be a nonempty string.");

    private static string Choice(string text, string file, string section, string key, object item, IReadOnlyList<string> allowed)
    {
        string value = String(text, file, section, key, item);
        return allowed.Contains(value) ? value : throw Error(text, file, key, section, $"'{key}' must be one of {string.Join(", ", allowed)} here; '{value}' is not.");
    }

    private static IReadOnlyList<string> Choices(string text, string file, string section, string key, object item, IReadOnlyList<string> allowed)
    {
        if (item is not TomlArray array || array.Count == 0 || array.Any(v => v is not string))
            throw Error(text, file, key, section, $"'{key}' must be a nonempty array of strings from {string.Join(", ", allowed)}.");
        var values = array.Cast<string>().ToArray();
        if (values.FirstOrDefault(v => !allowed.Contains(v)) is { } wrong)
            throw Error(text, file, key, section, $"'{key}' allows {string.Join(", ", allowed)} here; '{wrong}' is not one of them.");
        if (values.Distinct().Count() != values.Length) throw Error(text, file, key, section, $"'{key}' lists a value twice.");
        return values;
    }

    private static string Identifier(string text, string file, string section, string key, object item)
    {
        string value = String(text, file, section, key, item);
        return ExportTable.IsIdentifier(value) ? value : throw Error(text, file, key, section, $"'{key}' must be an ASCII identifier.");
    }

    private static string JavaPackage(string text, string file, string section, string key, object item)
    {
        string value = String(text, file, section, key, item);
        return value.Split('.').All(ExportTable.IsIdentifier) ? value : throw Error(text, file, key, section, $"'{key}' must be a Java package such as org.example.geometry.");
    }

    private static ManifestException Error(string text, string file, string key, string section, string message) =>
        new(file, [ProjectManifest.Locate(text, key, message, section)]);
}
