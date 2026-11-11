using AquariusLang.Build.Bindings;
using AquariusLang.Build.Libraries;
using AquariusLang.Compiler;
using AquariusTests.Exports;

namespace AquariusTests.architecture;

/// <summary>
/// The extension model of native library outputs (docs/native-libraries.md), enforced: every built platform has a
/// descriptor, a platform that builds libraries has its link rules in its platform layer, and every binding generator
/// has goldens. A new platform or language follows the same pattern and nothing else changes.
/// </summary>
[Trait("Category", "Architecture")]
public class NativeLibraryRulesTest
{
    private static readonly string Root = ArchitectureRulesTest.Root;

    [Fact]
    public void EveryBuiltPlatformHasOneNativeLibraryDescriptor()
    {
        var platforms = RuntimeTarget.Built.Select(t => t.Platform).Distinct().Order().ToArray();
        Assert.Equal(platforms, NativeLibraryPlatforms.All.Select(p => p.Platform).Order().ToArray());
    }

    [Fact]
    public void PlatformsThatBuildLibrariesHaveLinkRulesInTheirLayer()
    {
        foreach (var platform in NativeLibraryPlatforms.All) {
            bool rules = File.Exists(Path.Combine(Root, "runtime", "platform", platform.Platform, "embed.cmake"));
            Assert.True(rules == platform.Builds,
                $"{platform.Platform}: a platform builds native library outputs exactly when runtime/platform/{platform.Platform}/embed.cmake exists (descriptor Builds = {platform.Builds}).");
        }
    }

    [Fact]
    public void EveryBindingGeneratorHasGoldens()
    {
        foreach (var generator in BindingGenerators.All) {
            string folder = Path.Combine(ExportTest.GoldenFolder, generator.Language);
            Assert.True(Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(),
                $"The {generator.Language} binding has no goldens in {folder}: run ExportTest with AQUARIUS_UPDATE_BINDING_GOLDENS=1 and review them.");
        }
    }

    [Fact]
    public void DefaultBindingsAreWrittenOrPlannedLanguages()
    {
        foreach (var platform in NativeLibraryPlatforms.All.Where(p => p.Builds))
            Assert.Contains(platform.DefaultBindings, b => BindingGenerators.Find(b) != null);
    }
}
