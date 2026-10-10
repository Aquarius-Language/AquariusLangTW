namespace AquariusLang.Build;

public sealed record BottleBuildRequest(string Input, string Output, string? EntryPoint = null);

/// <summary>One platform backend. Source-to-bottle compilation remains independent of deployment.</summary>
public interface IBottleBuildTarget {
    string Name { get; }
    string ArtifactDescription { get; }
    void Build(BottleBuildRequest request);
}

/// <summary>Explicit target registration; new platforms do not change command-line parsing.</summary>
public sealed class BuildTargets {
    private readonly IReadOnlyDictionary<string, IBottleBuildTarget> targets;
    public BuildTargets(IEnumerable<IBottleBuildTarget> targets) {
        this.targets = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }
    public IBottleBuildTarget Resolve(string name) => targets.TryGetValue(name, out var target) ? target :
        throw new ArgumentException($"Unknown target: {name}. Available targets: bottle, {string.Join(", ", targets.Keys)}.");
}
