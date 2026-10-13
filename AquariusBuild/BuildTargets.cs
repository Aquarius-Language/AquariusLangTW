namespace AquariusLang.Build;

public sealed record WasmBuildRequest(string Input, string Output, string? EntryPoint = null,
    IReadOnlyDictionary<string,string>? Options = null);

/// <summary>One platform backend. Source-to-wasm compilation remains independent of deployment.</summary>
public interface IWasmBuildTarget {
    string Name { get; }
    string ArtifactDescription { get; }
    IReadOnlyCollection<string> OptionNames => Array.Empty<string>();
    void Build(WasmBuildRequest request);
}

/// <summary>Explicit target registration; new platforms do not change command-line parsing.</summary>
public sealed class BuildTargets {
    private readonly IReadOnlyDictionary<string, IWasmBuildTarget> targets;
    public BuildTargets(IEnumerable<IWasmBuildTarget> targets) {
        this.targets = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }
    public IWasmBuildTarget Resolve(string name) => targets.TryGetValue(name, out var target) ? target :
        throw new ArgumentException($"Unknown target: {name}. Available targets: wasm, {string.Join(", ", targets.Keys)}.");
    public IEnumerable<string> OptionNames => targets.Values.SelectMany(t => t.OptionNames).Distinct(StringComparer.Ordinal);
}
