using System.Text.Json;
using AquariusLang.Graphics;
using AquariusLang.Packaging;
using AquariusLang.runtime;
using AquariusLang.VM;

namespace AquariusLang.Web;

/// <summary>Export validated compiled bottles as a self-contained static browser application.</summary>
public static class WebsiteCompiler {
    // Compatibility entry point: source compilation uses the same package pipeline as aqua build.
    public static void Build(string source, string output, string? entry = null) {
        if (source.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase)) { BuildBottle(source, output, entry); return; }
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        bool single = File.Exists(source);
        string root = single ? Path.GetDirectoryName(source)! : source;
        if (!Directory.Exists(root)) throw new ArgumentException("Source directory does not exist.");
        if (Within(root, output)) throw new ArgumentException("Output must not contain the source directory.");
        var files = BottlePackage.EnumerateFiles(root).Where(p => !Within(p, output)).ToArray();
        var sources = files.Where(p => p.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)).ToList();
        string Name(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');
        entry ??= single ? Name(source) : sources.Any(p => Name(p) == "main.aqua") ? "main.aqua" : sources.Select(Name).FirstOrDefault();
        if (entry == null) throw new ArgumentException("Entry must name a bundled Aquarius script.");
        var assets = files.Where(p => new[] { ".png", ".jpg", ".jpeg", ".ppm", ".txt", ".wgsl", ".py" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
        string bottle = Path.Combine(Path.GetTempPath(), "aquarius-web-" + Guid.NewGuid().ToString("N") + ".bottle");
        try {
            BottlePackage.Compile(sources, bottle, root, assets, entry);
            Export(BottlePackage.Load(bottle), output, entry, sourceNames: true);
        } finally { if (File.Exists(bottle)) File.Delete(bottle); }
    }

    public static void BuildBottle(string bottle, string output, string? entry = null) {
        bottle = Path.GetFullPath(bottle); output = Path.GetFullPath(output);
        if (!bottle.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Web builds require an existing .bottle file.");
        if (Within(bottle, output)) throw new ArgumentException("Output must not contain the input bottle.");
        Export(BottlePackage.Load(bottle), output, entry, sourceNames: false);
    }

    private static bool Within(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Export(BottlePackage package, string output, string? entry, bool sourceNames) {
        string selected = package.ResolveScript(entry ?? package.EntryPoint);
        string Name(string p) => sourceNames ? Path.ChangeExtension(p, ".aqua").Replace('\\', '/') : p;
        // Serialize before touching output: unsupported instructions preserve previous builds.
        var modules = package.Scripts.ToDictionary(p => Name(p.Key), p => WebBytecodeSerializer.ToDocument(p.Value));
        var assets = package.Assets.ToDictionary(p => p.Key, p => Convert.ToBase64String(p.Value));
        var assembly = typeof(WebsiteCompiler).Assembly;
        const string prefix = "AquariusWebCompiler.browser.";
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (!resources.Contains(prefix + "vendor-jolt.wasm"))
            throw new ArgumentException("Browser dependencies are missing. Run npm ci --prefix AquariusWebCompiler and npm run --prefix AquariusWebCompiler prepare:browser, then rebuild the compiler.");
        string constants;
        using (var reader = new StreamReader(assembly.GetManifestResourceStream(prefix + "legacy-constants.json")!)) constants = reader.ReadToEnd();
        string json = JsonSerializer.Serialize(new {
            version = WebBytecodeSerializer.FormatVersion, entry = Name(selected), modules, assets,
            packageVersion = package.Version, pathComparison = "ordinalIgnoreCase",
            catalog = LibraryCatalog.Functions.Concat(LibraryCatalog.GlobalFunctions).Select(f => new {
                library = f.Library, english = f.EnglishName, chinese = f.TraditionalChineseName,
                min = f.MinimumArguments, max = f.MaximumArguments
            }),
            graphics = new { header = WgpuShaderAbi.Header, vertex = WgpuShaderAbi.Vertex, fragment = WgpuShaderAbi.Fragment, uniformBytes = WgpuShaderAbi.UniformBytes },
            physics = new { maxBodies = 4096, backend = "Jolt Physics" },
            legacyConstants = JsonSerializer.Deserialize<Dictionary<string, double>>(constants)
        });
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output must be a named directory.");
        // Rebuild only compiler-owned directories; never remove unrelated files or follow links.
        var owned = resources.Select(n => n[prefix.Length..]).Append("program.json").Append(".aquarius-web-output").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(output)) {
            if ((File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(output).Any() ||
                Directory.EnumerateFiles(output).Any(p => !owned.Contains(Path.GetFileName(p)) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                throw new ArgumentException("Output contains unrelated files or links. Select a dedicated web output directory.");
        }
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".aquarius-web-stage-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".aquarius-web-backup-" + Guid.NewGuid().ToString("N"));
        bool committed = false;
        try {
            Directory.CreateDirectory(stage);
            foreach (string resource in resources) {
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var target = File.Create(Path.Combine(stage, resource[prefix.Length..])); input.CopyTo(target);
            }
            File.WriteAllText(Path.Combine(stage, "program.json"), json);
            File.WriteAllText(Path.Combine(stage, ".aquarius-web-output"), "Aquarius static website\n");
            if (Directory.Exists(output)) MoveDirectory(output, backup);
            try { MoveDirectory(stage, output); committed = true; }
            catch { if (Directory.Exists(backup)) MoveDirectory(backup, output); throw; }
        } finally {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (committed && Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    private static void MoveDirectory(string source, string destination) {
        // Windows scanners can briefly hold newly written WASM assets open. Keep the
        // existing commit/rollback boundary and retry only transient directory locks.
        for (int attempt = 0; ; attempt++) {
            try { Directory.Move(source, destination); return; }
            catch (Exception e) when (attempt < 5 && OperatingSystem.IsWindows() &&
                e is IOException or UnauthorizedAccessException && (e.HResult & 0xffff) is 5 or 32 or 33 &&
                Directory.Exists(source) && !Directory.Exists(destination) && !File.Exists(destination)) {
                Thread.Sleep(25 << attempt);
            }
        }
    }
}
