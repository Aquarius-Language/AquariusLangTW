using System.Collections.ObjectModel;
using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusLang.Packaging;

/// <summary>A single portable .wasm application, including compiled modules and relocatable resources.</summary>
public sealed class WasmApplication
{
    public const int FormatVersion = WasmAbi.Version;
    public const int MaxEntries = 10_000;
    public const long MaxPackageBytes = WasmProgram.MaxBytes;
    public int Version => FormatVersion;
    public string EntryPoint => Program.Metadata.Entry;
    public WasmProgram Program { get; }
    public IReadOnlyDictionary<string, WasmProgram> Scripts { get; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; }
    private WasmApplication(WasmProgram program)
    {
        Program = program;
        Scripts = new ReadOnlyDictionary<string, WasmProgram>(program.Metadata.Modules.ToDictionary(p => p.Key, p => program, StringComparer.OrdinalIgnoreCase));
        Assets = new ReadOnlyDictionary<string, byte[]>(program.Metadata.Assets.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value), StringComparer.OrdinalIgnoreCase));
    }
    public static void Compile(IEnumerable<string> sourceFiles, string outputPath, string? rootDirectory = null,
        IEnumerable<string>? assetPaths = null, string? entryPoint = null)
    {
        string root = Path.GetFullPath(rootDirectory ?? System.Environment.CurrentDirectory), output = Path.GetFullPath(outputPath);
        if (!output.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must have a .wasm extension.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Application roots must not be symbolic links.");
        var modules = new Dictionary<string, LoweredProgram>(StringComparer.OrdinalIgnoreCase);
        var assets = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (string input in sourceFiles)
        {
            string source = Path.GetFullPath(input), name = RelativeInput(root, source); ValidateScriptPath(name);
            if (modules.ContainsKey(name)) throw new ArgumentException($"Duplicate module: {name}");
            try { modules.Add(name, new LoweringCompiler().Compile(File.ReadAllText(source))); }
            catch (CompilationException e) { throw new CompilationException($"{input}: {e.Message}"); }
        }
        if (modules.Count == 0) throw new ArgumentException("Provide at least one .aqua source file.");
        foreach (string input in assetPaths ?? Array.Empty<string>())
        {
            string path = Path.GetFullPath(input);
            foreach (string file in Directory.Exists(path) ? EnumerateFiles(path) : new[] { path })
            {
                if (new[] { ".aqua", ".wasm", ".wasm", ".aqua" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                string name = RelativeInput(root, file); long length = new FileInfo(file).Length; size = checked(size + length);
                if (size > MaxPackageBytes) throw new InvalidDataException("Application assets exceed the size limit.");
                if (modules.ContainsKey(name) || !assets.TryAdd(name, File.ReadAllBytes(file))) throw new ArgumentException($"Duplicate application path: {name}");
            }
        }
        if (modules.Count + assets.Count > MaxEntries) throw new InvalidDataException("Too many application entries.");
        string entry = entryPoint == null ? modules.Keys.First() : ResolvePath(entryPoint);
        entry = modules.Keys.FirstOrDefault(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Entry must name a compiled module.");
        var program = new WasmCompiler().Compile(modules, entry, assets);
        if (program.Bytes.Length > MaxPackageBytes) throw new InvalidDataException("Application exceeds the size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string stage = Path.Combine(Path.GetDirectoryName(output)!, ".aquarius-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(stage, program.Bytes.ToArray()); File.Move(stage, output, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    public static WasmApplication Load(string path) => new(WasmProgram.Load(path));
    public static WasmApplication Load(Stream input)
    {
        if (input.Length > MaxPackageBytes) throw new InvalidDataException("Application exceeds the size limit.");
        byte[] bytes = new byte[checked((int)input.Length)]; input.Position = 0; input.ReadExactly(bytes); return new(WasmProgram.Load(bytes));
    }
    public string ResolveScript(string path, string currentEntry = "")
    {
        string name = ResolvePath(path, currentEntry); ValidateScriptPath(name);
        return Scripts.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"Compiled module not found: {name}");
    }
    /// <summary>Normalize virtual rooted or module-relative paths, permitting internal parent traversal.</summary>
    public static string ResolvePath(string path, string currentEntry = "")
    {
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Empty package path.");
        path = path.Replace('\\', '/');
        string prefix = path.StartsWith('/') ? "" : currentEntry.Contains('/') ? currentEntry[..currentEntry.LastIndexOf('/')] + "/" : "";
        var parts = new List<string>();
        foreach (string part in (prefix + path).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Invalid relative package path: path escapes application."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        string result = string.Join('/', parts); ValidatePath(result); return result;
    }

    public static void ValidateScriptPath(string? path)
    {
        ValidatePath(path);
        if (!path!.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Invalid relative .aqua path: {path}. Use --root to select the source root.");
    }
    public static void ValidatePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(c => c < 32 || "\\:*?<>|\"".Contains(c)) ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
                System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException($"Invalid relative package path: {path}. Use --root to select the source root.");
    }
    private static string RelativeInput(string root, string file)
    {
        string name = Path.GetRelativePath(root, file).Replace('\\', '/'); ValidatePath(name);
        for (string? current = file; current != null && !current.Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package inputs must not follow symbolic links.");
        }
        return name;
    }
    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package roots must not be symbolic links.");
        foreach (string file in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package inputs must not follow symbolic links.");
            yield return file;
        }
        foreach (string dir in Directory.EnumerateDirectories(directory).OrderBy(p => p, StringComparer.Ordinal))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 || new[] { ".git", ".idea", "bin", "obj", "node_modules", "dist" }.Contains(Path.GetFileName(dir))) continue;
            foreach (string file in EnumerateFiles(dir)) yield return file;
        }
    }
}
