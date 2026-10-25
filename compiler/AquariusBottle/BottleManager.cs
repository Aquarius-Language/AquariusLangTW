using Tomlyn;
using Tomlyn.Model;

namespace AquariusLang.Bottle;

public sealed record BottleLock(int Format, string ManifestHash, Dictionary<string, string> Direct, List<CachedBottle> Packages);
public sealed record ResolvedProject(ProjectManifest Manifest, IReadOnlyList<CachedBottle> Packages);

public sealed class BottleManager(BottleCache? cache = null) {
    public BottleCache Cache { get; } = cache ?? new();

    public ResolvedProject Restore(ProjectManifest project, string target = "wasm", bool update = false, bool locked = false, bool offline = false) {
        using var held = Cache.Acquire();
        return RestoreCore(project, target, update, locked, offline);
    }
    private ResolvedProject RestoreCore(ProjectManifest project, string target, bool update, bool locked, bool offline, bool persistLock = true, string? sourceBoundary = null) {
        project.CheckPlatform(target);
        string lockPath = Path.Combine(project.Root, ProjectManifest.LockName);
        string fingerprint = BottleCache.HashText(File.ReadAllText(project.FilePath));
        BottleLock? pins = null;
        if (!update && File.Exists(lockPath)) {
            var saved = BottleCache.Read<BottleLock>(lockPath);
            if (saved.Format != 1 || saved.Packages == null || saved.Direct == null || saved.Packages.Count > 1000) throw new InvalidDataException("Invalid or unsupported Bottle lock format.");
            if (saved.ManifestHash == fingerprint) {
                // A checked-in lock may be restored into an empty cache on another machine.
                bool available = saved.Packages.All(p => File.Exists(Path.Combine(Cache.PackageRoot(p.Key), "package.json")));
                if (available) return ReadLocked(project, saved, target);
                pins = saved;
            }
        }
        if (locked && pins == null) throw new InvalidDataException("The Bottle lock is missing or out of date. Run 'aqua bottle restore' and commit aqua.lock.json.");
        var selected = new Dictionary<string, CachedBottle>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CachedBottle Select(BottleDependency dependency, string parent, string? boundary = null) {
            if (visiting.Count >= 128 || selected.Count >= 1000) throw new InvalidDataException("Dependency graph exceeds the depth or package limit.");
            if (!visiting.Add(dependency.Name)) throw new InvalidDataException($"Dependency cycle: {string.Join(" -> ", visiting)} -> {dependency.Name}");
            try {
                var requirement = new VersionRequirement(dependency.Version);
                var pin = pins?.Packages.SingleOrDefault(p => p.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase));
                CachedBottle Accept(CachedBottle package, BottleDependency expected) {
                    if (!package.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) || !new VersionRequirement(expected.Version).Matches(BottleVersion.Parse(package.Version)))
                        throw new InvalidDataException($"Dependency {expected.Name} requires '{expected.Version}', but source provides {package.Name} {package.Version}.");
                    if (selected.TryGetValue(package.Name, out var previous) && (previous.Key != package.Key || previous.Hash != package.Hash))
                        throw new InvalidDataException($"Conflicting dependency {package.Name}: {previous.Version} from {previous.Source} and {package.Version} from {package.Source}. One version and source per name is required.");
                    var expectedPin = pins?.Packages.SingleOrDefault(p => p.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase));
                    if (expectedPin != null && (package.Key != expectedPin.Key || package.Hash != expectedPin.Hash)) throw new InvalidDataException($"Source does not match locked {expectedPin.Key}. Restore the original library source or run 'aqua bottle update' intentionally.");
                    selected[package.Name] = package; return package;
                }
                CachedBottle FromSource(string path, string source, string? repositoryRoot = null) {
                    var library = ProjectManifest.Load(path);
                    if (library.Type != "library" || !library.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase) || !requirement.Matches(BottleVersion.Parse(library.Version!)))
                        throw new InvalidDataException($"Dependency {dependency.Name} requires a library matching '{dependency.Version}'; found {library.Name} {library.Version} ({library.Type}).");
                    var children = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var child in library.Dependencies.Values) children[child.Name] = Select(child, library.Root, repositoryRoot ?? boundary).Key;
                    return Accept(Cache.Store(library, source, children), dependency);
                }
                if (dependency.Path != null) {
                    string path = ProjectManifest.Resolve(Path.GetFullPath(dependency.Path, parent));
                    // A downloaded manifest may reference siblings in its repository, but
                    // cannot read arbitrary projects elsewhere on the installing machine.
                    if (boundary != null) SafeFiles.Check(boundary, path);
                    if (File.Exists(path)) return FromSource(path, "path:" + path);
                    // A copied lock restores without the original source tree. Without a lock,
                    // only an exact original source identity may satisfy an unavailable path.
                    var cachedPath = Cache.List(dependency.Name).Where(p => (pin != null ? p.Key == pin.Key && p.Hash == pin.Hash : p.Source == "path:" + path) && requirement.Matches(BottleVersion.Parse(p.Version))).OrderByDescending(p => BottleVersion.Parse(p.Version)).FirstOrDefault();
                    if (cachedPath != null) return FromCache(cachedPath, dependency);
                    throw new IOException($"Dependency source not found: {path}");
                }
                if (dependency.Git != null) {
                    if (offline) {
                        var cachedGit = Cache.List(dependency.Name).Where(p => p.Source.StartsWith("git:" + dependency.Git.TrimEnd('/') + "#", StringComparison.Ordinal) && requirement.Matches(BottleVersion.Parse(p.Version))).OrderByDescending(p => BottleVersion.Parse(p.Version)).FirstOrDefault();
                        if (cachedGit == null || dependency.Rev != null) throw new InvalidDataException($"Offline Git dependencies with a revision require a valid lock: {dependency.Name}.");
                        return FromCache(cachedGit, dependency);
                    }
                    string? revision = dependency.Rev;
                    if (pin?.Source.StartsWith("git:", StringComparison.Ordinal) == true) revision = pin.Source[(pin.Source.IndexOf('#') + 1)..].Split(':')[0];
                    return GitSource.WithCheckout(dependency.Git, revision, dependency.Subdirectory, (path, source, repositoryRoot) => FromSource(path, source, repositoryRoot));
                }
                var candidate = Cache.List(dependency.Name).Where(p => (pin == null || p.Key == pin.Key && p.Hash == pin.Hash) && requirement.Matches(BottleVersion.Parse(p.Version))).OrderByDescending(p => BottleVersion.Parse(p.Version)).FirstOrDefault();
                if (candidate == null) throw new InvalidDataException($"No cached {dependency.Name} matches '{dependency.Version}'. Install it from a local path or GitHub URL first.");
                return FromCache(candidate, dependency);

                CachedBottle FromCache(CachedBottle package, BottleDependency expected) {
                    Accept(package, expected);
                    var manifest = ProjectManifest.Load(Cache.ContentRoot(package));
                    foreach (var child in manifest.Dependencies.Values) {
                        if (!package.Dependencies.TryGetValue(child.Name, out string? key)) throw new InvalidDataException($"Missing cached dependency {child.Name} in {package.Key}.");
                        var childPin = pins?.Packages.SingleOrDefault(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase));
                        var childRequirement = new VersionRequirement(child.Version);
                        var cachedChild = childPin != null ? Cache.Get(childPin.Key) :
                            selected.TryGetValue(child.Name, out var chosen) && childRequirement.Matches(BottleVersion.Parse(chosen.Version)) ? chosen :
                            Cache.List(child.Name).Where(p => childRequirement.Matches(BottleVersion.Parse(p.Version))).OrderByDescending(p => BottleVersion.Parse(p.Version)).FirstOrDefault() ?? Cache.Get(key);
                        if (!visiting.Add(child.Name)) throw new InvalidDataException($"Dependency cycle involving {child.Name}.");
                        try {
                            if (!new VersionRequirement(child.Version).Matches(BottleVersion.Parse(cachedChild.Version))) throw new InvalidDataException($"Cached dependency constraint mismatch: {child.Name}.");
                            FromCache(cachedChild, child);
                            package.Dependencies[child.Name] = cachedChild.Key;
                        } finally { visiting.Remove(child.Name); }
                    }
                    return package;
                }
            } finally { visiting.Remove(dependency.Name); }
        }
        foreach (var dependency in project.Dependencies.Values) direct[dependency.Name] = Select(dependency, project.Root, sourceBoundary).Key;
        var packages = selected.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var result = new ResolvedProject(project, packages); ValidatePlatforms(result, target, Cache);
        if (pins != null) return ReadLocked(project, pins, target);
        if (persistLock) BottleCache.Write(lockPath, new BottleLock(1, fingerprint, direct, packages));
        return result;
    }

    public ResolvedProject ReadLocked(ProjectManifest project, BottleLock saved, string target = "wasm", bool verify = true) {
        if (saved.Format != 1 || saved.Direct == null || saved.Packages == null || saved.Packages.Count > 1000) throw new InvalidDataException("Invalid Bottle lock.");
        var packages = new Dictionary<string, CachedBottle>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in saved.Packages) {
            var actual = Cache.Get(pin.Key, verify);
            if (actual.Hash != pin.Hash || pin.Dependencies == null) throw new InvalidDataException($"Lock/cache mismatch for {pin.Key}.");
            actual = actual with { Dependencies = new Dictionary<string, string>(pin.Dependencies, StringComparer.OrdinalIgnoreCase) };
            if (!packages.TryAdd(actual.Name, actual)) throw new InvalidDataException($"Duplicate locked library: {actual.Name}");
        }
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(ProjectManifest owner, IReadOnlyDictionary<string, string> children) {
            if (active.Count >= 128) throw new InvalidDataException("Locked dependency graph exceeds depth limit.");
            if (owner.Dependencies.Count != children.Count) throw new InvalidDataException($"Lock dependency count mismatch for {owner.Name}.");
            foreach (var dependency in owner.Dependencies.Values) {
                if (!children.TryGetValue(dependency.Name, out var key) || !packages.TryGetValue(dependency.Name, out var package) || package.Key != key || !new VersionRequirement(dependency.Version).Matches(BottleVersion.Parse(package.Version)))
                    throw new InvalidDataException($"Lock does not satisfy dependency {dependency.Name} '{dependency.Version}'.");
                if (!active.Add(package.Name)) throw new InvalidDataException($"Dependency cycle involving {package.Name}.");
                if (reached.Add(package.Name)) Walk(ProjectManifest.Load(Cache.ContentRoot(package)), package.Dependencies);
                active.Remove(package.Name);
            }
        }
        Walk(project, saved.Direct);
        if (reached.Count != packages.Count) throw new InvalidDataException("Lock contains unreachable libraries.");
        var result = new ResolvedProject(project, packages.Values.ToArray()); ValidatePlatforms(result, target, Cache); return result;
    }
    internal static void ValidatePlatforms(ResolvedProject project, string target, BottleCache? cache = null) {
        cache ??= new(); project.Manifest.CheckPlatform(target);
        foreach (var package in project.Packages) {
            var manifest = ProjectManifest.Load(cache.ContentRoot(package));
            manifest.CheckPlatform(target);
            if (target == "wasm" && project.Manifest.Platforms.Except(manifest.Platforms).Any()) throw new InvalidDataException($"{manifest.Name} does not support all declared project platforms. Build for a specific --target or narrow project.platforms.");
        }
    }
    public void Install(string source, string projectPath, string? version = null, string? revision = null, string? subdirectory = null, bool global = false) {
        using var held = Cache.Acquire();
        void InstallFrom(string path, string identity, string? git, string? repositoryRoot = null) {
            var library = ProjectManifest.Load(path);
            if (library.Type != "library") throw new InvalidDataException("Only library projects can be installed.");
            string requirement = version ?? library.Version!;
            if (!new VersionRequirement(requirement).Matches(BottleVersion.Parse(library.Version!))) throw new InvalidDataException($"Library {library.Name} {library.Version} does not match '{requirement}'.");
            // Resolve the library's complete dependency tree before publishing the parent.
            var resolution = RestoreCore(library, "wasm", false, false, false, false, repositoryRoot);
            var children = library.Dependencies.Keys.ToDictionary(n => n, n => resolution.Packages.Single(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Key, StringComparer.OrdinalIgnoreCase);
            Cache.Store(library, identity, children);
            if (global) return;
            var project = ProjectManifest.Load(projectPath);
            if (project.Name.Equals(library.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A project cannot depend on itself.");
            var dependencies = project.Model.TryGetValue("dependencies", out var value) ? (TomlTable)value : new TomlTable();
            foreach (string key in dependencies.Keys.Where(k => k.Equals(library.Name, StringComparison.OrdinalIgnoreCase)).ToArray()) dependencies.Remove(key);
            var spec = new TomlTable { ["version"] = requirement };
            if (git != null) { spec["git"] = git; if (revision != null) spec["rev"] = revision; if (subdirectory != null) spec["subdirectory"] = subdirectory; }
            else spec["path"] = Path.GetRelativePath(project.Root, ProjectManifest.Resolve(source)).Replace('\\', '/');
            dependencies[library.Name] = spec; project.Model["dependencies"] = dependencies;
            SaveAndRestore(project, () => RestoreCore(ProjectManifest.Load(project.FilePath), "wasm", false, false, false));
        }
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) GitSource.WithCheckout(source, revision, subdirectory, (path, identity, repositoryRoot) => { InstallFrom(path, identity, source, repositoryRoot); return 0; });
        else {
            if (revision != null || subdirectory != null) throw new ArgumentException("--rev and --subdirectory require a GitHub source.");
            string path = ProjectManifest.Resolve(source); InstallFrom(path, "path:" + path, null);
        }
    }
    public void Uninstall(string name, string projectPath) {
        using var held = Cache.Acquire(); var project = ProjectManifest.Load(projectPath);
        string lockPath = Path.Combine(project.Root, ProjectManifest.LockName);
        BottleLock? previous = File.Exists(lockPath) ? BottleCache.Read<BottleLock>(lockPath) : null;
        if (previous != null && previous.ManifestHash == project.ContentHash) ReadLocked(project, previous);
        else previous = null;
        if (!project.Model.TryGetValue("dependencies", out var value) || value is not TomlTable dependencies) throw new InvalidDataException($"No direct dependency named {name}.");
        string? key = dependencies.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key == null) throw new InvalidDataException($"No direct dependency named {name}.");
        dependencies.Remove(key);
        SaveAndRestore(project, () => {
            var updated = ProjectManifest.Load(project.FilePath);
            if (previous == null) { RestoreCore(updated, "wasm", false, false, true); return; }
            var remaining = previous.Packages.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string packageKey) { if (reached.Add(packageKey)) foreach (string child in remaining[packageKey].Dependencies.Values) Visit(child); }
            var direct = previous.Direct.Where(d => !d.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToDictionary(d => d.Key, d => d.Value);
            foreach (string packageKey in direct.Values) Visit(packageKey);
            var pruned = new BottleLock(1, updated.ContentHash, direct, previous.Packages.Where(p => reached.Contains(p.Key)).ToList());
            ReadLocked(updated, pruned); BottleCache.Write(lockPath, pruned);
        });
    }
    private static void SaveAndRestore(ProjectManifest project, Action restore) {
        string original = File.ReadAllText(project.FilePath); string lockPath = Path.Combine(project.Root, ProjectManifest.LockName); string? oldLock = File.Exists(lockPath) ? File.ReadAllText(lockPath) : null;
        string updated = Toml.FromModel(project.Model); ProjectManifest.Parse(project.FilePath, updated);
        try { BottleCache.AtomicText(project.FilePath, updated); restore(); }
        catch {
            BottleCache.AtomicText(project.FilePath, original);
            if (oldLock != null) BottleCache.AtomicText(lockPath, oldLock); else if (File.Exists(lockPath)) File.Delete(lockPath);
            throw;
        }
    }
}
