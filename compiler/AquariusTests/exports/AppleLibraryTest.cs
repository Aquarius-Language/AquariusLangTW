using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Build.Bindings;
using AquariusLang.Build.Libraries;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Packaging;
using AquariusTests.Packaging;
using AquariusTests.Targets;

namespace AquariusTests.Exports;

/// <summary>A test that builds Apple native library outputs: a Mac with the runtime pack of <paramref name="target"/> and
/// its embed runtime.</summary>
public sealed class AppleLibraryFactAttribute : FactAttribute
{
    public AppleLibraryFactAttribute(string target)
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Apple native library outputs are built on a Mac.";
        else if (AquariusInstallation.Current is not { } installation || !File.Exists(Path.Combine(installation.RuntimePack(target), "embed", "CMakeLists.txt")))
            Skip = $"Needs the {target} runtime pack (scripts/build-runtime.ps1 -Targets {target}).";
    }
}

/// <summary>
/// Apple native library outputs (docs/native-libraries.md, phase 5): what any host can check (slices, install names,
/// versions, the iOS framework and xcframework layouts, the manifest's Apple keys, and the errors a build gives before it
/// needs a Mac) and, on a Mac with the packs, real builds audited with Apple's tools. The goldens of the Swift binding,
/// the module maps, the Info.plists and the exported symbols list are in ExportTest.
/// </summary>
[Collection(ExecutableCollection.Name)]
public class AppleLibraryTest
{
    private static readonly string Geometry = Path.Combine(AquariusInstallation.Require().Root, "tests", "exports", "geometry", "main.aqua");
    private static readonly Lazy<ExportTable> Table = new(() => {
        string wasm = CompileGeometry();
        try { return ExportTable.Read(WasmApplication.Load(wasm).Program)!; } finally { File.Delete(wasm); }
    });
    private static BindingContext Context(string platform, string? version = "1.2.0") =>
        new(Table.Value, new NativeLibrarySettings("geometry", "geo_", Version: version), platform);
    private static string CompileGeometry()
    {
        string output = Path.Combine(Path.GetTempPath(), "aquarius-apple-test-" + Guid.NewGuid().ToString("N") + ".wasm");
        WasmApplication.Compile([Geometry], output, Path.GetDirectoryName(Geometry));
        return output;
    }
    private static Dictionary<string, string> Kind(string kinds) => new() { [NativeLibraryOutput.KindOption] = kinds };

    [Theory]
    [InlineData("ios-arm64", "ios-arm64", null, "iPhoneOS")]
    [InlineData("ios-simulator-arm64", "ios-arm64-simulator", "simulator", "iPhoneSimulator")]
    [InlineData("macos-arm64", "macos-arm64", null, "MacOSX")]
    public void SlicesAreNamedAsXcframeworksNameThem(string target, string identifier, string? variant, string bundlePlatform)
    {
        var slice = AppleLibraryPackage.Slice.Of(RuntimeTarget.Get(target));
        Assert.Equal(identifier, slice.Identifier);
        Assert.Equal("arm64", slice.Architecture);
        Assert.Equal(variant, slice.Variant);
        Assert.Equal(bundlePlatform, slice.BundlePlatform);
    }

