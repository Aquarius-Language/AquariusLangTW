using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AquariusLang.Packaging;

namespace AquariusLang.Bottle;

public sealed record CachedBottle(string Name, string Version, string Hash, string Source, Dictionary<string, string> Dependencies) {
    public string Key => Name.ToLowerInvariant() + "/" + Version;
}

/// <summary>Immutable per-user source/asset snapshots. Resolution pins belong to individual project locks.</summary>
public sealed class BottleCache {
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string Root { get; }
    public BottleCache(string? root = null) {
        Root = Path.GetFullPath(root ?? System.Environment.GetEnvironmentVariable("AQUA_BOTTLE_HOME") ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".aquarius", "bottles"));
    }
    public string PackageRoot(string key) {
        string[] parts = key.Split('/');
        if (parts.Length != 2 || !ProjectManifest.ValidName(parts[0])) throw new InvalidDataException("Invalid Bottle cache key.");
        BottleVersion.Parse(parts[1]); return SafeFiles.Under(Root, "packages/" + key);
    }
    public string ContentRoot(CachedBottle package) => Path.Combine(PackageRoot(package.Key), "content");
    public IDisposable Acquire() {
        Directory.CreateDirectory(Root); SafeFiles.Check(Root, Root);
        try { return new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another Bottle command is using the cache. Retry when it finishes.", e); }
    }
    public CachedBottle Get(string key, bool verify = true) {
        string directory = PackageRoot(key);
        var package = Read<CachedBottle>(Path.Combine(directory, "package.json"));
        if (string.IsNullOrEmpty(package.Name) || package.Version == null || package.Hash == null || package.Source == null || package.Dependencies == null || !ProjectManifest.ValidName(package.Name) || package.Hash.Length != 64 || package.Key != key) throw new InvalidDataException($"Invalid cache record: {key}");
        if (verify && HashDirectory(ContentRoot(package)) != package.Hash) throw new InvalidDataException($"Cached library {key} failed integrity verification. Remove that cache version and reinstall from a trusted source.");
        var manifest = ProjectManifest.Load(ContentRoot(package));
        if (!manifest.Name.Equals(package.Name, StringComparison.Ordinal) || manifest.Type != "library" || manifest.Version != package.Version) throw new InvalidDataException($"Cache manifest mismatch: {key}");
        if (!manifest.Dependencies.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(package.Dependencies.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"Cache dependency mismatch: {key}");
        return package;
    }
    public IEnumerable<CachedBottle> List(string? name = null, bool verify = true) {
        string directory = Path.Combine(Root, "packages"); if (!Directory.Exists(directory)) yield break;
        foreach (string packageName in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal)) {
            if (name != null && !Path.GetFileName(packageName).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string version in Directory.EnumerateDirectories(packageName).Order(StringComparer.Ordinal)) {
                string key = Path.GetFileName(packageName) + "/" + Path.GetFileName(version);
                yield return Get(key, verify);
            }
        }
    }
    internal CachedBottle Store(ProjectManifest manifest, string source, Dictionary<string, string> dependencies) {
        string stage = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(stage, "content"));
        try {
            string content = Path.Combine(stage, "content");
            var files = manifest.SourceFiles().Concat(manifest.Assets.SelectMany(asset => {
                string path = SafeFiles.Under(manifest.Root, asset.Source);
                if (!File.Exists(path) && !Directory.Exists(path)) throw new InvalidDataException($"Asset does not exist: {path}");
                return Directory.Exists(path) ? SafeFiles.Enumerate(path) : [path];
            })).Append(manifest.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length > WasmApplication.MaxEntries) throw new InvalidDataException("Too many library files.");
            long bytes = 0;
            foreach (string file in files) {
                SafeFiles.Check(manifest.Root, file);
                bytes = checked(bytes + new FileInfo(file).Length);
                if (bytes > WasmApplication.MaxPackageBytes) throw new InvalidDataException("Library snapshot exceeds the package size limit.");
                string target = SafeFiles.Under(content, file == manifest.FilePath ? ProjectManifest.FileName : Path.GetRelativePath(manifest.Root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            // Empty asset directories are harmless but must survive snapshot validation.
            foreach (var asset in manifest.Assets) if (Directory.Exists(SafeFiles.Under(manifest.Root, asset.Source))) Directory.CreateDirectory(SafeFiles.Under(content, asset.Source));
            foreach (string sourcePath in manifest.Sources.Where(s => s != ".")) if (Directory.Exists(SafeFiles.Under(manifest.Root, sourcePath))) Directory.CreateDirectory(SafeFiles.Under(content, sourcePath));
            var package = new CachedBottle(manifest.Name, manifest.Version!, HashDirectory(content), source, dependencies);
            string destination = PackageRoot(package.Key);
            if (Directory.Exists(destination)) {
                var existing = Get(package.Key);
                if (existing.Hash != package.Hash)
                    throw new InvalidDataException($"Library {package.Key} is already cached with different contents. Publish a new version; versions are immutable.");
                // Two projects may use the same source with different compatible child versions.
                return existing with { Dependencies = dependencies };
            }
            Write(Path.Combine(stage, "package.json"), package);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(stage, destination);
            return package;
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public void Remove(string name, string version) {
        string key = name.ToLowerInvariant() + "/" + BottleVersion.Parse(version);
        string directory = PackageRoot(key); if (!Directory.Exists(directory)) throw new IOException($"Cached library not found: {key}");
        // Removal is also the recovery path for corrupt content, including a bad
        // manifest. Read other descriptors without trying to validate that content.
        foreach (string nameDirectory in Directory.EnumerateDirectories(Path.Combine(Root, "packages"))) {
            SafeFiles.Check(Root, nameDirectory);
            foreach (string versionDirectory in Directory.EnumerateDirectories(nameDirectory)) {
                if (versionDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase)) continue;
                SafeFiles.Check(Root, versionDirectory);
                var package = Read<CachedBottle>(Path.Combine(versionDirectory, "package.json"));
                if (package.Dependencies == null) throw new InvalidDataException("Invalid cached dependency descriptor.");
                if (package.Dependencies.Values.Contains(key)) throw new InvalidDataException($"Cannot remove {key}; cached {package.Key} depends on it.");
            }
        }
        Directory.Delete(directory, true);
    }
    internal static string HashDirectory(string root) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0; int count = 0;
        foreach (string file in SafeFiles.Enumerate(root).OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), StringComparer.Ordinal)) {
            long length = new FileInfo(file).Length; bytes = checked(bytes + length);
            if (++count > WasmApplication.MaxEntries || bytes > WasmApplication.MaxPackageBytes) throw new InvalidDataException("Library cache exceeds package limits.");
            byte[] path = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(path.Length))); hash.AppendData(path);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(length)));
            using var stream = File.OpenRead(file); byte[] buffer = new byte[81920]; int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static T Read<T>(string file) {
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException($"Bottle record exceeds the size limit: {file}");
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions) ?? throw new InvalidDataException($"Empty Bottle record: {file}"); }
        catch (JsonException e) { throw new InvalidDataException($"Invalid Bottle record: {file}", e); }
    }
    internal static void Write<T>(string file, T value) => AtomicText(file, JsonSerializer.Serialize(value, JsonOptions) + "\n");
    internal static void AtomicText(string file, string text) {
        string stage = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(stage, text, new UTF8Encoding(false)); File.Move(stage, file, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
