using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AquariusLang.Compiler;
using AquariusLang.Wasm;
using Xunit;

namespace AquariusTests.architecture;

/// <summary>
/// Enforced architecture rules (see ARCHITECTURE.md, "Enforced architecture rules"). Each failure message names the
/// rule and the legitimate way to change it. Everything here runs in plain `dotnet test` in a few seconds; the binary
/// reproducibility rule runs when the pinned Clang toolchain is installed (every CI job that builds installs it).
/// </summary>
[Trait("Category", "Architecture")]
public class ArchitectureRulesTest
{
    internal static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AquariusLang.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));
    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    private static readonly string[] Ignored = { "/bin/", "/obj/", "/node_modules/", "/.git/", "/.web-build/", "/.native-build/", "/.android-build/",
        "/dist/", "/.graphics-build/", "/.pbr-build/", "/.array-build/", "/.build-home/", "/build-targets/", "/TestResults/", "/.aqua/", "/vendor/" };
    /// <summary>Files under the repository, excluding build output, vendored code and ignored tool caches.</summary>
    private static IEnumerable<string> Files(string relativeDirectory, params string[] patterns) =>
        patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root, relativeDirectory), p, SearchOption.AllDirectories))
            .Where(f => !Ignored.Any(i => ("/" + Relative(f)).Contains(i, StringComparison.OrdinalIgnoreCase)))
            .Distinct().OrderBy(f => f, StringComparer.Ordinal);
    /// <summary>Tracked files (git ls-files) when git is available; otherwise every non-ignored file.</summary>
    private static IReadOnlyList<string> TrackedFiles()
    {
        try {
            var start = new ProcessStartInfo("git", "ls-files -z") { WorkingDirectory = Root, RedirectStandardOutput = true, UseShellExecute = false };
            using var git = Process.Start(start)!;
            string output = git.StandardOutput.ReadToEnd(); git.WaitForExit();
            if (git.ExitCode == 0) return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        } catch (System.ComponentModel.Win32Exception) { }
        return Files(".", "*").Select(Relative).ToArray();
    }

    // ---- Toolchain -----------------------------------------------------------------------------------------------

    /// <summary>Historical validation records may mention the retired Zig toolchain; nothing else may.</summary>
    private static readonly string[] ZigHistory = { "docs/pbr-validation.md", "docs/wasm-migration-progress.md", "ARCHITECTURE.md" /* states this rule */ };

    [Fact]
    public void NoZigToolchain()
    {
        var zig = new Regex(@"\bzig\b|ZIG_", RegexOptions.IgnoreCase);
        var offenders = TrackedFiles()
            .Where(f => Regex.IsMatch(f, @"\.(ps1|sh|ya?ml|csproj|props|targets|py|cmake|bat|json|md)$|CMakeLists\.txt$", RegexOptions.IgnoreCase))
            .Where(f => !ZigHistory.Contains(f) && !f.StartsWith("docs/archive/", StringComparison.Ordinal) && !f.StartsWith("native/vendor/", StringComparison.Ordinal))
            .Where(f => File.Exists(Path.Combine(Root, f)) && zig.IsMatch(Read(f)))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Rule 'No Zig': the repository builds every Wasm/native helper with the pinned LLVM/Clang toolchain " +
            "(scripts/wasm-toolchain.json, scripts/wasm-toolchain.ps1). Remove Zig invocations/ZIG_ variables from: " + string.Join(", ", offenders) +
            ". Historical mentions belong only in docs/archive/ or the listed validation records.");
    }

    [ToolchainFact]
    public void CommittedWasmBinariesRebuildFromSource()
    {
        foreach (var script in new[] { "build-wasm-runtime.ps1", "build-audio-runtime.ps1", "build-pose-wasm.ps1", "build-geometry-wasm.ps1" }) {
            var (code, output) = RunPowerShell(Path.Combine(Root, "scripts", script), "-Check");
            Assert.True(code == 0, $"Rule 'Reproducible Wasm binaries': scripts/{script} -Check failed, so a committed binary no longer " +
                $"matches its source. Rebuild it with scripts/{script} (pinned Clang) and commit the result.\n{output}");
        }
    }

    // ---- Generated sources ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("native/generate_runtime_layout.py")]
    [InlineData("native/generate_portable_libraries.py")]
    [InlineData("native/generate_library_catalog.py")]
    public void GeneratedSourcesAreFresh(string generator)
    {
        var (code, output) = RunPython(generator, "--check");
        Assert.True(code == 0, $"Rule 'Generated sources are fresh': {generator} --check reports stale output. Generated files are never edited " +
            $"by hand; change the generator's table (or the code it scans) and run `python {generator}`.\n{output}");
    }

    [Fact]
    public void GeneratedLibrariesHaveNoHandWrittenEntries()
    {
        // Libraries scanned by native/generate_library_catalog.py are described only by its output.
        var generated = Regex.Matches(Read("AquariusCore/runtime/LibraryCatalog.Generated.cs"), @"new\(""([^""]+)"", ""([^""]+)""")
            .Select(m => (Library: m.Groups[1].Value, Name: m.Groups[2].Value)).ToHashSet();
        var owned = generated.Select(e => e.Library).ToHashSet(StringComparer.Ordinal);
        var offenders = AquariusLang.runtime.LibraryCatalog.Functions.Where(f => owned.Contains(f.Library) && !generated.Contains((f.Library, f.EnglishName)))
            .Select(f => $"{f.Library}.{f.EnglishName}").ToList();
        Assert.True(offenders.Count == 0, "Rule 'Generated sources are fresh': these catalog entries belong to libraries generated by " +
            "native/generate_library_catalog.py but are written by hand elsewhere, so the catalog, LIBRARY_NAMES.md and the code can drift. " +
            "Register the function in code, add its translation to the generator's TRANSLATIONS table and regenerate: " + string.Join(", ", offenders));
    }

    // ---- Runtime layout and the single Wasm encoder ---------------------------------------------------------------

    [Fact]
    public void RuntimeIncludesGeneratedLayoutChecks()
    {
        Assert.True(Regex.IsMatch(Read("AquariusCore/wasm/runtime/runtime.c"), @"#include ""layout\.h"""),
            "Rule 'Runtime layout': runtime.c must #include \"layout.h\" (generated by native/generate_runtime_layout.py) so the C struct " +
            "offsets the C# emitters and hosts rely on are checked at compile time.");
    }

    private static readonly string[] Emitters = { "WasmCompiler.cs", "WasmBodyEmitter.cs", "WasmNumericFunction.cs", "WasmIntegerFunction.cs" };

    [Fact]
    public void EmittersUseNamedRuntimeOffsets()
    {
        // A load/store whose memarg offset is a raw number (other than 0) instead of a RuntimeLayout constant.
        var raw = new Regex(@"Memory\(\s*(?:w\s*,\s*)?0x(?:28|29|2b|36|37|39)\s*,\s*\d\s*,\s*(?!0\s*\))\d+\s*\)|(?:Load|Store)(?:I32|I64|F64)\(\s*[1-9]\d*\s*\)|\b56\s*\+\s*16\b");
        var offenders = Emitters.SelectMany(name => {
            string path = Path.Combine(Root, "AquariusCore/wasm", name);
            return File.ReadLines(path).Select((line, i) => (line, i)).Where(p => raw.IsMatch(p.line)).Select(p => $"{name}:{p.i + 1}");
        }).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Runtime layout': emitters must address guest structs through RuntimeLayout constants " +
            "(AquariusCore/wasm/WasmRuntimeLayout.Generated.cs), never raw offsets. Offenders: " + string.Join(", ", offenders) +
            ". Add a missing field to native/generate_runtime_layout.py and regenerate.");
    }

    [Fact]
    public void OneWasmEncoder()
    {
        // LEB128 loops (">>= 7") and private encoder helpers belong only to WasmEncoding.cs.
        var encoder = new Regex(@">>=\s*7\b|\bvoid\s+(?:WriteUnsigned|Signed|Leb\w*)\s*\(|private static void (?:U|S|I|L)\(BinaryWriter");
        var offenders = Files("AquariusCore/wasm", "*.cs").Where(f => Path.GetFileName(f) != "WasmEncoding.cs" && encoder.IsMatch(File.ReadAllText(f)))
            .Select(Relative).ToList();
        Assert.True(offenders.Count == 0, "Rule 'One Wasm encoder': LEB128 and instruction-immediate encoding lives only in " +
            "AquariusCore/wasm/WasmEncoding.cs (import it with `using static AquariusLang.Wasm.WasmEncoding;`). Remove the private encoder from: " +
            string.Join(", ", offenders));
    }

    // ---- ABI compatibility -----------------------------------------------------------------------------------------

    // Built by the ABI 2 compiler (commit 7fa27b7) from the sources beside it; never regenerate it with a newer compiler.
    private const string Abi2Artifact = "AquariusTests/compiler/fixtures/abi2-application.wasm";
    private const string Abi2Sha256 = "10f4af1017eb2ee8e27d323dce686276482c90743149dfdc7712bc373e22a53c";

    [Fact]
    public void Abi2ArtifactsStillLoadAndRun()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Root, Abi2Artifact));
        Assert.True(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == Abi2Sha256, $"Rule 'ABI 2 compatibility': {Abi2Artifact} " +
            "is the committed output of the ABI 2 compiler and must not change. Restore it from git; new hosts must keep loading it as is.");
        string fix = " New hosts must load ABI 2 artifacts: keep the JSON metadata reader in WasmProgram.LoadLegacy and readManifest " +
            "(AquariusCore/hosting/wasm-manifest.mjs), and keep the value ABI, function table and import module unchanged.";
        WasmProgram program;
        try { program = WasmProgram.Load(bytes); }
        catch (InvalidDataException e) { Assert.Fail("Rule 'ABI 2 compatibility': the ABI 2 artifact no longer loads (" + e.Message + ")." + fix); return; }
        Assert.True(program.Metadata.AbiVersion == WasmAbi.LegacyVersion && program.Functions[1].NumericTable != 0 &&
            System.Text.Encoding.UTF8.GetString(program.Assets["data/note.txt"].Span) == "abi2", "Rule 'ABI 2 compatibility': metadata, function layout or assets read wrongly." + fix);
        // Run it as shipped, and re-encoded by a new deployment step (external assets are written as ABI 3).
        string directory = Path.Combine(Path.GetTempPath(), "aquarius-abi2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            string original = Path.Combine(directory, "abi2.wasm"), external = Path.Combine(directory, "external.wasm");
            File.WriteAllBytes(original, bytes); File.WriteAllBytes(external, program.WithExternalAssets().Bytes.ToArray());
            foreach (string path in new[] { original, external }) {
                string result = AquariusLang.Desktop.runtime.ScriptRunner.RunWasm(path).Inspect();
                Assert.True(result == "[6765, 42, 49, 68, 3, 函式(a) {\n...\n}]", $"Rule 'ABI 2 compatibility': the ABI 2 artifact runs differently ({result})." + fix);
            }
        } finally { Directory.Delete(directory, true); }
    }

    // ---- Codegen stability -----------------------------------------------------------------------------------------

    private const string Goldens = "AquariusTests/architecture/codegen-goldens.txt";
    private static readonly string[] GoldenPrograms = {
        "examples/increment.aqua", "examples/increment_and_plus_equal.aqua", "examples/conditions.aqua", "examples/for_loop.aqua",
        "examples/nested_func.aqua", "examples/num_operations_casting.aqua", "examples/var_scope.aqua", "examples/comments.aqua",
        "examples/elapsed_time.aqua", "AquariusWebCompiler/tests/fixtures/portable/main.aqua"
    };

    [Fact]
    public void CompiledOutputMatchesGoldens()
    {
        var actual = GoldenPrograms.Where(p => File.Exists(Path.Combine(Root, p))).Select(p => {
            // Multi-line string literals would otherwise follow the checkout's line endings (CRLF on Windows, LF elsewhere).
            var bytes = new WasmCompiler().Compile(Read(p).Replace("\r\n", "\n")).Bytes.ToArray();
            return $"{p}\t{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\t{bytes.Length}";
        }).ToList();
        Assert.True(actual.Count >= 8, "Rule 'Codegen stability': most golden programs are missing; update GoldenPrograms.");
        string path = Path.Combine(Root, Goldens), text = string.Join("\n", actual) + "\n";
        if (Environment.GetEnvironmentVariable("AQUARIUS_UPDATE_CODEGEN_GOLDENS") == "1") File.WriteAllText(path, text);
        string expected = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        Assert.True(expected == text, "Rule 'Codegen stability': compiled Wasm for the golden programs changed. If the emitter or " +
            "runtime change is intended, review it and update the goldens with AQUARIUS_UPDATE_CODEGEN_GOLDENS=1 dotnet test --filter " +
            $"CompiledOutputMatchesGoldens, then commit {Goldens}.\nExpected:\n{expected}\nActual:\n{text}");
    }

    // ---- Dependency direction --------------------------------------------------------------------------------------

    private static readonly string[] HostPackages = { "Silk.NET", "Wasmtime", "Microsoft.ClearScript", "Microsoft.ML.OnnxRuntime", "Magick.NET", "Tomlyn", "Jolt" };
    private static IEnumerable<string> References(string project, string kind) =>
        XDocument.Load(Path.Combine(Root, project)).Descendants(kind).Select(e => (string?)e.Attribute("Include") ?? "");

    [Fact]
    public void CoreDependsOnNoHostOrProject()
    {
        var projects = References("AquariusCore/AquariusCore.csproj", "ProjectReference").ToList();
        var packages = References("AquariusCore/AquariusCore.csproj", "PackageReference").Where(p => HostPackages.Any(h => p.StartsWith(h, StringComparison.OrdinalIgnoreCase))).ToList();
        var assemblies = typeof(WasmCompiler).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "")
            .Where(n => HostPackages.Any(h => n.StartsWith(h, StringComparison.OrdinalIgnoreCase)) || n.StartsWith("Aquarius", StringComparison.Ordinal)).ToList();
        Assert.True(projects.Count == 0 && packages.Count == 0 && assemblies.Count == 0,
            "Rule 'Dependency direction': AquariusCore is the portable core; hosts depend on it, never the reverse, and it references no " +
            "host/native packages. Move host code into a host project (AquariusDesktop, AquariusWebCompiler, AquariusAndroid...). Found: " +
            string.Join(", ", projects.Concat(packages).Concat(assemblies)));
    }

    [Fact]
    public void PackagingDependsOnlyOnCore()
    {
        var projects = References("AquariusPackaging/AquariusPackaging.csproj", "ProjectReference").Select(p => Path.GetFileName(p.Replace('\\', '/'))).ToList();
        var packages = References("AquariusPackaging/AquariusPackaging.csproj", "PackageReference").Where(p => HostPackages.Any(h => p.StartsWith(h, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(projects.SequenceEqual(new[] { "AquariusCore.csproj" }) && packages.Count == 0,
            "Rule 'Dependency direction': AquariusPackaging depends only on AquariusCore (ARCHITECTURE.md). Found project references: " +
            string.Join(", ", projects) + "; host packages: " + string.Join(", ", packages));
    }

    // ---- Library admission ------------------------------------------------------------------------------------------

    /// <summary>Bundled libraries that are documented host-capability exceptions to the Wasm rule (docs/external-libraries.md).</summary>
    private static readonly string[] HostCapabilityExceptions = { "webgpu", "network", "webcam", "onnx" };

    [Fact]
    public void BundledComputationalLibrariesExecuteInWasm()
    {
        var offenders = AquariusLang.External.ExternalLibraries.All
            .Where(l => l.Profile == AquariusLang.External.ExternalLibraryProfile.HostCapability && !HostCapabilityExceptions.Contains(l.Id))
            .Select(l => l.Id).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Library admission': a compiler-bundled computational library executes in Wasm " +
            "(docs/external-libraries.md#requirement-for-future-libraries). Only documented host capabilities (" + string.Join(", ", HostCapabilityExceptions) +
            ") may use HostCapability. Port these to CoreWasm or document the exception: " + string.Join(", ", offenders));
    }

    // ---- Shared JavaScript -----------------------------------------------------------------------------------------

    /// <summary>Browser modules that intentionally implement a same-named hosting module for the DOM (the core one is a headless stub).</summary>
    private static readonly string[] BrowserImplementations = { "surface.mjs" };

    [Fact]
    public void BrowserModulesReExportCoreHosting()
    {
        var tracked = new HashSet<string>(TrackedFiles(), StringComparer.Ordinal);
        var shared = Directory.EnumerateFiles(Path.Combine(Root, "AquariusCore/hosting"), "*.mjs").Select(Path.GetFileName).ToHashSet();
        var reExport = new Regex(@"^\s*export\s+(\*|\{[^}]*\})\s+from\s+['""]\.\./\.\./AquariusCore/[\w./-]+\.mjs['""];?\s*$");
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "AquariusWebCompiler/browser"), "*.mjs")
            .Where(f => shared.Contains(Path.GetFileName(f)) && !BrowserImplementations.Contains(Path.GetFileName(f)) && tracked.Contains(Relative(f)))
            .Where(f => File.ReadAllLines(f).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//")).Any(l => !reExport.IsMatch(l)))
            .Select(Relative).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Single source for shared JS': a browser module named like an AquariusCore/hosting module must " +
            "only re-export it (export * from '../../AquariusCore/hosting/NAME.mjs'), never copy it. Replace the copy in: " + string.Join(", ", offenders));
    }

    // ---- Host bridge -----------------------------------------------------------------------------------------------

    private static readonly string[] HostBridges = { "AquariusCore/hosting/wasm-host.mjs", "AquariusCore/wasm/WasmHostRuntime.cs" };

    [Theory]
    [InlineData("AquariusCore/hosting/wasm-host.mjs", "'aqua_stale' 'aqua_take_released' 'aqua_take_dirty_array' 'aqua_take_dirty_hash' 'aqua_array_items' 'aqua_weak_mirrors' 'aqua_pending'")]
    [InlineData("AquariusCore/wasm/WasmHostRuntime.cs", "exports.Stale( exports.TakeReleased() exports.TakeDirtyArray() exports.TakeDirtyHash() exports.ArrayItems( exports.WeakMirrors() exports.Pending()")]
    public void HostsTranslateRuntimeMirrorDecisions(string host, string runtimeDecisions)
    {
        // The runtime decides which mirrors are stale (aqua_stale), which guest objects were dropped
        // (aqua_take_released) and which changed (aqua_take_dirty_*); hosts only translate values.
        string source = Read(host);
        var missing = runtimeDecisions.Split(' ').Where(call => !source.Contains(call, StringComparison.Ordinal)).ToList();
        var copying = new Regex(@"\b[Ss]ynchronizeIn\b|foreach\s*\(var binding in Bindings\(|for\(const \[n,v\]\s*of this\.bindings\(");
        var unconditional = new Regex(@"if\s*\(\s*!refreshingDirtyHashes\s*\)\s*Refresh\(|if\(!this\.refreshingDirtyHashes\)this\.refresh\(");
        var problems = missing.Select(name => "does not consume " + name).ToList();
        if (copying.IsMatch(source)) problems.Add("copies scope bindings between host and guest");
        if (unconditional.IsMatch(source)) problems.Add("refreshes cached mirrors without asking the runtime whether they are stale");
        Assert.True(problems.Count == 0, $"Rule 'Hosts contain no mirroring algorithm': {host} {string.Join("; ", problems)}. Mirror freshness, " +
            "release and change tracking are runtime decisions (runtime.c exports); host scopes are live views of guest scopes (attach). " +
            "Use the runtime exports instead of re-reading or copying, as both hosts in " + string.Join(" and ", HostBridges) + " do.");
    }

    // ---- Processing geometry -------------------------------------------------------------------------------------

    /// <summary>Hosts that draw Processing sketches: the shared JavaScript canvas (browser and Android) and the desktop canvas.</summary>
    private static readonly string[] ProcessingHosts = { "AquariusCore/hosting/processing.mjs", "AquariusDesktop/graphics/ProcessingCanvas.cs",
        "AquariusDesktop/graphics/ProcessingDrawing.cs" };

    [Fact]
    public void ProcessingGeometryLivesInTheKernel()
    {
        // Triangulation, stroke joins/caps, ellipses, rounded rectangles, curves and box/sphere meshes are geometry.c; hosts
        // keep device, texture, font and window access and upload the kernel's vertex ranges.
        var tessellation = new Regex(@"\bearcut\b|\b[Tt]riangulate\s*\(|\bMiterStrip\b|\bsegmentVertices\b|\bStrokeOffset\b|\bear[Cc]lip");
        var problems = new List<string>();
        var hostFiles = ProcessingHosts.Concat(Files("AquariusDesktop/graphics", "*.cs").Select(Relative))
            .Concat(Files("AquariusWebCompiler/browser", "*.mjs").Select(Relative)).Concat(Files("AquariusAndroid/template", "*.mjs").Select(Relative))
            .Concat(Files("AquariusCore/hosting", "*.mjs").Select(Relative)).Distinct();
        foreach (var file in hostFiles)
            foreach (Match m in tessellation.Matches(Read(file))) problems.Add($"{file} tessellates ({m.Value})");
        foreach (var host in ProcessingHosts.Take(2))
            foreach (var operation in new[] { "geo_polygon", "geo_polyline", "geo_rect", "geo_ellipse", "geo_curve" })
                if (!Read(host).Contains(operation, StringComparison.Ordinal)) problems.Add($"{host} does not draw through {operation}");
        if (Read("AquariusWebCompiler/package.json").Contains("\"earcut\"", StringComparison.Ordinal)) problems.Add("AquariusWebCompiler/package.json depends on earcut");
        Assert.True(problems.Count == 0, "Rule 'Processing geometry lives in the kernel': " + string.Join("; ", problems) + ". Shape tessellation " +
            "belongs in AquariusCore/graphics/geometry.c (rebuilt with scripts/build-geometry-wasm.ps1) so every host draws the same pixels; add or " +
            "extend a geo_* operation there and call it through GeometryKernel (C#) or geometry-core.mjs (JavaScript).");
    }

    // ---- Repository hygiene ----------------------------------------------------------------------------------------

    [Fact]
    public void NoCommittedIdeOrUserFiles()
    {
        var offenders = TrackedFiles().Where(f => f.Contains(".idea/", StringComparison.Ordinal) || f.StartsWith(".idea", StringComparison.Ordinal)
            || f.EndsWith(".DotSettings.user", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".user", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Repository hygiene': IDE and per-user files are not committed (.gitignore covers them). " +
            "git rm --cached: " + string.Join(", ", offenders.Take(20)));
    }

    [Fact]
    public void ExamplesHaveOneTree()
    {
        // Aquarius sources live in examples/, in test fixtures, or are core libraries embedded in AquariusCore/graphics.
        static bool Allowed(string f) => f.StartsWith("examples/", StringComparison.Ordinal) || f.StartsWith("AquariusTests/", StringComparison.Ordinal) ||
            f.Contains("/tests/", StringComparison.Ordinal) || Regex.IsMatch(f, @"\AAquariusCore/graphics/[^/]+\.aqua\z");
        var offenders = TrackedFiles().Where(f => f.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) && !Allowed(f))
            .Select(f => f[..(f.LastIndexOf('/') + 1)]).Distinct().ToList();
        Assert.True(offenders.Count == 0, "Rule 'One example tree': examples/ is the only sample tree; desktop builds ship it and tests read it " +
            "there. Express platform variants inside the example (a desktop.aqua entry beside main.aqua, or a width/platform " +
            "condition) and move these samples into examples/: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DocsUseRepositoryRelativeLinks()
    {
        var machine = new Regex(@"[A-Za-z]:[/\\](?:OfficialProjects|Users)[/\\]", RegexOptions.IgnoreCase);
        var offenders = TrackedFiles().Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("docs/archive/", StringComparison.Ordinal))
            .Where(f => File.Exists(Path.Combine(Root, f)))
            .SelectMany(f => File.ReadLines(Path.Combine(Root, f)).Select((l, i) => (f, l, i)).Where(p => machine.IsMatch(p.l)).Select(p => $"{p.f}:{p.i + 1}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Rule 'Repository hygiene': Markdown must not contain absolute machine paths (C:/OfficialProjects/..., " +
            "C:\\Users\\...); use repository-relative links or describe the location. Offenders: " + string.Join(", ", offenders.Take(30)));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static (int Code, string Output) Run(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // A PSModulePath inherited from a different PowerShell edition (pwsh 7 running the tests, Windows PowerShell running
        // the scripts) hides the child's own built-in modules (Get-FileHash); let each edition compute its default.
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
    // (executable, leading arguments); on Windows the bare python/python3 names often resolve to the Store stubs, so the py launcher follows.
    internal static (string File, string Prefix)? Python()
    {
        var candidates = new List<(string?, string)> { (Environment.GetEnvironmentVariable("AQUARIUS_PYTHON"), ""), ("python3", ""), ("python", "") };
        if (OperatingSystem.IsWindows()) candidates.Add(("py", "-3 "));
        foreach (var (file, prefix) in candidates.Where(c => !string.IsNullOrEmpty(c.Item1))) {
            try { if (Run(file!, prefix + "--version").Code == 0) return (file!, prefix); } catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
    private static (int Code, string Output) RunPython(string script, string arguments)
    {
        var python = Python();
        Assert.True(python != null, "The architecture rules run the repository's Python generators; install Python 3 or set AQUARIUS_PYTHON.");
        return Run(python!.Value.File, $"{python.Value.Prefix}\"{Path.Combine(Root, script)}\" {arguments}");
    }
    internal static string PowerShell => OperatingSystem.IsWindows() ? "powershell" : "pwsh";
    private static (int Code, string Output) RunPowerShell(string script, string arguments) =>
        Run(PowerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {arguments}");
    internal static bool CanRun(string file)
    {
        try { return Run(file, "-NoProfile -Command exit 0").Code == 0; } catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>Runs only where the pinned LLVM/Clang toolchain is installed (scripts/install-wasm-toolchain.ps1).</summary>
public sealed class ToolchainFactAttribute : FactAttribute
{
    public ToolchainFactAttribute()
    {
        string toolchains = Path.Combine(ArchitectureRulesTest.Root, ".native-build", "toolchains");
        bool installed = Directory.Exists(toolchains) && Directory.EnumerateDirectories(toolchains, "llvm-*").Any();
        if (!installed && Environment.GetEnvironmentVariable("AQUARIUS_LLVM") == null)
            Skip = "Install the pinned Clang toolchain (scripts/install-wasm-toolchain.ps1) to check that committed Wasm binaries rebuild from source.";
        else if (!ArchitectureRulesTest.CanRun(ArchitectureRulesTest.PowerShell))
            Skip = "Install PowerShell 7 (pwsh) to run the Wasm build scripts' -Check mode; CI runners include it.";
    }
}