    [Fact]
    public void InstallNamesAreRpathRelative()
    {
        var mac = Context("macos");
        Assert.Equal("@rpath/libgeometry.dylib", AppleLibraryPackage.InstallName(mac, RuntimeTarget.Get("macos-arm64"), framework: false));
        Assert.Equal("@rpath/Geometry.framework/Versions/A/Geometry", AppleLibraryPackage.InstallName(mac, RuntimeTarget.Get("macos-arm64"), framework: true));
        Assert.Equal("@rpath/Geometry.framework/Geometry", AppleLibraryPackage.InstallName(Context("ios"), RuntimeTarget.Get("ios-arm64"), framework: true));
        var renamed = mac with { Settings = mac.Settings with { Platforms = new Dictionary<string, NativeLibraryPlatformSettings> { ["macos"] = new(Module: "GeoKit") } } };
        Assert.Equal("@rpath/GeoKit.framework/Versions/A/GeoKit", AppleLibraryPackage.InstallName(renamed, RuntimeTarget.Get("macos-arm64"), framework: true));
        Assert.Equal("GeoKit", SwiftBinding.Module(renamed));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", "1.0.0")]
    [InlineData("3.0.1-beta.2", "3.0.1", "3.0.0")]
    [InlineData(null, "0.0.0", "0.0.0")]
    public void DylibVersionsComeFromTheProjectVersion(string? version, string current, string compatibility)
    {
        var settings = new NativeLibrarySettings("geometry", Version: version);
        Assert.Equal((current, compatibility), AppleLibraryPackage.Versions(settings));
    }

    [Fact]
    public void TheExportedSymbolsListIsTheExportsWithMachONames()
    {
        var context = Context("macos");
        var lines = EmbedTable.MachOSymbols(context, java: true).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(EmbedTable.Symbols(context, java: true).Select(s => "_" + s), lines);
        Assert.Contains("_JNI_OnLoad", lines);
    }

    [Fact]
    public void IosFrameworksXcframeworksAndStaticSlicesHaveTheirLayout()
    {
        using var t = new TestFolder();
        var context = Context("ios");
        t.Write("include/geometry.h", "/* header */\n");
        string dylib = t.Write("built/libgeometry.dylib", "binary");
        string archive = t.Write("built/libgeometry.a", "archive");
        var device = AppleLibraryPackage.Slice.Of(RuntimeTarget.Get("ios-arm64"));
        string xcframework = t.FilePath("Geometry.xcframework");
        string framework = AppleLibraryPackage.WriteFramework(context, device, Path.Combine(xcframework, device.Identifier), dylib, t.FilePath("include"));
        Assert.Equal(Path.Combine(xcframework, "ios-arm64", "Geometry.framework"), framework);
        Assert.Equal("binary", File.ReadAllText(Path.Combine(framework, "Geometry")));
        Assert.True(File.Exists(Path.Combine(framework, "Headers", "geometry.h")));
        Assert.Contains("framework module Geometry", File.ReadAllText(Path.Combine(framework, "Modules", "module.modulemap")));
        Assert.Contains("<string>iPhoneOS</string>", File.ReadAllText(Path.Combine(framework, "Info.plist")));
        AppleLibraryPackage.WriteXcframework(xcframework, [(device, "Geometry.framework", "Geometry.framework/Geometry", null)]);
        Assert.Contains("<key>LibraryIdentifier</key><string>ios-arm64</string>", File.ReadAllText(Path.Combine(xcframework, "Info.plist")));

        string slice = t.FilePath("Geometry-static.xcframework/ios-arm64");
        AppleLibraryPackage.WriteStaticSlice(context, slice, archive, t.FilePath("include"));
        Assert.Equal("archive", File.ReadAllText(Path.Combine(slice, "libgeometry.a")));
        Assert.True(File.Exists(Path.Combine(slice, "Headers", "geometry.h")));
        Assert.Contains("module Geometry {", File.ReadAllText(Path.Combine(slice, "Headers", "module.modulemap")));
    }

    // ---- the manifest's Apple keys ----

    private const string Project = "manifest_version = 2\n[project]\nname = \"geometry\"\nversion = \"1.2.0\"\n[export]\n";
    private static ProjectManifest Manifest(string text) => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), text);

    [Fact]
    public void AppleTablesTakeModuleBundleIdPackageAndTargets()
    {
        var export = Manifest(Project + "[export.macos]\npackage = \"framework\"\nmodule = \"GeoKit\"\nbundle_id = \"org.example.geokit\"\nbindings = [\"c\", \"swift\"]\n" +
            "[export.ios]\nkinds = [\"shared\", \"static\"]\ntargets = [\"ios-arm64\"]\n").Export!;
        Assert.Equal("framework", export.For("macos").Package);
        Assert.Equal("GeoKit", export.For("macos").Module);
        Assert.Equal("org.example.geokit", export.For("macos").BundleId);
        Assert.Equal(["shared", "static"], export.KindsFor("ios"));
        Assert.Equal(["ios-arm64"], export.For("ios").Targets);
        Assert.Equal("org.example.geokit", AppleLibraryPackage.BundleId(new BindingContext(null!, export, "macos")));
        Assert.Equal("org.aquariuslang.library.geometry", AppleLibraryPackage.BundleId(new BindingContext(null!, export, "ios")));
    }

    [Theory]
    [InlineData("[export.macos]\nbundle_id = \"no dots\"\n", "bundle identifier")]
    [InlineData("[export.ios]\nkinds = [\"object\"]\n", "'object' is not one of them")]
    [InlineData("[export.ios]\npackage = \"framework\"\n", "must be one of xcframework, folder")]
    [InlineData("[export.ios]\njava_package = \"org.x\"\n", "Unknown [export.ios] key 'java_package'")]
    [InlineData("[export.ios]\ntargets = [\"macos-arm64\"]\n", "'macos-arm64' is not one of them")]
    [InlineData("[export.macos]\nmodule = \"Geo Kit\"\n", "ASCII identifier")]
    public void AppleTablesAreChecked(string table, string message)
    {
        var error = Assert.Throws<ManifestException>(() => Manifest(Project + table));
        Assert.Contains(message, error.Message);
    }

    // ---- builds: what fails before a Mac is needed, then real builds on a Mac ----

    [Fact]
    public void AppleBuildsCheckKindsAndTheHostBeforeTouchingTheOutput()
    {
        using var t = new TestFolder();
        string wasm = CompileGeometry();
        try {
            var ios = new IosTarget();
            var kinds = Assert.Throws<ArgumentException>(() => ios.Build(new WasmBuildRequest(wasm, t.FilePath("dist"), Options: Kind("object"))));
            Assert.Contains("ios builds shared, static native library outputs, not object", kinds.Message);
            if (!OperatingSystem.IsMacOS()) {
                var host = Assert.Throws<ArgumentException>(() => ios.Build(new WasmBuildRequest(wasm, t.FilePath("dist"), Options: Kind("shared"))));
                Assert.Contains("macos-arm64", host.Message);
                var mac = Assert.Throws<ArgumentException>(() => new ExecutableTarget("macos", "macos-arm64").Build(
                    new WasmBuildRequest(wasm, t.FilePath("dist"), Options: Kind("shared,static,object"))));
                Assert.Contains("macos-arm64 host", mac.Message);
            }
            Assert.False(Directory.Exists(t.FilePath("dist")));
        } finally { File.Delete(wasm); }
    }

    [Fact]
    public void ApplePlatformsAreTheOnesNotVerifiedYet()
    {
        Assert.Equal(["ios", "macos"], NativeLibraryPlatforms.All.Where(p => !p.Verified).Select(p => p.Platform).Order().ToArray());
        Assert.Contains("not verified on macos hardware yet", NativeLibraryOutput.UnverifiedNote("macos"));
    }

    private static RunResult Tool(string program, params string[] arguments) => Processes.Run(program, Path.GetTempPath(), arguments, timeoutMs: 120_000);

    [AppleLibraryFact("macos-arm64")]
    public void MacLibrariesExportExactlyTheirSymbolsWithAnRpathInstallName()
    {
        using var t = new TestFolder();
        string wasm = CompileGeometry();
        try {
            new ExecutableTarget("macos", "macos-arm64").Build(new WasmBuildRequest(wasm, t.FilePath("dist"), Options: Kind("shared,static,object"),
                Export: new NativeLibrarySettings("geometry", "geo_", Version: "1.2.0")));
            string root = t.FilePath("dist/macos-arm64");
            string dylib = Path.Combine(root, "lib", "libgeometry.dylib");
            Assert.Contains("@rpath/libgeometry.dylib", Tool("/usr/bin/otool", "-D", dylib).Output);
            Assert.Contains("current version 1.2.0", Tool("/usr/bin/otool", "-L", dylib).Output);
            var exported = Tool("/usr/bin/nm", "-gU", "-j", dylib).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order();
            Assert.Equal(EmbedTable.Symbols(Context("macos")).Select(s => "_" + s).Order(), exported);
            Assert.Equal(0, Tool("/usr/bin/codesign", "--verify", "--strict", dylib).ExitCode);
            foreach (string file in new[] { "lib/libgeometry.a", "lib/geometry.o" }) {
                var globals = Tool("/usr/bin/nm", "-gU", "-j", Path.Combine(root, file)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order();
                Assert.Equal(EmbedTable.Symbols(Context("macos")).Select(s => "_" + s).Order(), globals);
            }
            Assert.True(File.Exists(Path.Combine(root, "include", "module.modulemap")));
            Assert.True(File.Exists(Path.Combine(root, "swift", "Geometry.swift")));
        } finally { File.Delete(wasm); }
    }

    [AppleLibraryFact("ios-simulator-arm64")]
    public void IosLibrariesAreXcframeworksOfTheDeviceAndSimulatorSlices()
    {
        using var t = new TestFolder();
        string wasm = CompileGeometry();
        try {
            new IosTarget().Build(new WasmBuildRequest(wasm, t.FilePath("dist"), Options: Kind("shared,static"),
                Export: new NativeLibrarySettings("geometry", "geo_", Version: "1.2.0")));
            string root = t.FilePath("dist/ios");
            Assert.Equal(0, Tool("/usr/bin/plutil", "-lint", Path.Combine(root, "Geometry.xcframework", "Info.plist")).ExitCode);
            string simulator = Path.Combine(root, "Geometry.xcframework", "ios-arm64-simulator", "Geometry.framework", "Geometry");
            Assert.Contains("@rpath/Geometry.framework/Geometry", Tool("/usr/bin/otool", "-D", simulator).Output);
            Assert.True(File.Exists(Path.Combine(root, "Geometry-static.xcframework", "ios-arm64-simulator", "libgeometry.a")));
            Assert.Equal(0, Tool("/usr/bin/codesign", "--verify", Path.Combine(root, "Geometry.xcframework")).ExitCode);
        } finally { File.Delete(wasm); }
    }
}
