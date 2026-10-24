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
        foreach (var script in new[] { "build-wasm-runtime.ps1", "build-audio-runtime.ps1", "build-pose-wasm.ps1" }) {
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
            var bytes = new WasmCompiler().Compile(Read(p)).Bytes.ToArray();
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
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
    internal static string? Python()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("AQUARIUS_PYTHON"), "python3", "python" };
        foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c))) {
            try { if (Run(candidate!, "--version").Code == 0) return candidate; } catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
    private static (int Code, string Output) RunPython(string script, string arguments)
    {
        string? python = Python();
        Assert.True(python != null, "The architecture rules run the repository's Python generators; install Python 3 or set AQUARIUS_PYTHON.");
        return Run(python!, $"\"{Path.Combine(Root, script)}\" {arguments}");
    }
    private static (int Code, string Output) RunPowerShell(string script, string arguments)
    {
        string shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        return Run(shell, $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {arguments}");
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
    }
}
