using System.Text.RegularExpressions;
using AquariusLang.Libraries;
using AquariusLang.Packaging;
using Tomlyn;
using Tomlyn.Model;

namespace AquariusLang.Bottle;

public sealed record ManifestIssue(string Message, int Offset = 0, int Length = 1);
public sealed class ManifestException(string path, IReadOnlyList<ManifestIssue> issues)
    : IOException($"{path}: {string.Join("; ", issues.Select(i => i.Message))}") {
    public IReadOnlyList<ManifestIssue> Issues { get; } = issues;
}
public sealed record BottleDependency(string Name, string Version, string? Path = null, string? Git = null, string? Rev = null, string? Subdirectory = null);
public sealed record BottleAsset(string Source, string? Target, IReadOnlyList<string> Platforms);
/// <summary>[onnx] build options. Cuda selects the runtime pack built with ONNX Runtime's CUDA build for windows/linux
/// executables; other targets are unaffected. Only the root project's settings apply, never a dependency's.</summary>
public sealed record OnnxSettings(bool Cuda = false) { public static OnnxSettings Default { get; } = new(); }
/// <summary>[desktop] build options for the root project's executable exports.</summary>
public sealed record DesktopSettings(bool HideTerminal = false) { public static DesktopSettings Default { get; } = new(); }

