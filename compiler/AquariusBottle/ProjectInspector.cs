namespace AquariusLang.Bottle;

/// <summary>Read-only editor view. It never clones repositories, writes locks or populates the cache.</summary>
public static class ProjectInspector {
    public static IReadOnlyDictionary<string, ProjectManifest> Libraries(ProjectManifest project, BottleCache? cache = null, bool verify = true) {
        cache ??= new();
        string lockPath = Path.Combine(project.Root, ProjectManifest.LockName);
        if (File.Exists(lockPath)) {
            var saved = BottleCache.Read<BottleLock>(lockPath);
            if (saved.ManifestHash == project.ContentHash) {
                var resolved = new BottleManager(cache).ReadLocked(project, saved, verify: verify);
                return resolved.Packages.ToDictionary(p => p.Name, p => ProjectManifest.Load(cache.ContentRoot(p)), StringComparer.OrdinalIgnoreCase);
            }
        }
        var libraries = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(ProjectManifest owner, CachedBottle? cachedOwner = null) {
            if (active.Count >= 128 || libraries.Count >= 1000) throw new InvalidDataException("Dependency graph exceeds editor limits.");
            foreach (var dependency in owner.Dependencies.Values) {
                if (!active.Add(dependency.Name)) throw new InvalidDataException($"Dependency cycle involving {dependency.Name}.");
                try {
                    ProjectManifest library; CachedBottle? cached = null;
                    if (cachedOwner != null && cachedOwner.Dependencies.TryGetValue(dependency.Name, out var key)) { cached = cache.Get(key, verify); library = ProjectManifest.Load(cache.ContentRoot(cached)); }
                    else if (dependency.Path != null) library = ProjectManifest.Load(Path.GetFullPath(dependency.Path, owner.Root));
                    else {
                        cached = cache.List(dependency.Name, verify).Where(p => new VersionRequirement(dependency.Version).Matches(BottleVersion.Parse(p.Version))).OrderByDescending(p => BottleVersion.Parse(p.Version)).FirstOrDefault();
                        if (cached == null) throw new InvalidDataException($"Dependency {dependency.Name} is not available locally. Run 'aqua bottle restore'.");
                        library = ProjectManifest.Load(cache.ContentRoot(cached));
                    }
                    if (library.Type != "library" || !library.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase) || !new VersionRequirement(dependency.Version).Matches(BottleVersion.Parse(library.Version!))) throw new InvalidDataException($"Dependency {dependency.Name} does not satisfy '{dependency.Version}'.");
                    if (libraries.TryGetValue(library.Name, out var existing)) {
                        if (existing.Version != library.Version || existing.FilePath != library.FilePath) throw new InvalidDataException($"Conflicting dependency {library.Name}.");
                    } else { libraries.Add(library.Name, library); Walk(library, cached); }
                } finally { active.Remove(dependency.Name); }
            }
        }
        CachedBottle? ownerRecord = null;
        string record = Path.Combine(Path.GetDirectoryName(project.Root)!, "package.json");
        if (Path.GetFileName(project.Root) == "content" && File.Exists(record)) ownerRecord = BottleCache.Read<CachedBottle>(record);
        Walk(project, ownerRecord);
        foreach (var library in libraries.Values) if (project.Platforms.Except(library.Platforms).Any()) throw new InvalidDataException($"{library.Name} does not support all declared project platforms.");
        return libraries;
    }
    public static string? ResolveImport(string sourceFile, string import, Func<string, string?>? readText = null, BottleCache? cache = null) {
        if (!import.Replace('\\', '/').StartsWith("/bottles/", StringComparison.OrdinalIgnoreCase)) return null;
        string? manifestPath = ProjectManifest.Find(sourceFile); if (manifestPath == null) return null;
        var project = ProjectManifest.Parse(manifestPath, readText?.Invoke(manifestPath) ?? File.ReadAllText(manifestPath));
        string[] parts = import.Replace('\\', '/').TrimStart('/').Split('/', 3);
        if (parts.Length != 3) return null;
        // Diagnostics verify bytes on open/save/file changes; completion need only read metadata.
        var libraries = Libraries(project, cache, verify: false);
        if (!libraries.TryGetValue(parts[1], out var library)) {
            if (project.Type != "library" || !project.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) return null;
            library = project;
        }
        string path = SafeFiles.Under(library.Root, parts[2]);
        return library.SourceFiles().FirstOrDefault(source => source.Equals(path, StringComparison.OrdinalIgnoreCase));
    }
}
