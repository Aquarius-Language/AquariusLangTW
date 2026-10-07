using AquariusLang.Object;
using AquariusLang.VM;
using AquariusREPL.Graphics;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.runtime;

/// <summary>Executes packaged imports in memory; each script's closures retain its builtins and directory.</summary>
internal sealed class BottleRuntime : IDisposable {
    private readonly BottlePackage package;
    private readonly string directory;
    private readonly GraphicsRuntime graphics = new();
    private readonly HashSet<string> importing = new(StringComparer.OrdinalIgnoreCase);

    internal BottleRuntime(BottlePackage package, string path) {
        this.package = package;
        directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    }

    internal IObject Execute(string entry, AquaEnvironment? environment = null) {
        BottlePackage.ValidateScriptPath(entry);
        if (!package.Scripts.TryGetValue(entry, out var program)) return new ErrorObj($"Packaged script not found: {entry}");
        if (!importing.Add(entry)) return new ErrorObj($"Circular packaged import: {entry}");
        try {
            string scriptPath = Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar));
            var desktop = new DesktopBuiltins(graphics);
            desktop.NewDefaultBuiltins(scriptPath);
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
            string entry = Path.ChangeExtension(relative, ".rius");
            BottlePackage.ValidateScriptPath(entry);
            var environment = AquaEnvironment.NewEnvironment();
            var result = Execute(entry, environment);
            return result is ErrorObj ? result : new ModuleObj(environment);
        } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException) {
            return new ErrorObj($"匯入: {error.Message}");
        }
    }

    public void Dispose() => graphics.Dispose();
}
