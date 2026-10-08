using AquariusLang.Object;
using AquariusLang.VM;
using AquariusREPL.Graphics;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.runtime;

/// <summary>Executes packaged imports in memory; each script's closures retain its builtins and directory.</summary>
internal sealed class BottleRuntime : IDisposable {
    private readonly BottlePackage package;
    private readonly string directory;
    private readonly string? resources;
    internal string? ResourceDirectory => resources;
    private readonly GraphicsRuntime graphics;
    private readonly HashSet<string> importing = new(StringComparer.OrdinalIgnoreCase);

    internal BottleRuntime(BottlePackage package, string path, IEnumerable<string>? launchFiles = null) {
        graphics = new(new Application.DesktopApplicationHost(launchFiles: launchFiles));
        this.package = package;
        directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (package.Assets.Count != 0) {
            resources = Path.Combine(Path.GetTempPath(), "aquarius-resources-" + Guid.NewGuid().ToString("N"));
            try {
                foreach (var asset in package.Assets) {
                    BottlePackage.ValidatePath(asset.Key);
                    string target = Path.Combine(resources, asset.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, asset.Value);
                }
            } catch { Dispose(); throw; }
            graphics.ResourcePath = ResolveAsset;
        }
    }

    internal IObject Execute(string entry, AquaEnvironment? environment = null) {
        BottlePackage.ValidateScriptPath(entry);
        if (!package.Scripts.TryGetValue(entry, out var program)) return new ErrorObj($"Packaged script not found: {entry}");
        if (!importing.Add(entry)) return new ErrorObj($"Circular packaged import: {entry}");
        try {
            string scriptPath = Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar));
            var desktop = new DesktopBuiltins(graphics);
            desktop.NewDefaultBuiltins(scriptPath);
            desktop.ResourceArgument = ResolveAssetArgument;
            desktop.ScriptImport = path => Import(entry, path);
            return new VirtualMachine(desktop).Execute(program, environment);
        } finally { importing.Remove(entry); }
    }

    private IObject Import(string currentEntry, string path) {
        try {
            // Absolute paths produced by 目前工作目錄 also map into the relocated package.
            path = path.Replace('\\', '/');
            string relative;
            if (Path.IsPathRooted(path)) relative = Path.GetRelativePath(directory, Path.GetFullPath(path));
            else {
                string currentDirectory = Path.GetDirectoryName(currentEntry.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                relative = Path.GetRelativePath(directory, Path.GetFullPath(Path.Combine(directory, currentDirectory, path.Replace('\\', '/'))));
            }
            relative = relative.Replace('\\', '/');
            if (!relative.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".rius", StringComparison.OrdinalIgnoreCase))
                return new ErrorObj($"Packaged imports require an .aqua or .rius path: {path}");
            string entry = package.ResolveScript(relative);
            var environment = AquaEnvironment.NewEnvironment();
            var result = Execute(entry, environment);
            return result is ErrorObj ? result : new ModuleObj(environment);
        } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException) {
            return new ErrorObj($"匯入: {error.Message}");
        }
    }

    private string ResolveAsset(string path) {
        if (resources == null) return path;
        string relative = Path.IsPathRooted(path) ? Path.GetRelativePath(directory, Path.GetFullPath(path)) : path;
        string name;
        try { name = BottlePackage.ResolvePath(relative); }
        catch (InvalidDataException) { return path; }
        string? key = package.Assets.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key == null ? path : Path.Combine(resources, key.Replace('/', Path.DirectorySeparatorChar));
    }

    // Legacy execFile accepts a string containing a script path followed by arguments.
    private string ResolveAssetArgument(string argument) {
        string normalized = argument.Replace('\\', '/');
        foreach (string asset in package.Assets.Keys.OrderByDescending(k => k.Length)) {
            string full = Path.Combine(directory, asset.Replace('/', Path.DirectorySeparatorChar));
            if (argument.StartsWith(full, StringComparison.OrdinalIgnoreCase) &&
                (argument.Length == full.Length || argument[full.Length] == ' '))
                return QuoteResource(ResolveAsset(full)) + argument[full.Length..];
            string slash = full.Replace('\\', '/');
            if (normalized.StartsWith(slash, StringComparison.OrdinalIgnoreCase) &&
                (normalized.Length == slash.Length || normalized[slash.Length] == ' '))
                return QuoteResource(ResolveAsset(full)) + argument[slash.Length..];
        }
        return ResolveAsset(argument);
    }
    private static string QuoteResource(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    public void Dispose() {
        try { graphics.Dispose(); }
        finally {
            if (resources != null && Directory.Exists(resources) &&
                Path.GetFullPath(resources).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resources, true);
        }
    }
}
