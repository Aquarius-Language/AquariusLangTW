using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;

namespace AquariusLang.Libraries;

/// <summary>The libraries a compilation can link: the default libraries of the installation plus a project's external
/// libraries. Every module name (each alias) belongs to exactly one library.</summary>
public sealed class LibrarySet
{
    private readonly Dictionary<string, Library> byModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryModule> sourceModules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Library> byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Library> Libraries { get; }

    public LibrarySet(IEnumerable<Library> libraries)
    {
        var list = new List<Library>();
        foreach (var library in libraries) {
            if (!byName.TryAdd(library.Name, library)) throw new InvalidDataException($"Two libraries are named {library.Name}: {byName[library.Name].Root} and {library.Root}.");
            if (library.NativeName != null && list.FirstOrDefault(l => l.NativeName == library.NativeName) is { } same)
                throw new InvalidDataException($"The libraries {same.Name} and {library.Name} both build the native library {library.NativeName}.");
            void Claim(string name) {
                if (byModule.TryGetValue(name, out var owner)) throw new InvalidDataException($"The module name {name} belongs to both {owner.Name} and {library.Name}.");
                byModule.Add(name, library);
            }
            foreach (var aliases in library.NativeModules) foreach (string name in aliases) Claim(name);
            foreach (var module in library.Modules) foreach (string name in module.Names) { Claim(name); sourceModules.Add(name, module); }
            list.Add(library);
        }
        Libraries = list;
    }

    public static LibrarySet Empty { get; } = new([]);

    /// <summary>The default libraries: every libraries/&lt;name&gt;/aqua.toml of the installation (folders without a
    /// manifest are skipped). Empty when the compiler runs without an installation.</summary>
    public static LibrarySet Default => defaults.Value;
    private static readonly Lazy<LibrarySet> defaults = new(() => AquariusInstallation.Current is { } installation ? FromDirectory(installation.Libraries) : Empty);

    public static LibrarySet FromDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return Empty;
        return new LibrarySet(Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal)
            .Where(folder => File.Exists(Path.Combine(folder, Library.ManifestName)))
            .Select(folder => Library.Load(Path.Combine(folder, Library.ManifestName))));
    }

    /// <summary>This set plus more libraries (a project's external ones).</summary>
    public LibrarySet With(IEnumerable<Library> more) => new(Libraries.Concat(more));

    public Library? Find(string name) => byName.GetValueOrDefault(name);
    /// <summary>The library a module name (any alias) belongs to.</summary>
    public Library? OwnerOf(string module) => byModule.GetValueOrDefault(module);
    /// <summary>The Aquarius-source module an import name selects, or null for native modules and unknown names.</summary>
    public LibraryModule? SourceModule(string module) => sourceModules.GetValueOrDefault(module);
}
