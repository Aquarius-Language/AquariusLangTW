using System.Reflection;

namespace AquariusLang;

/// <summary>The AquariusLang version: one semantic version for the language, its compiler, the CLI and the language
/// server, set once as &lt;Version&gt; in compiler/Directory.Build.props (see docs/versioning.md).</summary>
public static class AquariusVersion
{
    public static string Current { get; } = typeof(AquariusVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
