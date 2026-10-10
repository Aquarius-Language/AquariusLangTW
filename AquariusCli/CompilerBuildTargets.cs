using AquariusLang.Build;
using AquariusLang.Web;

namespace AquariusLang.Cli;

/// <summary>The compiler's platform composition root; deployment contracts have no dependency on host executables.</summary>
public static class CompilerBuildTargets {
    public static BuildTargets Default { get; } = new(new IBottleBuildTarget[] {
        new WebsiteBuildTarget(), new SelfContainedExecutableTarget("windows", "win-x64", ".exe")
    });

    private sealed class WebsiteBuildTarget : IBottleBuildTarget {
        public string Name => "web";
        public string ArtifactDescription => "Website";
        public void Build(BottleBuildRequest request) => WebsiteCompiler.BuildBottle(request.Input, request.Output, request.EntryPoint);
    }
}
