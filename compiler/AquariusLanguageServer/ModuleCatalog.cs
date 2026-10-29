using System.Collections.Concurrent;
using System.Text.Json;
using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLanguageServer;

internal sealed record CatalogMember(IReadOnlyList<string> Names, bool Function, string Detail, bool Async = false);
/// <summary>A module the editor can complete members of: a native module from the runtime's catalog or a built-in
/// portable module of the language.</summary>
internal sealed record CatalogModule(string Library, IReadOnlyList<string> Names, IReadOnlyList<CatalogMember> Members)
{
    public CatalogMember? Find(string name) => Members.FirstOrDefault(m => m.Names.Contains(name));
}

/// <summary>
/// What a source file can import: the native modules the host runtime pack registers (<c>aquarius --catalog</c>, with
/// the project's external libraries passed as <c>--library</c>), the Aquarius-source modules of the default and the
/// project's libraries (their manifests) and the language's portable modules. Nothing about libraries is hard-coded here.
/// </summary>
internal sealed class ModuleCatalog
{
    private readonly Dictionary<string, CatalogModule> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sources = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Globals { get; }

    private ModuleCatalog(string? json, LibrarySet libraries)
    {
        var globals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json != null) {
            using var document = JsonDocument.Parse(json);
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateArray()) {
                string name = library.GetProperty("name").GetString()!;
                foreach (var module in library.GetProperty("modules").EnumerateArray()) {
                    var names = Names(module);
                    var members = module.GetProperty("members").EnumerateArray().Select(member => Member(names[0], member)).ToArray();
                    if (module.GetProperty("module").GetBoolean()) {
                        var entry = new CatalogModule(name, names, members);
                        foreach (string alias in names) modules.TryAdd(alias, entry);
                    } else if (name == "core" && names[0] == "globals")
                        foreach (var member in members) foreach (string alias in member.Names) if (!alias.Contains('@')) globals[alias] = member.Detail; // async@ and await@ are the compiler's
                }
            }
        }
        foreach (var portable in LanguageFacts.PortableModules)
            foreach (string alias in portable.Names) modules.TryAdd(alias, portable);
        foreach (var library in libraries.Libraries)
            foreach (var module in library.Modules)
                foreach (string alias in module.Names) sources.TryAdd(alias, library.SourcePath(module.Source));
        Globals = globals;
    }

    /// <summary>Names of library functions that give a promise (aqua_define_async).</summary>
    public IReadOnlySet<string> AsyncMembers => asyncMembers ??= modules.Values.SelectMany(m => m.Members).Where(m => m.Async).SelectMany(m => m.Names).ToHashSet(StringComparer.Ordinal);
    private HashSet<string>? asyncMembers;
    public CatalogModule? Module(string name) => modules.GetValueOrDefault(name);
    /// <summary>The source file of an Aquarius-source library module an import name selects.</summary>
    public string? SourceModule(string name) => sources.GetValueOrDefault(name);

    private static readonly ConcurrentDictionary<string, Lazy<ModuleCatalog>> cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The catalog for a source file: its project's libraries, or the default ones.</summary>
    public static ModuleCatalog For(string? sourceFile)
    {
        string? manifest = sourceFile == null ? null : ProjectManifest.Find(sourceFile);
        return cache.GetOrAdd(manifest ?? "", key => new Lazy<ModuleCatalog>(() => Create(key.Length == 0 ? null : key))).Value;
    }
    /// <summary>Forgets catalogs after manifests change.</summary>
    public static void Invalidate() => cache.Clear();

    private static ModuleCatalog Create(string? manifest)
    {
        LibrarySet libraries = LibrarySet.Default;
        try { if (manifest != null) libraries = ProjectBuilder.Inspect(ProjectManifest.Load(manifest)); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { Console.Error.WriteLine($"Libraries of {manifest}: {e.Message}"); }
        string? json = null;
        try {
            string target = AquariusInstallation.HostTarget;
            var files = libraries.Libraries.Where(l => l.External).Select(l => l.NativeBinary(target)).OfType<string>().ToArray();
            json = NativeRuntime.Catalog(files);
        } catch (Exception e) when (e is IOException or InvalidDataException or PlatformNotSupportedException or JsonException or System.ComponentModel.Win32Exception) {
            Console.Error.WriteLine($"Native module catalog unavailable (completion covers Aquarius sources only): {e.Message}");
        }
        try { return new ModuleCatalog(json, libraries); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) {
            Console.Error.WriteLine($"Invalid native module catalog: {e.Message}");
            return new ModuleCatalog(null, libraries);
        }
    }

    private static string[] Names(JsonElement element) => element.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray();

    private static CatalogMember Member(string owner, JsonElement member)
    {
        var names = Names(member);
        string kind = member.GetProperty("kind").GetString()!;
        string label = string.Join(" / ", names);
        if (kind != "function") return new CatalogMember(names, false, $"{owner}.{label} ({kind})");
        string signature = member.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "";
        bool async = member.TryGetProperty("async", out var a) && a.GetBoolean();
        return new CatalogMember(names, true, $"{label}({Parameters(signature)}){(async ? " → 承諾 Promise" : "")}", async);
    }

    /// <summary>A runtime signature ("ns|b", "v*") as readable parameter kinds.</summary>
    public static string Parameters(string signature)
    {
        var parts = new List<string>(); bool optional = false;
        foreach (char c in signature) {
            if (c == '|') { optional = true; continue; }
            if (c == '*') { if (parts.Count > 0) parts[^1] += "..."; continue; }
            string kind = c switch { 'n' => "number", 'i' => "integer", 'b' => "boolean", 's' => "string", 'a' => "array", 'h' => "hash",
                'f' => "function", 'o' => "object", _ => "value" };
            parts.Add(optional ? kind + "?" : kind);
        }
        return string.Join(", ", parts);
    }
}
