using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.runtime;

public sealed record LibraryFeature(string Name, string TraditionalChineseName, string[] Platforms, string Description);
/// <summary>Versioned, additive host surfaces: core contracts may have optional platform extensions.</summary>
public sealed class LibraryCapabilities {
    public string Platform { get; }
    public int ContractVersion { get; }
    private readonly HashSet<string> supported;
    private readonly IReadOnlyList<LibraryFeature> features;
    public LibraryCapabilities(string platform, int contractVersion, IReadOnlyList<LibraryFeature> features, IEnumerable<string> supported) {
        Platform = platform; ContractVersion = contractVersion; this.features = features;
        this.supported = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (this.supported.Any(n => !features.Any(f => f.Name == n))) throw new ArgumentException("Unknown library feature.");
    }
    public bool Supports(string name) => features.Any(f => (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || f.TraditionalChineseName == name) && supported.Contains(f.Name));
    public IReadOnlyDictionary<string, bool> Snapshot() => features.SelectMany(f => new[] { new KeyValuePair<string, bool>(f.Name, Supports(f.Name)), new(f.TraditionalChineseName, Supports(f.Name)) }).ToDictionary(p => p.Key, p => p.Value);
}
