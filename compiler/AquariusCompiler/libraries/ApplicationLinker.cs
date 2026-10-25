using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Libraries;

/// <summary>What linking adds to an application: the bundled library modules, the import table of their names and the
/// libraries it needs.</summary>
public sealed record LinkedApplication(
    IReadOnlyDictionary<string, LoweredProgram> Modules,
    IReadOnlyDictionary<string, string> Imports,
    IReadOnlyList<Library> Libraries,
    string[]? Platforms)
{
    /// <summary>The native libraries the application's metadata lists ([a-z0-9_] names), in order.</summary>
    public IReadOnlyList<string> NativeLibraries => Libraries.Where(l => l.HasNative).Select(l => l.NativeName!).ToArray();
}

/// <summary>
/// Finds the libraries an application needs and bundles their Aquarius-source modules. A library is needed when the
/// program imports one of its modules by a literal name (<c>匯入("Audio")</c> / <c>import("Models")</c>), when a bundled
/// library module does, or when the project declares it for dynamic imports. Bundled modules live at
/// <c>.aquarius/&lt;library&gt;/&lt;source&gt;</c> and are scanned the same way, including their relative <c>.aqua</c> imports.
/// </summary>
public static class ApplicationLinker
{
    public const string ReservedDirectory = ".aquarius/";

    public static LinkedApplication Link(IReadOnlyDictionary<string, LoweredProgram> modules, LibrarySet libraries,
        IEnumerable<string>? declared = null, string[]? platforms = null)
    {
        if (modules.Keys.Any(p => p.StartsWith(ReservedDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new CompilationException("The .aquarius module directory is reserved for library modules.");
        var result = new Dictionary<string, LoweredProgram>(modules, StringComparer.OrdinalIgnoreCase);
        var imports = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var needed = new List<Library>();
        var pending = new Queue<(LoweredProgram Program, Library? Owner, string Path)>(modules.Select(m => (m.Value, (Library?)null, m.Key)));

        void Need(Library library) { if (!needed.Contains(library)) needed.Add(library); }
        string Bundle(Library library, string source) {
            string path = library.BundlePath(source);
            if (!result.ContainsKey(path)) {
                var program = Lower(library.SourcePath(source), path);
                result.Add(path, program);
                pending.Enqueue((program, library, path));
            }
            return path;
        }
        void BundleModule(Library library, LibraryModule module) {
            string path = Bundle(library, module.Source);
            foreach (string name in module.Names) imports[name] = path;
        }

        foreach (string name in declared ?? []) {
            var library = libraries.Find(name) ?? throw new InvalidDataException($"The project declares the library {name}, which is neither a default library nor a dependency.");
            Need(library);
            foreach (var module in library.Modules) BundleModule(library, module);
        }
        while (pending.TryDequeue(out var item)) {
            foreach (string name in ImportedNames(item.Program)) {
                if (name.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) {
                    // A library module's relative import of another file of its library.
                    if (item.Owner is { } owner && !name.StartsWith('/') && !name.Contains(':') && Relative(item.Path, owner, name) is { } source &&
                        File.Exists(owner.SourcePath(source)))
                        Bundle(owner, source);
                    continue;
                }
                if (libraries.OwnerOf(name) is not { } library) continue;
                Need(library);
                if (libraries.SourceModule(name) is { } module) BundleModule(library, module);
            }
        }

        string[]? supported = platforms;
        foreach (var library in needed) {
            supported = (supported ?? Library.KnownPlatforms).Intersect(library.Platforms, StringComparer.Ordinal).ToArray();
            if (supported.Length == 0)
                throw new InvalidDataException(platforms != null
                    ? $"The library {library.Name} does not support {string.Join(", ", platforms)} (it supports {string.Join(", ", library.Platforms)})."
                    : $"The libraries this application needs have no platform in common ({library.Name} supports {string.Join(", ", library.Platforms)}).");
        }
        return new LinkedApplication(result, imports, needed.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray(), supported);
    }

    // The library source a bundled module's relative import names, or null if it leaves the library.
    private static string? Relative(string bundledPath, Library owner, string import)
    {
        string prefix = ReservedDirectory + owner.Name + "/";
        string directory = bundledPath[prefix.Length..];
        directory = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : "";
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (string part in import.Replace('\\', '/').Split('/')) {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Every literal name the program passes to import / 匯入, including in nested functions.</summary>
    public static IEnumerable<string> ImportedNames(LoweredProgram program)
    {
        var seen = new HashSet<LoweredProgram>();
        var stack = new Stack<LoweredProgram>();
        stack.Push(program);
        while (stack.TryPop(out var body)) {
            if (!seen.Add(body)) continue;
            var code = body.Instructions; var pool = body.Constants;
            for (int i = 0; i + 2 < code.Count; i++)
                if (code[i].Code == IrOperation.Load && pool[code[i].Operand] is "import" or "匯入" &&
                    code[i + 1].Code == IrOperation.Constant && pool[code[i + 1].Operand] is StringObj literal &&
                    code[i + 2].Code == IrOperation.Call && code[i + 2].Operand == 1)
                    yield return literal.Value;
            foreach (object constant in pool) {
                if (constant is FunctionCode function) stack.Push(function.BodyCode);
                else if (constant is LoweredProgram nested) stack.Push(nested);
            }
        }
    }

    /// <summary>Every string literal of a program (and its nested functions) that ends in .aqua: the scripts it may
    /// import, including computed paths such as <c>目前工作目錄 + "/tools.aqua"</c>.</summary>
    public static IEnumerable<string> ScriptReferences(LoweredProgram program)
    {
        var seen = new HashSet<LoweredProgram>();
        var stack = new Stack<LoweredProgram>();
        stack.Push(program);
        while (stack.TryPop(out var body)) {
            if (!seen.Add(body)) continue;
            foreach (object constant in body.Constants) {
                if (constant is StringObj text && text.Value.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) yield return text.Value;
                else if (constant is FunctionCode function) stack.Push(function.BodyCode);
                else if (constant is LoweredProgram nested) stack.Push(nested);
            }
        }
    }

    // Library sources rarely change while a compiler process runs (tests, the language server): lower each version once.
    private static readonly ConcurrentDictionary<(string, DateTime, long), Lazy<LoweredProgram>> lowered = new();
    private static LoweredProgram Lower(string file, string path)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new IOException($"Library module {path} is missing: {file}");
        return lowered.GetOrAdd((info.FullName, info.LastWriteTimeUtc, info.Length), _ => new Lazy<LoweredProgram>(() => {
            try { return new LoweringCompiler().Compile(File.ReadAllText(file)); }
            catch (CompilationException e) { throw new CompilationException($"{file}: {e.Message}"); }
        })).Value;
    }
}
