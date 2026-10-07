using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using AquariusLang.VM;

namespace AquariusREPL.runtime;

/// <summary>A ZIP container of .rius programs and a versioned entry-point manifest.</summary>
public sealed class BottlePackage {
    public const string ManifestPath = "bottle.json";
    public const int FormatVersion = 1;
    private const int MaxScripts = 10_000;
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string EntryPoint { get; }
    public IReadOnlyDictionary<string, Bytecode> Scripts { get; }

    private BottlePackage(string entryPoint, Dictionary<string, Bytecode> scripts) {
        EntryPoint = entryPoint;
        Scripts = new ReadOnlyDictionary<string, Bytecode>(scripts);
    }

    /// <summary>Compile all inputs before atomically replacing the destination. Paths are relative to rootDirectory.</summary>
    public static void Compile(IEnumerable<string> sourceFiles, string outputPath, string? rootDirectory = null) {
        string root = Path.GetFullPath(rootDirectory ?? System.Environment.CurrentDirectory);
        string output = Path.GetFullPath(outputPath);
        if (!output.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output file must have a .bottle extension.");
        var scripts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string sourceFile in sourceFiles) {
            string source = Path.GetFullPath(sourceFile);
            if (!source.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Expected an .aqua source file: {sourceFile}");
            string relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            string entry = Path.ChangeExtension(relative, ".rius");
            ValidateScriptPath(entry);
            if (scripts.ContainsKey(entry)) throw new ArgumentException($"Duplicate package path: {entry}");
            if (scripts.Count == MaxScripts) throw new ArgumentException("Too many scripts in the bottle.");
            Bytecode program;
            try { program = new VmCompiler().Compile(File.ReadAllText(source)); }
            catch (VmCompilationException error) { throw new VmCompilationException($"{sourceFile}: {error.Message}"); }
            using var buffer = new MemoryStream();
            BytecodeSerializer.Write(program, buffer);
            total += buffer.Length;
            if (total > MaxPackageBytes) throw new InvalidDataException("The bottle exceeds the size limit.");
            scripts.Add(entry, buffer.ToArray());
        }
        if (scripts.Count == 0) throw new ArgumentException("Provide at least one .aqua source file.");
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)) {
                using (var manifest = archive.CreateEntry(ManifestPath).Open())
                    JsonSerializer.Serialize(manifest, new Manifest("aquarius-bottle", FormatVersion, scripts.Keys.First()), JsonOptions);
                foreach (var script in scripts) {
                    using var stream = archive.CreateEntry(script.Key, CompressionLevel.Optimal).Open();
                    stream.Write(script.Value);
                }
            }
            File.Move(temporary, output, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static BottlePackage Load(string path) {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaxScripts + 1) throw new InvalidDataException("Too many entries in the bottle.");
        var scripts = new Dictionary<string, Bytecode>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Manifest? manifest = null;
        long total = 0;
        foreach (var entry in archive.Entries) {
            if (!paths.Add(entry.FullName)) throw new InvalidDataException($"Duplicate bottle entry: {entry.FullName}");
            total += entry.Length;
            if (total > MaxPackageBytes) throw new InvalidDataException("The bottle exceeds the size limit.");
            if (entry.FullName == ManifestPath) {
                if (entry.Length > 16 * 1024) throw new InvalidDataException("Bottle manifest exceeds the size limit.");
                using var stream = entry.Open();
                var bytes = new byte[16 * 1024 + 1];
                int length = stream.ReadAtLeast(bytes, bytes.Length, false);
                if (length == bytes.Length) throw new InvalidDataException("Bottle manifest exceeds the size limit.");
                try { manifest = JsonSerializer.Deserialize<Manifest>(bytes.AsSpan(0, length), JsonOptions); }
                catch (JsonException error) { throw new InvalidDataException("Invalid bottle manifest.", error); }
            } else {
                ValidateScriptPath(entry.FullName);
                if (entry.Length > BytecodeSerializer.MaxFileBytes) throw new InvalidDataException("Bytecode exceeds the size limit.");
                using var stream = entry.Open();
                scripts.Add(entry.FullName, BytecodeSerializer.Read(stream));
            }
        }
        if (manifest is null || manifest.Format != "aquarius-bottle" || manifest.Version != FormatVersion)
            throw new InvalidDataException("Missing or unsupported bottle manifest.");
        ValidateScriptPath(manifest.EntryPoint);
        if (!scripts.ContainsKey(manifest.EntryPoint)) throw new InvalidDataException("The bottle entry point is missing.");
        return new BottlePackage(manifest.EntryPoint, scripts);
    }

    internal static void ValidateScriptPath(string? path) {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            !path.EndsWith(".rius", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException($"Invalid relative .rius path: {path}. Use --root to select the source root.");
    }

    private sealed record Manifest(string Format, int Version, string EntryPoint);
}
