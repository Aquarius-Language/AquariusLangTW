using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace AquariusLang.Libraries;

/// <summary>An Aquarius-source module of a library: its import names (aliases) and its source, relative to the
/// library folder.</summary>
public sealed record LibraryModule(IReadOnlyList<string> Names, string Source);

/// <summary>
/// A library as its manifest (aqua.toml, manifest_version = 2) describes it: a default library under libraries/ or an
/// external one (a Bottle package). Its native part registers <see cref="NativeModules"/> from C/C++; its
/// Aquarius-source <see cref="Modules"/> are compiled into the applications that import them.
/// </summary>
public sealed class Library
{
    public const string ManifestName = "aqua.toml";
    public static readonly string[] KnownPlatforms = ["windows", "linux", "android", "web", "macos", "ios", "freebsd"];

    public string Name { get; }
    public string Version { get; }
    public string? Description { get; }
    public string? License { get; }
    public string Root { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<string> Platforms { get; }
    /// <summary>The native library's file name stem (aqua_&lt;name&gt;.dll, libaqua_&lt;name&gt;.so, &lt;name&gt;.wasm) and the
    /// name applications list in their metadata; null when the library has no native part.</summary>
    public string? NativeName { get; }
    /// <summary>The modules the native part registers, each as its alias list ("Audio|音訊").</summary>
    public IReadOnlyList<IReadOnlyList<string>> NativeModules { get; }
    /// <summary>External libraries: the CMake folder of the native part, relative to <see cref="Root"/>.</summary>
    public string? NativeSource { get; }
    public IReadOnlyList<LibraryModule> Modules { get; }
    /// <summary>Android permissions the library needs; APKs of applications that need it request them.</summary>
    public IReadOnlyList<string> AndroidPermissions { get; }
    public bool HasNative => NativeName != null;
    /// <summary>An external library (a Bottle package): its native part ships as prebuilt binaries in native/&lt;target&gt;/
    /// instead of being linked into the runtime.</summary>
    public bool External { get; }

    private Library(string manifest, string name, string version, string? description, string? license, string[] platforms,
        string? nativeName, string[][] nativeModules, string? nativeSource, LibraryModule[] modules, string[] permissions, bool external)
    {
        External = external;
        ManifestPath = Path.GetFullPath(manifest); Root = Path.GetDirectoryName(ManifestPath)!;
        Name = name; Version = version; Description = description; License = license; Platforms = platforms;
        NativeName = nativeName; NativeModules = nativeModules; NativeSource = nativeSource; Modules = modules; AndroidPermissions = permissions;
    }

    /// <summary>Where a library's Aquarius source is bundled inside applications.</summary>
    public string BundlePath(string source) => ".aquarius/" + Name + "/" + source.Replace('\\', '/');
    public string SourcePath(string source) => Path.GetFullPath(Path.Combine(Root, source.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The native library file for a runtime target, as it is named in runtime packs and next to applications.</summary>
    public static string NativeFile(string nativeName, string target) =>
        target.StartsWith("windows-", StringComparison.Ordinal) ? $"aqua_{nativeName}.dll" : target == "web" ? $"{nativeName}.wasm" : $"libaqua_{nativeName}.so";

    /// <summary>An external library's prebuilt native binary for a target (native/&lt;target&gt;/&lt;file&gt;), or null.</summary>
    public string? NativeBinary(string target)
    {
        if (NativeName == null) return null;
        string file = Path.Combine(Root, "native", target, NativeFile(NativeName, target));
        return File.Exists(file) ? file : null;
    }

    public static bool ValidNativeName(string name) => Regex.IsMatch(name, "^[a-z0-9_]{1,64}$");
    public static bool ValidName(string name) => name.Length <= 100 && Regex.IsMatch(name, @"^[\p{L}\p{Nd}][\p{L}\p{Nd}_-]*$") &&
        !Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    public static Library Load(string path)
    {
        string file = Directory.Exists(path) ? Path.Combine(path, ManifestName) : path;
        string text = File.ReadAllText(file);
        var syntax = Toml.Parse(text, file);
        if (syntax.HasErrors) throw new LibraryManifestException(file, "", string.Join("; ", syntax.Diagnostics.Select(d => d.ToString())));
        return FromModel(file, syntax.ToModel(), external: false);
    }

    /// <summary>Validates the library tables of a manifest_version 2 manifest. External libraries (Bottle packages) may
    /// also have the <paramref name="projectKeys"/> top-level tables, which their package manager reads.</summary>
    public static Library FromModel(string file, TomlTable model, bool external, params string[] projectKeys)
    {
        string section = ""; // the table being validated, so editors can point at its key
        void Fail(string key, string message) => throw new LibraryManifestException(file, key, message, section);
        void Keys(TomlTable table, string section, params string[] keys) {
            foreach (string key in table.Keys) if (!keys.Contains(key)) Fail(key, $"Unknown {section} key '{key}'.");
        }
        string? Text(TomlTable table, string key, bool required = false) {
            if (!table.TryGetValue(key, out var value)) { if (required) Fail(key, $"Missing required '{key}'."); return null; }
            if (value is not string text || string.IsNullOrWhiteSpace(text)) { Fail(key, $"'{key}' must be a nonempty string."); return null; }
            return text;
        }
        string[] Texts(TomlTable table, string key) {
            if (!table.TryGetValue(key, out var value)) return [];
            if (value is not TomlArray array || array.Any(v => v is not string s || string.IsNullOrWhiteSpace(s))) Fail(key, $"'{key}' must be an array of nonempty strings.");
            return ((TomlArray)value).Cast<string>().ToArray();
        }
        string Relative(string value, string key) {
            string path = value.Replace('\\', '/');
            if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")) Fail(key, $"'{key}' must be a relative path inside the library folder: {value}");
            return path;
        }
        string root = Path.GetDirectoryName(Path.GetFullPath(file))!;

        Keys(model, "manifest", new[] { "manifest_version", "library", "native", "modules", "android" }.Concat(projectKeys).ToArray());
        if (!model.TryGetValue("manifest_version", out var version) || version is not 2L) Fail("manifest_version", "Library manifests need manifest_version = 2.");
        if (!model.TryGetValue("library", out var libraryValue) || libraryValue is not TomlTable library) { Fail("library", "Missing [library] table."); return null!; }
        section = "library";
        Keys(library, "library", "name", "version", "description", "license", "platforms");
        string name = Text(library, "name", true)!;
        if (!ValidName(name)) Fail("name", "Library names start with a letter or digit and contain only letters, digits, underscores or hyphens.");
        string libraryVersion = Text(library, "version", true)!;
        if (!Regex.IsMatch(libraryVersion, @"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")) Fail("version", "Library versions are semantic versions such as 1.2.0.");
        string[] platforms = Texts(library, "platforms").Select(p => p.ToLowerInvariant()).ToArray();
        if (platforms.Length == 0 || platforms.Distinct().Count() != platforms.Length || platforms.Any(p => !KnownPlatforms.Contains(p)))
            Fail("platforms", $"'platforms' must list the supported platforms: {string.Join(", ", KnownPlatforms)}.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] Aliases(string spec, string key) {
            string[] aliases = spec.Split('|');
            foreach (string alias in aliases)
                if (alias.Length == 0 || alias.Length > 128 || alias.Any(char.IsWhiteSpace) || alias.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) || alias.StartsWith('/') || !names.Add(alias))
                    Fail(key, $"Invalid or duplicate module name '{alias}' in '{spec}'.");
            return aliases;
        }

        string? nativeName = null, nativeSource = null; string[][] nativeModules = [];
        if (model.TryGetValue("native", out var nativeValue)) {
            if (nativeValue is not TomlTable native) { Fail("native", "[native] must be a table."); return null!; }
            section = "native";
            Keys(native, "native", external ? ["name", "modules", "source"] : ["name", "modules"]);
            nativeName = Text(native, "name") ?? name;
            if (!ValidNativeName(nativeName)) Fail("name", $"The native library name '{nativeName}' must use only a-z, 0-9 and _ (at most 64); set [native] name.");
            nativeModules = Texts(native, "modules").Select(spec => Aliases(spec, "modules")).ToArray();
            if (nativeModules.Length == 0) Fail("modules", "[native] modules must list the modules the native part registers, e.g. [\"Audio|音訊\"].");
            // Only 'aqua library build' needs the source folder; installed packages ship native/<target>/ binaries.
            if (Text(native, "source") is { } source) nativeSource = Relative(source, "source");
        }

        var modules = new List<LibraryModule>();
        if (model.TryGetValue("modules", out var modulesValue)) {
            if (modulesValue is not TomlTableArray tables) { Fail("modules", "Declare Aquarius-source modules as [[modules]] tables with names and source."); return null!; }
            foreach (var module in tables) {
                section = "modules";
                Keys(module, "modules", "names", "source");
                string[] aliases = Aliases(Text(module, "names", true)!, "names");
                string source = Relative(Text(module, "source", true)!, "source");
                if (!source.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) Fail("source", $"A module source must be an .aqua file: {source}");
                if (!File.Exists(Path.Combine(root, source))) Fail("source", $"Module source does not exist: {source}");
                modules.Add(new LibraryModule(aliases, source));
            }
        }
        section = "";
        if (nativeModules.Length == 0 && modules.Count == 0) Fail("library", "A library needs [native] modules, [[modules]] or both.");

        string[] permissions = [];
        if (model.TryGetValue("android", out var androidValue)) {
            if (androidValue is not TomlTable android) { Fail("android", "[android] must be a table."); return null!; }
            section = "android";
            Keys(android, "android", "permissions");
            permissions = Texts(android, "permissions");
            if (permissions.Any(p => !Regex.IsMatch(p, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"))) Fail("permissions", "Android permissions are names such as android.permission.INTERNET.");
        }
        return new Library(file, name, libraryVersion, Text(library, "description"), Text(library, "license"), platforms,
            nativeName, nativeModules, nativeSource, modules.ToArray(), permissions, external);
    }
}

/// <summary>A manifest that breaks the library manifest rules; <see cref="Key"/> names the offending key.</summary>
public sealed class LibraryManifestException(string file, string key, string message, string section = "") : IOException($"{file}: {message}")
{
    public string File { get; } = file;
    public string Key { get; } = key;
    /// <summary>The table holding <see cref="Key"/> (library, native, modules, android), or empty for top-level keys.</summary>
    public string Section { get; } = section;
    public string Reason { get; } = message;
}
