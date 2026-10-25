using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Wasm;

namespace AquariusTests.architecture;

/// <summary>
/// The owner's architecture principles, enforced. Each failure names the rule and the legitimate way to change things.
/// <list type="number">
/// <item>C# is only the compiler: no C# outside compiler/, and no NuGet package that would run programs.</item>
/// <item>No JavaScript in any runtime or library: JavaScript lives in web-glue/ (exposing Web APIs only); the editor
/// extension, the project website and the browser test harness are tools, not runtime code.</item>
/// <item>Each library is one self-contained folder with a valid aqua.toml; its web glue is a self-contained domain.</item>
/// <item>No code generators (no Python, no generated sources); X-macro tables instead.</item>
/// <item>Windows builds use Clang + MinGW, never MSVC.</item>
/// <item>The compiler's guest runtime contract and codegen stay deliberate (layout table, one encoder, goldens).</item>
/// </list>
/// </summary>
[Trait("Category", "Architecture")]
public class ArchitectureRulesTest
{
    internal static readonly string Root = AquariusInstallation.Require().Root;
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));
    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    private static readonly string[] Ignored = ["/bin/", "/obj/", "/node_modules/", "/.git/", "/.native-build/", "/dist/", "/build/", "/TestResults/", "/.aqua/"];

    /// <summary>The repository's files: tracked and new (not ignored) files when git is available, else every file
    /// outside build output.</summary>
    private static IReadOnlyList<string> RepositoryFiles() => repositoryFiles.Value;
    private static readonly Lazy<IReadOnlyList<string>> repositoryFiles = new(() => {
        try {
            var start = new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard") { WorkingDirectory = Root, RedirectStandardOutput = true, UseShellExecute = false };
            using var git = Process.Start(start)!;
            string output = git.StandardOutput.ReadToEnd(); git.WaitForExit();
            if (git.ExitCode == 0) return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(Path.Combine(Root, f))).ToArray();
        } catch (System.ComponentModel.Win32Exception) { }
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(Relative)
            .Where(f => !Ignored.Any(i => ("/" + f).Contains(i, StringComparison.OrdinalIgnoreCase))).ToArray();
    });
    private static IEnumerable<string> FilesWith(params string[] extensions) =>
        RepositoryFiles().Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    // ---- 1. C# is only the compiler --------------------------------------------------------------------------------

    [Fact]
    public void CSharpIsOnlyTheCompiler()
    {
        var offenders = FilesWith(".cs", ".csproj").Where(f => !f.StartsWith("compiler/", StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0, "Rule 'C# is only the compiler': C# and .NET projects live in compiler/; every target's runtime and " +
            "every library is C/C++ (runtime/, libraries/) or Aquarius. Move or remove: " + string.Join(", ", offenders));
    }

    private static readonly string[] RuntimePackages = ["Wasmtime", "Microsoft.ClearScript", "Magick.NET", "Silk.NET", "Microsoft.ML.OnnxRuntime", "Jolt"];

    [Fact]
    public void CompilerProjectsCarryNoRuntimePackages()
    {
        var projects = FilesWith(".csproj").ToList();
        Assert.NotEmpty(projects);
        var offenders = projects.SelectMany(p => XDocument.Load(Path.Combine(Root, p)).Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "").Where(name => RuntimePackages.Any(r => name.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            .Select(name => $"{p}: {name}")).ToList();
        Assert.True(offenders.Count == 0, "Rule 'C# is only the compiler': no .NET project references a package that runs programs or " +
            "libraries (engines, script hosts, codecs, GPU or inference runtimes); that work belongs to runtime/ and libraries/. " + string.Join(", ", offenders));
        var compiler = XDocument.Load(Path.Combine(Root, "compiler/AquariusCompiler/AquariusCompiler.csproj"));
        Assert.True(!compiler.Descendants("ProjectReference").Any(), "Rule 'Dependency direction': AquariusCompiler references no other project; " +
            "build targets, Bottle, the CLI and the language server depend on it.");
    }

    [Fact]
    public void TheSolutionHoldsTheCompilerProjects()
    {
        string solution = Read("AquariusLang.sln");
        var listed = Regex.Matches(solution, @"""(compiler\\[^""]+\.csproj)""").Select(m => m.Groups[1].Value.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        var present = FilesWith(".csproj").Where(f => f.StartsWith("compiler/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.True(listed.SequenceEqual(present), "Rule 'One solution': AquariusLang.sln lists every compiler/ project. Listed: " +
            string.Join(", ", listed) + "; present: " + string.Join(", ", present));
    }

    // ---- 2. JavaScript only in web-glue/ ---------------------------------------------------------------------------

    /// <summary>Development tools that never ship with applications: the VS Code extension, the project website and scripts/.</summary>
    private static readonly string[] JavaScriptTools = ["editors/vscode/", "website/", "scripts/"];

    [Fact]
    public void JavaScriptLivesOnlyInWebGlue()
    {
        var offenders = FilesWith(".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx")
            .Where(f => !f.StartsWith("web-glue/", StringComparison.Ordinal) && !JavaScriptTools.Any(t => f.StartsWith(t, StringComparison.Ordinal))).ToList();
        Assert.True(offenders.Count == 0, "Rule 'No JavaScript in runtimes or libraries': JavaScript exists only in web-glue/, to expose Web APIs " +
            "to C/C++ compiled to WebAssembly. Port the logic to the library's C/C++ (or Aquarius) and keep at most a thin glue domain: " + string.Join(", ", offenders));
    }

    [Fact]
    public void WebGlueDomainsAreSelfContained()
    {
        var import = new Regex(@"(?:import|export)\s[^'""]*?from\s*['""]([^'""]+)['""]|import\s*['""]([^'""]+)['""]|import\(\s*['""]([^'""]+)['""]\s*\)");
        var offenders = new List<string>();
        foreach (string file in FilesWith(".mjs", ".js").Where(f => f.StartsWith("web-glue/", StringComparison.Ordinal))) {
            string[] parts = file.Split('/');
            if (parts.Length < 3) continue; // web-glue/index.mjs imports every domain
            string domain = parts[1];
            foreach (Match m in import.Matches(Read(file))) {
                string target = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (!target.StartsWith('.')) { offenders.Add($"{file} imports {target}"); continue; }
                string resolved = Relative(Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(file)!, target)));
                bool own = resolved.StartsWith($"web-glue/{domain}/", StringComparison.Ordinal);
                bool core = resolved == "web-glue/core/glue.mjs" || (domain is "core" or "page") && resolved.StartsWith("web-glue/", StringComparison.Ordinal);
                if (!own && !core) offenders.Add($"{file} imports {target}");
            }
        }
        Assert.True(offenders.Count == 0, "Rule 'Self-contained glue domains': a web-glue/<domain>/ folder imports only its own files and " +
            "../core/glue.mjs (no packages, no other domains), so the folder can move to its own repository: " + string.Join(", ", offenders));
    }

    // ---- 3. Libraries ------------------------------------------------------------------------------------------------

    private static IEnumerable<string> LibraryFolders() => Directory.Exists(Path.Combine(Root, "libraries"))
        ? Directory.EnumerateDirectories(Path.Combine(Root, "libraries")).Order(StringComparer.Ordinal) : [];

    [Fact]
    public void EveryLibraryHasAValidManifest()
    {
        var problems = new List<string>(); var libraries = new List<Library>();
        foreach (string folder in LibraryFolders()) {
            string name = Path.GetFileName(folder);
            if (!File.Exists(Path.Combine(folder, Library.ManifestName))) { problems.Add($"libraries/{name} has no aqua.toml"); continue; }
            try {
                var library = Library.Load(Path.Combine(folder, Library.ManifestName));
                libraries.Add(library);
                if (library.Name != name) problems.Add($"libraries/{name}/aqua.toml names the library {library.Name}");
                if (library.HasNative && !File.Exists(Path.Combine(folder, "CMakeLists.txt"))) problems.Add($"libraries/{name} has a [native] part but no CMakeLists.txt");
            } catch (IOException e) { problems.Add(e.Message); }
        }
        try { _ = new LibrarySet(libraries); } catch (InvalidDataException e) { problems.Add(e.Message); }
        Assert.True(problems.Count == 0, "Rule 'Self-contained libraries': every libraries/<name>/ folder has an aqua.toml (manifest_version = 2, " +
            "[library] name = the folder name), a CMakeLists.txt for its [native] part, and module names no other library uses: " + string.Join("; ", problems));
    }

    [Fact]
    public void LibrariesReachNothingOutsideTheirFolder()
    {
        var offenders = new List<string>();
        var include = new Regex(@"#\s*include\s*""([^""]+)""");
        foreach (string folder in LibraryFolders()) {
            string root = Relative(folder) + "/";
            foreach (string file in RepositoryFiles().Where(f => f.StartsWith(root, StringComparison.Ordinal) && !f.StartsWith(root + "third_party/", StringComparison.Ordinal))) {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".inc" or ".def" or ".m" or ".mm")
                    foreach (Match m in include.Matches(Read(file))) {
                        string resolved = Relative(Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(file)!, m.Groups[1].Value)));
                        if (m.Groups[1].Value.Contains("..") && !resolved.StartsWith(root, StringComparison.Ordinal)) offenders.Add($"{file}: #include \"{m.Groups[1].Value}\"");
                    }
                if (Path.GetFileName(file) == "CMakeLists.txt" || extension == ".cmake")
                    foreach (Match m in Regex.Matches(Read(file), @"\$\{CMAKE_CURRENT_(?:SOURCE|LIST)_DIR\}/\.\./|(?<![\w/])\.\./\.\./"))
                        offenders.Add($"{file}: {m.Value}");
            }
        }
        Assert.True(offenders.Count == 0, "Rule 'Self-contained libraries': a library uses only its own folder, the SDK headers (<aqua/...>) and " +
            "the toolchain's pinned SDKs, so it could become its own repository: " + string.Join(", ", offenders));
    }

    /// <summary>The compiler links applications by the module names manifests declare; the runtime registers the names the
    /// code defines (aqua_define_module). Every library in the host runtime pack must agree with its manifest.</summary>
    [RuntimeFact]
    public void NativeModulesInManifestsAreTheOnesTheRuntimeRegisters()
    {
        using var catalog = System.Text.Json.JsonDocument.Parse(AquariusLang.Build.NativeRuntime.Catalog());
        var manifests = LibrarySet.Default;
        var problems = new List<string>();
        foreach (var entry in catalog.RootElement.GetProperty("libraries").EnumerateArray()) {
            string name = entry.GetProperty("name").GetString()!;
            if (name == "core") continue;
            var registered = entry.GetProperty("modules").EnumerateArray().Where(m => m.GetProperty("module").GetBoolean())
                .Select(m => string.Join("|", m.GetProperty("names").EnumerateArray().Select(n => n.GetString()))).ToHashSet(StringComparer.Ordinal);
            var library = manifests.Libraries.FirstOrDefault(l => l.NativeName == name);
            if (library == null) { problems.Add($"the runtime registers the library {name}, which has no manifest under libraries/"); continue; }
            var declared = library.NativeModules.Select(aliases => string.Join("|", aliases)).ToHashSet(StringComparer.Ordinal);
            foreach (string module in declared.Except(registered)) problems.Add($"{name} declares {module} but does not register it");
            foreach (string module in registered.Except(declared)) problems.Add($"{name} registers {module} but its aqua.toml does not declare it");
        }
        Assert.True(problems.Count == 0, "Rule 'Manifests describe libraries': [native] modules lists exactly the modules the library registers " +
            "(names as passed to aqua_define_module), since the compiler links applications by those names: " + string.Join("; ", problems));
    }

    // ---- 4. No code generators ---------------------------------------------------------------------------------------

    [Fact]
    public void NoCodeGenerators()
    {
        var offenders = FilesWith(".py", ".pyw", ".rb", ".pl").Where(f => !f.StartsWith("examples/", StringComparison.Ordinal)).ToList();
        offenders.AddRange(RepositoryFiles().Where(f => Regex.IsMatch(Path.GetFileName(f), @"\.(Generated|generated|gen)\.(cs|c|h|mjs|js|cpp)$")));
        Assert.True(offenders.Count == 0, "Rule 'No code generators': tables are X-macro .def files included by the code that uses them " +
            "(and read by the compiler where it needs them, like runtime/guest/layout.def); no Python or generated sources. Examples may " +
            "carry scripts their programs run. Offenders: " + string.Join(", ", offenders));
    }

    // ---- 5. Windows builds use Clang + MinGW -------------------------------------------------------------------------

    [Fact]
    public void WindowsBuildsNeverUseMsvc()
    {
        string toolchain = Read("runtime/sdk/cmake/toolchains/windows-x64.cmake");
        Assert.True(toolchain.Contains("x86_64-w64-mingw32", StringComparison.Ordinal) && !Regex.IsMatch(toolchain, @"clang-cl|\bcl\.exe|link\.exe", RegexOptions.IgnoreCase),
            "Rule 'No MSVC': runtime/sdk/cmake/toolchains/windows-x64.cmake targets x86_64-w64-mingw32 with the pinned Clang and llvm-mingw.");
        var msvc = new Regex(@"\bMSVC\b|clang-cl|\bcl\.exe|/MD\b|/MT\b|vcvars|Visual Studio \d+", RegexOptions.IgnoreCase);
        var offenders = RepositoryFiles().Where(f => (f.StartsWith("runtime/", StringComparison.Ordinal) || f.StartsWith("libraries/", StringComparison.Ordinal))
                && (Path.GetFileName(f) == "CMakeLists.txt" || f.EndsWith(".cmake", StringComparison.Ordinal)) && !f.Contains("/third_party/", StringComparison.Ordinal))
            .Where(f => Read(f).Split('\n').Any(line => msvc.IsMatch(line.Split('#')[0]))).ToList(); // comments may name what is not used
        Assert.True(offenders.Count == 0, "Rule 'No MSVC': Windows binaries build with Clang for x86_64-w64-mingw32 (runtime/sdk/cmake/toolchains); " +
            "CMake files have no MSVC branches or flags: " + string.Join(", ", offenders));
    }

    // ---- 6. The compiler's contract with the guest runtime -----------------------------------------------------------

    [Fact]
    public void GuestRuntimeChecksTheSharedLayoutTable()
    {
        Assert.True(Regex.IsMatch(Read("runtime/guest/runtime.c"), @"#include ""layout\.h"""),
            "Rule 'Runtime layout': runtime/guest/runtime.c includes layout.h, which checks the offsets of runtime/guest/layout.def, the table the compiler reads.");
        Assert.Contains("layout.def", Read("compiler/AquariusCompiler/AquariusCompiler.csproj"));
    }

    private static readonly string[] Emitters = ["WasmCompiler.cs", "WasmBodyEmitter.cs", "WasmNumericFunction.cs", "WasmIntegerFunction.cs"];

    [Fact]
    public void EmittersUseNamedRuntimeOffsets()
    {
        var raw = new Regex(@"Memory\(\s*(?:w\s*,\s*)?0x(?:28|29|2b|36|37|39)\s*,\s*\d\s*,\s*(?!0\s*\))\d+\s*\)|(?:Load|Store)(?:I32|I64|F64)\(\s*[1-9]\d*\s*\)|\b56\s*\+\s*16\b");
        var offenders = Emitters.SelectMany(name => File.ReadLines(Path.Combine(Root, "compiler/AquariusCompiler/wasm", name)).Select((line, i) => (line, i))
            .Where(p => raw.IsMatch(p.line)).Select(p => $"{name}:{p.i + 1}")).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Runtime layout': emitters address guest structs through RuntimeLayout constants (read from " +
            "runtime/guest/layout.def), never raw offsets. Add the field to layout.def. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void OneWasmEncoder()
    {
        var encoder = new Regex(@">>=\s*7\b|\bvoid\s+(?:WriteUnsigned|Signed|Leb\w*)\s*\(|private static void (?:U|S|I|L)\(BinaryWriter");
        var offenders = FilesWith(".cs").Where(f => f.StartsWith("compiler/AquariusCompiler/", StringComparison.Ordinal) && Path.GetFileName(f) != "WasmEncoding.cs")
            .Where(f => encoder.IsMatch(Read(f))).ToList();
        Assert.True(offenders.Count == 0, "Rule 'One Wasm encoder': LEB128 and instruction immediates are encoded only by " +
            "compiler/AquariusCompiler/wasm/WasmEncoding.cs. Remove the private encoder from: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheCompilerEmitsTheAbiTheRuntimeReads()
    {
        Assert.Equal(4, WasmAbi.Version);
        string host = Read("runtime/host/application.c");
        Assert.True(host.Contains("abi != 3 && abi != 4", StringComparison.Ordinal) && host.Contains("abi >= 4", StringComparison.Ordinal),
            "Rule 'One application ABI': runtime/host/application.c reads the metadata version the compiler writes (WasmAbi.Version).");
        Assert.Contains("\"AQUARIUS-APP-V2!\"u8", Read("compiler/AquariusBuild/ExecutableBundle.cs"));
        Assert.Contains("'A', 'Q', 'U', 'A', 'R', 'I', 'U', 'S', '-', 'A', 'P', 'P', '-', 'V', '2', '!'", Read("runtime/platform/desktop/main.c"));
    }

    private const string Goldens = "compiler/AquariusTests/architecture/codegen-goldens.txt";
    private static readonly string[] GoldenPrograms = [
        "examples/increment.aqua", "examples/increment_and_plus_equal.aqua", "examples/conditions.aqua", "examples/for_loop.aqua",
        "examples/nested_func.aqua", "examples/num_operations_casting.aqua", "examples/var_scope.aqua", "examples/comments.aqua", "examples/elapsed_time.aqua",
    ];

    [Fact]
    public void CompiledOutputMatchesGoldens()
    {
        var actual = GoldenPrograms.Where(p => File.Exists(Path.Combine(Root, p))).Select(p => {
            // Multi-line string literals would otherwise follow the checkout's line endings (CRLF on Windows, LF elsewhere).
            var bytes = new WasmCompiler(LibrarySet.Empty).Compile(Read(p).Replace("\r\n", "\n")).Bytes.ToArray();
            return $"{p}\t{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\t{bytes.Length}";
        }).ToList();
        Assert.True(actual.Count >= 8, "Rule 'Codegen stability': most golden programs are missing; update GoldenPrograms.");
        string path = Path.Combine(Root, Goldens), text = string.Join("\n", actual) + "\n";
        if (Environment.GetEnvironmentVariable("AQUARIUS_UPDATE_CODEGEN_GOLDENS") == "1") File.WriteAllText(path, text);
        string expected = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        Assert.True(expected == text, "Rule 'Codegen stability': compiled Wasm for the golden programs changed. If the emitter or guest " +
            "runtime change is intended, review it and update the goldens with AQUARIUS_UPDATE_CODEGEN_GOLDENS=1 dotnet test --filter " +
            $"CompiledOutputMatchesGoldens, then commit {Goldens}.\nExpected:\n{expected}\nActual:\n{text}");
    }

    [ToolchainFact]
    public void TheEmbeddedGuestRuntimeRebuildsFromSource()
    {
        var (code, output) = Run(PowerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(Root, "scripts/build-wasm-runtime.ps1")}\" -Check");
        Assert.True(code == 0, "Rule 'Reproducible Wasm': runtime/guest/runtime.wasm (embedded in the compiler) must rebuild byte-identically " +
            "from runtime.c with the pinned Clang (scripts/build-wasm-runtime.ps1). Rebuild and commit it.\n" + output);
    }

    // ---- Repository hygiene --------------------------------------------------------------------------------------

    [Fact]
    public void NoCommittedIdeOrUserFiles()
    {
        var offenders = RepositoryFiles().Where(f => f.Contains(".idea/", StringComparison.Ordinal) || f.StartsWith(".idea", StringComparison.Ordinal)
            || f.EndsWith(".DotSettings.user", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".user", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(offenders.Count == 0, "Rule 'Repository hygiene': IDE and per-user files are not committed (.gitignore covers them): " +
            string.Join(", ", offenders.Take(20)));
    }

    [Fact]
    public void AquariusSourcesLiveInExamplesLibrariesAndTests()
    {
        static bool Allowed(string f) => f.StartsWith("examples/", StringComparison.Ordinal) || f.StartsWith("compiler/AquariusTests/", StringComparison.Ordinal) ||
            f.StartsWith("libraries/", StringComparison.Ordinal) || f.StartsWith("tests/", StringComparison.Ordinal) || f.Contains("/tests/", StringComparison.Ordinal);
        var offenders = FilesWith(".aqua").Where(f => !Allowed(f)).Select(f => f[..(f.LastIndexOf('/') + 1)]).Distinct().ToList();
        Assert.True(offenders.Count == 0, "Rule 'One example tree': Aquarius sources are examples (examples/), library modules and tests " +
            "(libraries/<name>/), or test fixtures. Move these: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DocsUseRepositoryRelativeLinks()
    {
        var machine = new Regex(@"[A-Za-z]:[/\\](?:OfficialProjects|Users)[/\\]", RegexOptions.IgnoreCase);
        var offenders = FilesWith(".md").Where(f => !f.StartsWith("docs/archive/", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(Path.Combine(Root, f)).Select((l, i) => (f, l, i)).Where(p => machine.IsMatch(p.l)).Select(p => $"{p.f}:{p.i + 1}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Rule 'Repository hygiene': Markdown must not contain absolute machine paths; use repository-relative " +
            "links. Offenders: " + string.Join(", ", offenders.Take(30)));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    internal static (int Code, string Output) Run(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // A PSModulePath inherited from another PowerShell edition hides the child's own modules (Get-FileHash).
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
    internal static string PowerShell => OperatingSystem.IsWindows() ? "powershell" : "pwsh";
    internal static bool CanRun(string file)
    {
        try { return Run(file, "-NoProfile -Command exit 0").Code == 0; } catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>Runs only where the pinned LLVM/Clang toolchain is installed (scripts/install-toolchain.ps1).</summary>
public sealed class ToolchainFactAttribute : FactAttribute
{
    public ToolchainFactAttribute()
    {
        string toolchains = Path.Combine(ArchitectureRulesTest.Root, ".native-build", "toolchains");
        bool installed = Directory.Exists(toolchains) && Directory.EnumerateDirectories(toolchains, "llvm-*").Any();
        if (!installed && Environment.GetEnvironmentVariable("AQUARIUS_LLVM") == null)
            Skip = "Install the pinned Clang toolchain (scripts/install-toolchain.ps1) to check that the committed guest runtime rebuilds from source.";
        else if (!ArchitectureRulesTest.CanRun(ArchitectureRulesTest.PowerShell))
            Skip = "Install PowerShell to run scripts/build-wasm-runtime.ps1 -Check.";
    }
}
