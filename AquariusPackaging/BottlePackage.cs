using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using AquariusLang.VM;

namespace AquariusLang.Packaging;

/// <summary>Host-independent compiled modules and resources. Loading never executes code.</summary>
public sealed class BottlePackage {
    public const string ManifestPath = "bottle.json";
    public const int FormatVersion = 2;
    public const int MaxEntries = 10_000;
    public const long MaxPackageBytes = 256L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public int Version { get; }
    public string EntryPoint { get; }
    public IReadOnlyDictionary<string, Bytecode> Scripts { get; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; }

    private BottlePackage(int version, string entry, Dictionary<string, Bytecode> scripts, Dictionary<string, byte[]> assets) {
        Version = version; EntryPoint = entry;
        Scripts = new ReadOnlyDictionary<string, Bytecode>(scripts);
        Assets = new ReadOnlyDictionary<string, byte[]>(assets);
    }

    public static void Compile(IEnumerable<string> sourceFiles, string outputPath, string? rootDirectory = null,
        IEnumerable<string>? assetPaths = null, string? entryPoint = null) {
        string root = Path.GetFullPath(rootDirectory ?? System.Environment.CurrentDirectory);
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package roots must not be symbolic links.");
        string output = Path.GetFullPath(outputPath);
        if (!output.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output file must have a .bottle extension.");
        var scripts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assets = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void AddBytes(long length) {
            total = checked(total + length);
            if (total > MaxPackageBytes) throw new InvalidDataException("The bottle exceeds the size limit.");
            if (scripts.Count + assets.Count >= MaxEntries) throw new ArgumentException("Too many entries in the bottle.");
        }
        foreach (string input in sourceFiles) {
            string source = Path.GetFullPath(input);
            if (!source.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Expected an .aqua source file: {input}");
            string name = Path.ChangeExtension(RelativeInput(root, source), ".rius");
            ValidateScriptPath(name);
            if (scripts.ContainsKey(name)) throw new ArgumentException($"Duplicate package path: {name}");
            Bytecode program;
            try { program = new VmCompiler().Compile(File.ReadAllText(source)); }
            catch (VmCompilationException error) { throw new VmCompilationException($"{input}: {error.Message}"); }
            using var buffer = new MemoryStream(); BytecodeSerializer.Write(program, buffer);
            AddBytes(buffer.Length); scripts.Add(name, buffer.ToArray());
        }
        if (scripts.Count == 0) throw new ArgumentException("Provide at least one .aqua source file.");
        foreach (string input in assetPaths ?? Array.Empty<string>()) {
            string path = Path.GetFullPath(input);
            var files = Directory.Exists(path) ? EnumerateFiles(path) : new[] { path };
            foreach (string file in files) {
                if (new[] { ".aqua", ".bottle" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                string name = RelativeInput(root, file); ValidatePath(name);
                if (name.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rius", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Reserved package path: {name}");
                if (assets.ContainsKey(name)) throw new ArgumentException($"Duplicate asset path: {name}");
                long length = new FileInfo(file).Length; AddBytes(length);
                assets.Add(name, ReadBounded(File.OpenRead(file), length, MaxPackageBytes));
            }
        }
        string entry = entryPoint == null ? scripts.Keys.First() : ResolvePath(entryPoint);
        if (!entry.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".rius", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Entry must use an .aqua or .rius extension.");
        entry = Path.ChangeExtension(entry, ".rius"); ValidateScriptPath(entry);
        entry = scripts.Keys.FirstOrDefault(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Entry must name a packaged script.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest("aquarius-bottle", FormatVersion, entry,
            BytecodeSerializer.FormatVersion, scripts.Keys.ToArray(), assets.Keys.ToArray()), JsonOptions);
        if (manifest.Length > MaxManifestBytes) throw new InvalidDataException("Bottle manifest exceeds the size limit.");
        if (total + manifest.Length > MaxPackageBytes) throw new InvalidDataException("The bottle exceeds the size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)) {
                void Write(string name, byte[] bytes) {
                    var item = archive.CreateEntry(name, CompressionLevel.Optimal);
                    item.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = item.Open(); stream.Write(bytes);
                }
                Write(ManifestPath, manifest);
                foreach (var script in scripts) Write(script.Key, script.Value);
                foreach (var asset in assets) Write(asset.Key, asset.Value);
            }
            File.Move(temporary, output, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static BottlePackage Load(string path) {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaxEntries + 1) throw new InvalidDataException("Too many entries in the bottle.");
        long total = 0;
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in archive.Entries) {
            ValidatePath(item.FullName);
            if (!entries.TryAdd(item.FullName, item)) throw new InvalidDataException($"Duplicate bottle entry: {item.FullName}");
            total = checked(total + item.Length);
            if (total > MaxPackageBytes) throw new InvalidDataException("The bottle exceeds the size limit.");
        }
        if (!entries.TryGetValue(ManifestPath, out var manifestEntry)) throw new InvalidDataException("Missing bottle manifest.");
        var bytes = ReadBounded(manifestEntry.Open(), manifestEntry.Length, MaxManifestBytes);
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions); }
        catch (JsonException error) { throw new InvalidDataException("Invalid bottle manifest.", error); }
        if (manifest == null || manifest.Format != "aquarius-bottle" || manifest.Version is not (1 or FormatVersion))
            throw new InvalidDataException("Missing or unsupported bottle manifest.");
        if (manifest.Version == 1 && bytes.Length > 16 * 1024) throw new InvalidDataException("Bottle manifest exceeds the size limit.");
        ValidateScriptPath(manifest.EntryPoint);
        string[] modules = manifest.Version == 1 ? entries.Keys.Where(k => k != ManifestPath).ToArray() : manifest.Modules
            ?? throw new InvalidDataException("Bottle module metadata is missing.");
        string[] resources = manifest.Version == 1 ? Array.Empty<string>() : manifest.Assets
            ?? throw new InvalidDataException("Bottle asset metadata is missing.");
        if (manifest.Version == FormatVersion && manifest.BytecodeVersion != BytecodeSerializer.FormatVersion)
            throw new InvalidDataException("Unsupported bottle bytecode version.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestPath };
        var scripts = new Dictionary<string, Bytecode>(StringComparer.OrdinalIgnoreCase);
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry Listed(string name) {
            ValidatePath(name);
            if (!listed.Add(name)) throw new InvalidDataException($"Duplicate manifest path: {name}");
            if (!entries.TryGetValue(name, out var item)) throw new InvalidDataException($"Missing bottle entry: {name}");
            return item;
        }
        foreach (string name in modules) {
            ValidateScriptPath(name); var item = Listed(name);
            if (item.Length > BytecodeSerializer.MaxFileBytes) throw new InvalidDataException("Bytecode exceeds the size limit.");
            using var stream = item.Open(); scripts.Add(item.FullName, BytecodeSerializer.Read(stream));
        }
        foreach (string name in resources) {
            if (name != null && (name.EndsWith(".rius", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Assets must not contain Aquarius source or bytecode.");
            var item = Listed(name!); assets.Add(item.FullName, ReadBounded(item.Open(), item.Length, MaxPackageBytes));
        }
        if (listed.Count != entries.Count) throw new InvalidDataException("Bottle contains undeclared entries.");
        string entry = scripts.Keys.FirstOrDefault(k => k.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The bottle entry point is missing.");
        return new BottlePackage(manifest.Version, entry, scripts, assets);
    }

    public string ResolveScript(string path, string currentEntry = "") {
        string name = ResolvePath(path, currentEntry);
        if (!name.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".rius", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Packaged imports require an .aqua or .rius path.");
        name = Path.ChangeExtension(name, ".rius"); ValidateScriptPath(name);
        return Scripts.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Packaged script not found: {name}");
    }

    /// <summary>Normalize virtual rooted or module-relative paths, permitting internal parent traversal.</summary>
    public static string ResolvePath(string path, string currentEntry = "") {
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Empty package path.");
        path = path.Replace('\\', '/');
        string prefix = path.StartsWith('/') ? "" : currentEntry.Contains('/') ? currentEntry[..currentEntry.LastIndexOf('/')] + "/" : "";
        var parts = new List<string>();
        foreach (string part in (prefix + path).Split('/')) {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Invalid relative package path: path escapes bottle package."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        string result = string.Join('/', parts); ValidatePath(result); return result;
    }

    public static void ValidateScriptPath(string? path) {
        ValidatePath(path);
        if (!path!.EndsWith(".rius", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Invalid relative .rius path: {path}. Use --root to select the source root.");
    }
    public static void ValidatePath(string? path) {
        if (string.IsNullOrEmpty(path) || path.Any(c => c < 32 || "\\:*?<>|\"".Contains(c)) ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
                System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException($"Invalid relative package path: {path}. Use --root to select the source root.");
    }
    private static string RelativeInput(string root, string file) {
        string name = Path.GetRelativePath(root, file).Replace('\\', '/'); ValidatePath(name);
        for (string? current = file; current != null && !current.Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package inputs must not follow symbolic links.");
        }
        return name;
    }
    public static IEnumerable<string> EnumerateFiles(string directory) {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package roots must not be symbolic links.");
        foreach (string file in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal)) {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package inputs must not follow symbolic links.");
            yield return file;
        }
        foreach (string dir in Directory.EnumerateDirectories(directory).OrderBy(p => p, StringComparer.Ordinal)) {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 || new[] { ".git", ".idea", "bin", "obj", "node_modules", "dist" }.Contains(Path.GetFileName(dir))) continue;
            foreach (string file in EnumerateFiles(dir)) yield return file;
        }
    }
    private static byte[] ReadBounded(Stream stream, long expected, long maximum) {
        using (stream) {
            if (expected < 0 || expected > maximum) throw new InvalidDataException("Bottle entry exceeds the size limit.");
            var bytes = new byte[checked((int)expected)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Bottle entry length mismatch.");
            return bytes;
        }
    }
    private sealed record Manifest(string Format, int Version, string EntryPoint, int? BytecodeVersion = null, string[]? Modules = null, string[]? Assets = null);
}