/// <summary>
/// The aqua.toml of a project or library, shared by the compiler and the editor. Parsing never fetches or executes
/// dependencies.
/// <list type="bullet">
/// <item>manifest_version = 2 (current): <c>[project]</c> is an application; <c>[library]</c> a library with
/// <c>[native]</c> (C/C++ part), <c>[[modules]]</c> (Aquarius-source modules imported by name) and <c>[android]</c>
/// tables, the same shape as the default libraries' manifests (<see cref="Library"/>).</item>
/// <item>manifest_version = 1 (still read): <c>[project]</c> with type = "executable" or "library"; library sources are
/// imported by path (<c>/bottles/NAME/main.aqua</c>).</item>
/// </list>
/// Both have <c>[dependencies]</c>, <c>[[assets]]</c> and <c>[tool]</c>; projects also <c>[desktop]</c> and <c>[onnx]</c>.
/// </summary>
public sealed class ProjectManifest {
    public const string FileName = "aqua.toml";
    public const string LockName = "aqua.lock.json";
    public static readonly string[] KnownPlatforms = Library.KnownPlatforms;
    public string FilePath { get; }
    public string ContentHash { get; }
    public string Root => System.IO.Path.GetDirectoryName(FilePath)!;
    public int ManifestVersion { get; }
    public string Name { get; }
    /// <summary>"executable" or "library".</summary>
    public string Type { get; }
    public string? Version { get; }
    public string Entry { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<string> Platforms { get; }
    /// <summary>Libraries the application declares for imports by computed names: default library names or dependency names.</summary>
    public IReadOnlyList<string> Libraries { get; } = [];
    public IReadOnlyList<BottleAsset> Assets { get; } = [];
    public IReadOnlyDictionary<string, BottleDependency> Dependencies { get; } = new Dictionary<string, BottleDependency>();
    public OnnxSettings Onnx { get; } = OnnxSettings.Default;
    public DesktopSettings Desktop { get; } = DesktopSettings.Default;
    /// <summary>The library tables of a manifest_version 2 library; null for projects and version 1 libraries.</summary>
    public Library? Library { get; }
    /// <summary>A version 2 library: its Aquarius modules are imported by name, never staged under /bottles.</summary>
    public bool IsModuleLibrary => Library != null;
    internal TomlTable Model { get; }

    private ProjectManifest(string file, string text, TomlTable model) {
        FilePath = System.IO.Path.GetFullPath(file); Model = model; ContentHash = BottleCache.HashText(text);
        void Fail(string key, string message) => throw new ManifestException(file, [Locate(text, key, message)]);
        void Keys(TomlTable table, string section, params string[] keys) {
            foreach (string key in table.Keys) if (!keys.Contains(key)) Fail(key, $"Unknown {section} key '{key}'.");
        }
        string? String(TomlTable table, string key, bool required = false) {
            if (!table.TryGetValue(key, out var value)) { if (required) Fail(key, $"Missing required '{key}'."); return null; }
            if (value is not string result || string.IsNullOrWhiteSpace(result)) { Fail(key, $"'{key}' must be a nonempty string."); return null; }
            return result;
        }
        string[] Strings(TomlTable table, string key, string[] defaults) {
            if (!table.TryGetValue(key, out var value)) return defaults;
            if (value is not TomlArray array || array.Any(v => v is not string s || string.IsNullOrWhiteSpace(s))) { Fail(key, $"'{key}' must be an array of nonempty strings."); return []; }
            return array.Cast<string>().ToArray();
        }
        string[] PlatformList(TomlTable table, string key, bool required) {
            string[] platforms = Strings(table, key, [] ).Select(p => p.ToLowerInvariant()).ToArray();
            if ((required && platforms.Length == 0) || platforms.Distinct().Count() != platforms.Length || platforms.Any(p => !KnownPlatforms.Contains(p)))
                Fail(key, $"'{key}' must list supported platforms: {string.Join(", ", KnownPlatforms)}.");
            return platforms;
        }
        string Relative(string value, string key) {
            try { SafeFiles.Relative(value); } catch (InvalidDataException e) { Fail(key, e.Message); }
            return value.Replace('\\', '/');
        }
        long manifestVersion = model.TryGetValue("manifest_version", out var schema) ? schema as long? ?? 0 : 1;
        if (manifestVersion is not (1 or 2)) Fail("manifest_version", "manifest_version must be 1 or 2.");
        ManifestVersion = (int)manifestVersion;
        string[] shared = ["manifest_version", "dependencies", "assets", "tool"];
        if (ManifestVersion == 2 && model.ContainsKey("library")) {
            // A library: its tables follow the default libraries' manifest rules.
            try { Library = Library.FromModel(file, model, true, "dependencies", "assets", "tool"); }
            catch (LibraryManifestException e) { throw new ManifestException(file, [Locate(text, e.Key, e.Reason, e.Section)]); }
            Name = Library!.Name; Type = "library"; Version = Library.Version; Platforms = Library.Platforms;
            Entry = Library.Modules.FirstOrDefault()?.Source ?? "";
            Sources = Library.Modules.Select(m => m.Source).ToArray();
        } else {
            Keys(model, "manifest", [.. shared, "project", "onnx", "desktop"]);
            if (!model.TryGetValue("project", out var projectValue) || projectValue is not TomlTable) Fail("project", ManifestVersion == 2 ? "Missing [project] or [library] table." : "Missing [project] table.");
            var project = (TomlTable)model["project"];
            if (ManifestVersion == 1) Keys(project, "project", "name", "type", "version", "entry", "sources", "platforms", "description", "license", "libraries");
            else Keys(project, "project", "name", "version", "entry", "sources", "platforms", "description", "license", "libraries");
            Name = String(project, "name", true)!;
            if (!ValidName(Name)) Fail("name", "Project names must start with a letter or digit and contain only letters, digits, underscores or hyphens; whitespace is forbidden.");
            Type = ManifestVersion == 1 ? String(project, "type", true)! : "executable";
            if (Type is not ("library" or "executable")) Fail("type", "Project type must be 'executable' or 'library'.");
            Version = String(project, "version", Type == "library");
            try { if (Version != null) BottleVersion.Parse(Version); } catch (InvalidDataException e) { Fail("version", e.Message); }
            Entry = Relative(String(project, "entry") ?? "main.aqua", "entry");
            if (!Entry.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) Fail("entry", "Entry must name an .aqua file.");
            Sources = Strings(project, "sources", ["."]).Select(s => s == "." ? s : Relative(s, "sources")).ToArray();
            if (Sources.Count == 0) Fail("sources", "At least one source path is required.");
            Platforms = PlatformList(project, "platforms", Type == "library");
            Libraries = Strings(project, "libraries", []);
            if (Libraries.Any(l => !ValidName(l)) || Libraries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Libraries.Count)
                Fail("libraries", "'libraries' lists default library names or dependency names, each once.");
        }
        var dependencies = new Dictionary<string, BottleDependency>(StringComparer.OrdinalIgnoreCase);
        if (model.TryGetValue("dependencies", out var dependencyValue)) {
            if (dependencyValue is not TomlTable) Fail("dependencies", "[dependencies] must be a table.");
            foreach (var pair in (TomlTable)dependencyValue) {
                if (!ValidName(pair.Key) || pair.Key.Equals(Name, StringComparison.OrdinalIgnoreCase)) Fail(pair.Key, $"Invalid or self dependency '{pair.Key}'.");
                string constraint; string? path = null, git = null, rev = null, subdirectory = null;
                if (pair.Value is string version) constraint = version;
                else if (pair.Value is TomlTable spec) {
                    Keys(spec, "dependency", "version", "path", "git", "rev", "subdirectory");
                    constraint = String(spec, "version", true)!; path = String(spec, "path"); git = String(spec, "git"); rev = String(spec, "rev"); subdirectory = String(spec, "subdirectory");
                    if (path != null && git != null) Fail(pair.Key, "Choose either path or git for a dependency.");
                    if (git == null && (rev != null || subdirectory != null)) Fail(pair.Key, "rev and subdirectory require git.");
                    if (subdirectory != null) Relative(subdirectory, "subdirectory");
                    if (git != null) try { GitSource.Validate(git, rev); } catch (InvalidDataException e) { Fail(pair.Key, e.Message); }
                } else { throw new ManifestException(file, [Locate(text, pair.Key, "Dependency must be a version string or inline table.")]); }
                try { _ = new VersionRequirement(constraint); } catch (InvalidDataException e) { Fail(pair.Key, e.Message); }
                if (!dependencies.TryAdd(pair.Key, new(pair.Key, constraint, path, git, rev, subdirectory))) Fail(pair.Key, "Duplicate dependency name (names are case insensitive).");
            }
        }
        Dependencies = dependencies;
        var assets = new List<BottleAsset>();
        if (model.TryGetValue("assets", out var assetValue)) {
            if (assetValue is TomlArray list && list.All(v => v is string))
                assets.AddRange(list.Cast<string>().Select(s => new BottleAsset(Relative(s, "assets"), null, [])));
            else if (assetValue is TomlTableArray tables) foreach (var asset in tables) {
                Keys(asset, "asset", "source", "target", "platforms");
                string source = Relative(String(asset, "source", true)!, "source");
                string? target = String(asset, "target"); if (target != null) target = Relative(target, "target");
                assets.Add(new(source, target, PlatformList(asset, "platforms", false)));
            } else Fail("assets", "Use assets = ['relative/path'] before [project], or [[assets]] tables with source, optional target and platforms.");
        }
        Assets = assets;
        if (model.TryGetValue("desktop", out var desktopValue)) {
            if (desktopValue is not TomlTable desktop) { Fail("desktop", "[desktop] must be a table, e.g. [desktop] hide_terminal = true."); return; }
            Keys(desktop, "desktop", "hide_terminal");
            bool hideTerminal = false;
            if (desktop.TryGetValue("hide_terminal", out var hideValue)) { if (hideValue is bool flag) hideTerminal = flag; else Fail("hide_terminal", "'hide_terminal' must be true or false."); }
            Desktop = new(hideTerminal);
        }
        if (model.TryGetValue("onnx", out var onnxValue)) {
            if (onnxValue is not TomlTable onnx) { Fail("onnx", "[onnx] must be a table, e.g. [onnx] cuda = true."); return; }
            Keys(onnx, "onnx", "cuda");
            bool cuda = false;
            if (onnx.TryGetValue("cuda", out var cudaValue)) { if (cudaValue is bool flag) cuda = flag; else Fail("cuda", "'cuda' must be true or false."); }
            Onnx = new(cuda);
        }
    }
    public static bool ValidName(string name) => Library.ValidName(name);
    public static string Resolve(string path) => System.IO.Path.GetFullPath(Directory.Exists(path) ? System.IO.Path.Combine(path, FileName) : path);
    public static ProjectManifest Load(string path) { string file = Resolve(path); return Parse(file, File.ReadAllText(file)); }
    public static ProjectManifest Parse(string file, string text) {
        if (text.Length > 1024 * 1024) throw new ManifestException(file, [new("Manifest exceeds the 1 MiB limit.")]);
        var syntax = Toml.Parse(text, file);
        if (syntax.HasErrors) throw new ManifestException(file, syntax.Diagnostics.Select(d => new ManifestIssue(d.Message, d.Span.Offset, Math.Max(1, d.Span.Length))).ToArray());
        return new ProjectManifest(file, text, syntax.ToModel());
    }
    public static string? Find(string path) {
        for (string? directory = Directory.Exists(path) ? System.IO.Path.GetFullPath(path) : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); directory != null; directory = System.IO.Path.GetDirectoryName(directory)) {
            string candidate = System.IO.Path.Combine(directory, FileName); if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    /// <summary>Whether a manifest describes a version 2 library ([library] table). Sources inside a library folder (its
    /// tests, for example) compile as plain programs that may import the library, not as a project.</summary>
    public static bool IsLibraryManifest(string file) {
        try {
            var syntax = Toml.Parse(File.ReadAllText(file), file);
            if (syntax.HasErrors) return false;
            var model = syntax.ToModel();
            return model.ContainsKey("library") && model.TryGetValue("manifest_version", out var version) && version is 2L;
        } catch (IOException) { return false; }
    }
    /// <summary>Where a key is: inside its table when <paramref name="section"/> names one, else its first assignment.</summary>
    public static ManifestIssue Locate(string text, string key, string message, string section = "") {
        var header = section.Length > 0 ? Regex.Match(text, @"(?m)^[ \t]*\[\[?" + Regex.Escape(section) + @"\]\]?[ \t]*\r?$") : Match.Empty;
        if (header.Success && key.Length > 0) {
            int start = header.Index + header.Length;
            var next = Regex.Match(text[start..], @"(?m)^[ \t]*\[");
            string body = text[start..(next.Success ? start + next.Index : text.Length)];
            var inside = Regex.Match(body, @"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=");
            if (inside.Success) return new(message, start + inside.Index + inside.Value.IndexOf(key, StringComparison.Ordinal), key.Length);
        }
        var match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=");
        if (!match.Success && key.Length > 0) match = Regex.Match(text, @"(?m)^\s*\[\[?" + Regex.Escape(key) + @"\]\]?");
        int offset = match.Success ? match.Index + match.Value.IndexOf(key, StringComparison.Ordinal) : 0;
        return new(message, offset, Math.Max(1, match.Success ? key.Length : 1));
    }
    /// <summary>The Aquarius sources of the project (version 2 libraries: every .aqua file of the library folder outside
    /// tests/, which its modules may import relatively).</summary>
    public IReadOnlyList<string> SourceFiles() {
        if (IsModuleLibrary)
            return SafeFiles.Enumerate(Root, true, skipTests: true).Where(f => f.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))
                .Concat(Library!.Modules.Select(m => Library.SourcePath(m.Source))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var paths = Sources.SelectMany(s => {
            string path = s == "." ? Root : SafeFiles.Under(Root, s);
            return Directory.Exists(path) ? SafeFiles.Enumerate(path, true).Where(f => f.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) : [path];
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
        string entry = SafeFiles.Under(Root, Entry);
        if (!File.Exists(entry)) throw new InvalidDataException($"Entry does not exist: {entry}");
        if (!paths.Contains(entry, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"Entry '{Entry}' must be included in project.sources.");
        foreach (string path in paths) { SafeFiles.Check(Root, path); if (!File.Exists(path) || !path.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source must be an existing .aqua file: {path}"); }
        return paths;
    }
    /// <summary>A version 2 library's prebuilt native binaries (native/&lt;target&gt;/&lt;file&gt;) for every target.</summary>
    public IReadOnlyList<string> NativeBinaries() => Library is { NativeName: { } native }
        ? AquariusLang.Compiler.AquariusInstallation.Targets.Select(t => System.IO.Path.Combine(Root, "native", t, Library.NativeFile(native, t))).Where(File.Exists).ToList()
        : [];
    public void CheckPlatform(string target) {
        if (target != "wasm" && Platforms.Count > 0 && !Platforms.Contains(target)) throw new InvalidDataException($"{Name} does not support {target} (supports {string.Join(", ", Platforms)}).");
    }
}

internal static class SafeFiles {
    public static string Relative(string path) { WasmApplication.ValidatePath(path.Replace('\\', '/')); return path; }
    public static string Under(string root, string relative) { Relative(relative); string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative)); Check(root, path); return path; }
    public static void Check(string root, string path) {
        root = System.IO.Path.GetFullPath(root); path = System.IO.Path.GetFullPath(path);
        string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative != ".") Relative(relative);
        for (string? current = path; current != null; current = System.IO.Path.GetDirectoryName(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Bottle inputs must not follow symbolic links: {current}");
        }
    }
    public static IEnumerable<string> Enumerate(string path, bool skipProjects = false, bool skipTests = false) {
        Check(path, path);
        foreach (string file in Directory.EnumerateFiles(path).Order(StringComparer.Ordinal)) { Check(path, file); yield return file; }
        foreach (string directory in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal)) {
            string name = System.IO.Path.GetFileName(directory);
            if (skipProjects && new[] { ".git", ".idea", ".aqua", "bin", "obj", "node_modules", "dist", "bottles" }.Contains(name)) continue;
            if (skipTests && name is "tests" or "native" or "third_party") continue;
            if (skipProjects && File.Exists(System.IO.Path.Combine(directory, ProjectManifest.FileName))) continue;
            foreach (string file in Enumerate(directory, skipProjects, skipTests)) yield return file;
        }
    }
}
