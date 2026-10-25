using AquariusLang.Build.Targets;
using AquariusLang.Libraries;

namespace AquariusLang.Build;

/// <summary>One export of a compiled application.</summary>
/// <param name="Input">The application .wasm.</param>
/// <param name="Output">The file or folder the target writes.</param>
/// <param name="EntryPoint">The script to start, instead of the application's entry.</param>
/// <param name="Options">Target options (<see cref="IWasmBuildTarget.OptionNames"/>) with their values.</param>
/// <param name="Libraries">The libraries the application was compiled with: default libraries plus the project's
/// external ones, whose native binaries the target ships.</param>
public sealed record WasmBuildRequest(string Input, string Output, string? EntryPoint = null,
    IReadOnlyDictionary<string, string>? Options = null, LibrarySet? Libraries = null)
{
    public bool Flag(string name)
    {
        if (Options == null || !Options.TryGetValue(name, out var value)) return false;
        return value.ToLowerInvariant() switch {
            "true" or "on" or "yes" or "1" => true, "false" or "off" or "no" or "0" => false,
            _ => throw new ArgumentException($"{name} expects true or false.")
        };
    }
    public string? Option(string name) => Options?.GetValueOrDefault(name);
}

/// <summary>One platform backend: it turns an application .wasm into what the platform runs.</summary>
public interface IWasmBuildTarget
{
    string Name { get; }
    string ArtifactDescription { get; }
    IReadOnlyCollection<string> OptionNames => Array.Empty<string>();
    void Build(WasmBuildRequest request);
}

/// <summary>The registered targets; a new platform registers here and needs no command-line changes.</summary>
public sealed class BuildTargets
{
    private readonly IReadOnlyDictionary<string, IWasmBuildTarget> targets;
    public BuildTargets(IEnumerable<IWasmBuildTarget> targets) => this.targets = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static BuildTargets Default { get; } = new(new IWasmBuildTarget[] {
        new WebTarget(), new ExecutableTarget("windows", "windows-x64"), new ExecutableTarget("linux", "linux-x64"), new AndroidTarget()
    });

    public IWasmBuildTarget Resolve(string name) => targets.TryGetValue(name, out var target) ? target :
        throw new ArgumentException($"Unknown target: {name}. Available targets: wasm, {string.Join(", ", targets.Keys)}.");
    public IEnumerable<string> Names => targets.Keys;
    public IEnumerable<string> OptionNames => targets.Values.SelectMany(t => t.OptionNames).Distinct(StringComparer.Ordinal);
}
