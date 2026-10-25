using AquariusLang.Libraries;
using AquariusLang.Packaging;

namespace AquariusLang.Bottle;

/// <summary>A compiled project: the libraries it was linked with (default ones plus its external libraries), which
/// build targets and <c>aqua run</c> use to ship and load native parts.</summary>
public sealed record ProjectBuild(ProjectManifest Project, LibrarySet Libraries);

public static class ProjectBuilder {
    /// <summary>Stages the project's sources, its version 1 libraries (under bottles/NAME/) and every library's assets,
    /// then compiles them with the project's version 2 libraries as external libraries.</summary>
    public static ProjectBuild Compile(string projectPath, string output, string target = "wasm", bool locked = false, bool offline = false, BottleCache? cache = null) {
        var project = ProjectManifest.Load(projectPath);
        if (project.IsModuleLibrary) throw new InvalidDataException($"{project.Name} is a library. Build a program that imports it, such as one of its tests: aqua build {Path.Combine(project.Root, "tests", "<case>.aqua")}");
        var manager = new BottleManager(cache); var resolved = manager.Restore(project, target, locked: locked, offline: offline);
        var packages = resolved.Packages.Select(p => ProjectManifest.Load(manager.Cache.ContentRoot(p))).ToArray();
        var libraries = Libraries(packages);
        string stage = Path.Combine(Path.GetTempPath(), "aquarius-project-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>(); var assets = new List<string>(); long size = 0;
            void Copy(string input, string name, bool source) {
                SafeFiles.Relative(name);
                if (!source && name.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Asset destinations cannot have an .aqua extension: {name}");
                if (!names.Add(name)) throw new InvalidDataException($"Conflicting application path: {name}. Give assets distinct targets.");
                if (names.Count > WasmApplication.MaxEntries || (size = checked(size + new FileInfo(input).Length)) > WasmApplication.MaxPackageBytes) throw new InvalidDataException("Project exceeds application package limits.");
                string destination = SafeFiles.Under(stage, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(input, destination);
                (source ? sources : assets).Add(destination);
            }
            void AddProject(ProjectManifest manifest, string prefix) {
                // Version 2 libraries' Aquarius modules are bundled by the compiler when imported; only their assets are staged.
                if (!manifest.IsModuleLibrary)
                    foreach (string source in manifest.SourceFiles()) Copy(source, prefix + Path.GetRelativePath(manifest.Root, source).Replace('\\', '/'), true);
                foreach (var asset in manifest.Assets) {
                    if (asset.Platforms.Count > 0 && target == "wasm") throw new InvalidDataException($"Platform-specific assets in {manifest.Name} require --platform for Wasm output or an explicit --target (web/windows/linux/android).");
                    if (asset.Platforms.Count > 0 && !asset.Platforms.Contains(target)) continue;
                    string input = SafeFiles.Under(manifest.Root, asset.Source);
                    bool directory = Directory.Exists(input);
                    if (!directory && !File.Exists(input)) throw new InvalidDataException($"Asset does not exist: {input}");
                    foreach (string file in directory ? SafeFiles.Enumerate(input) : [input]) {
                        string name = asset.Target ?? prefix + asset.Source;
                        if (directory) name += "/" + Path.GetRelativePath(input, file).Replace('\\', '/');
                        if (file.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"An .aqua source cannot be declared as an asset: {file}");
                        SafeFiles.Check(manifest.Root, file); Copy(file, name, false);
                    }
                }
            }
            // /bottles is reserved for dependencies and cannot be shadowed by application sources.
            if (project.SourceFiles().Any(f => Path.GetRelativePath(project.Root, f).Replace('\\', '/').StartsWith("bottles/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The 'bottles' directory is reserved for installed libraries.");
            AddProject(project, "");
            foreach (var library in packages) AddProject(library, "bottles/" + library.Name + "/");
            IEnumerable<string> platforms = project.Platforms.Count == 0 ? ProjectManifest.KnownPlatforms : project.Platforms;
            foreach (var library in packages) platforms = platforms.Intersect(library.Platforms);
            string[] supported = target == "wasm" ? platforms.Order(StringComparer.Ordinal).ToArray() : [target];
            if (supported.Length == 0) throw new InvalidDataException("Project and dependencies have no common supported platform.");
            // Declared libraries name default libraries or dependencies (their library names).
            var declared = project.Libraries.Select(name => packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.IsModuleLibrary)?.Name ?? name);
            WasmApplication.Compile(sources, output, stage, assets, project.Entry, supported, libraries, declared);
            return new ProjectBuild(project, libraries);
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    /// <summary>The default libraries plus the version 2 libraries among a project's resolved packages.</summary>
    public static LibrarySet Libraries(IEnumerable<ProjectManifest> packages) =>
        LibrarySet.Default.With(packages.Where(p => p.IsModuleLibrary).Select(p => p.Library!));

    /// <summary>The libraries a project links with, from its lock and the cache, without restoring or downloading.</summary>
    public static LibrarySet Inspect(ProjectManifest project, BottleCache? cache = null) {
        cache ??= new();
        var packages = ProjectInspector.Libraries(project, cache, verify: false).Values;
        return project.IsModuleLibrary ? LibrarySet.Default.With(packages.Where(p => p.IsModuleLibrary).Select(p => p.Library!).Prepend(project.Library!).DistinctBy(l => l.Name)) : Libraries(packages);
    }
}
