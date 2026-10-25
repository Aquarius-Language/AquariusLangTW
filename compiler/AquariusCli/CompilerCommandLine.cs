using AquariusLang.Bottle;
using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Packaging;

namespace AquariusLang.Cli;

public static class CompilerCommandLine {
    public const string Usage = "aqua build main.aqua [more.aqua ...] [--root directory] [--assets path ...] [--entry main.aqua] [-o app.wasm]\n" +
        "aqua build (main.aqua ... | app.wasm) --target web -o site-directory [--entry main.aqua]\n" +
        "aqua build (main.aqua ... | app.wasm) --target windows -o app.exe [--entry main.aqua] [--hide-terminal true|false] [--onnx-cuda true|false]\n" +
        "aqua build (main.aqua ... | app.wasm) --target linux -o app [--entry main.aqua] [--onnx-cuda true|false]\n" +
        "aqua build (main.aqua ... | app.wasm) --target android -o app.apk [--entry main.aqua] [--application-id org.example.app] [--app-name Name] [--keystore file --key-alias name]\n" +
        "aqua build [aqua.toml|project-directory] [--target PLATFORM] [--platform PLATFORM] [-o output] [--locked] [--offline]\n" +
        "aqua run (app.wasm | main.aqua | project-directory) [--entry main.aqua] [argument ...]\n" +
        "aqua repl\n" + BottleCommandLine.Usage + "\n" + LibraryBuilder.Usage + "\n" +
        "--hide-terminal true builds a Windows GUI executable without a console ([desktop] hide_terminal = true in aqua.toml).\n" +
        "--onnx-cuda true uses the runtime pack built with ONNX Runtime's CUDA providers ([onnx] cuda = true in aqua.toml).\n" +
        "Android signing: --keystore/--key-alias with AQUARIUS_KEYSTORE_PASSWORD (and AQUARIUS_KEY_PASSWORD); a debug key otherwise.\n" +
        "Exit codes: 0 success, 1 compilation/runtime/IO failure, 2 invalid command or options; aqua run returns the application's.";

    private static readonly string[] SharedOptions = ["-o", "--root", "--assets", "--entry", "--target", "--platform"];

    public static int Run(string[] args) {
        try {
            if (args.Length == 0 || args is ["--help"] or ["-h"] || args is [_, "--help"]) { Console.WriteLine(Usage); return 0; }
            switch (args[0]) {
                case "bottle": return BottleCommandLine.Run(args[1..]);
                case "new": return BottleCommandLine.Run(args);
                case "library": return Library(args[1..]);
                case "repl":
                    if (args.Length != 1) throw new ArgumentException("repl takes no arguments.");
                    return Repl.Run(Console.In, Console.Out);
                case "run": return RunCommand(args[1..]);
                case "build": return Build(args[1..]);
                default: throw new ArgumentException($"Unknown command: {args[0]}");
            }
        } catch (ArgumentException error) { Console.Error.WriteLine(error.Message + "\n" + Usage); return 2; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CompilationException or NotSupportedException or OverflowException) {
            Console.Error.WriteLine(error.Message); return 1;
        }
    }

    private static int Build(string[] args) {
        var files = new List<string>(); var assets = new List<string>();
        var options = new Dictionary<string, string>(); var flags = new HashSet<string>(); bool positional = false;
        var targets = BuildTargets.Default;
        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];
            if (!positional && arg == "--") { positional = true; continue; }
            if (!positional && arg.StartsWith('-')) {
                if (arg is "--locked" or "--offline") { if (!flags.Add(arg)) throw new ArgumentException($"Specify {arg} only once."); continue; }
                if (!SharedOptions.Contains(arg) && !targets.OptionNames.Contains(arg)) throw new ArgumentException($"Unknown option: {arg}");
                if (++i == args.Length || args[i].StartsWith('-') || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException($"Missing value for {arg}.");
                if (arg == "--assets") assets.Add(args[i]);
                else if (!options.TryAdd(arg, args[i])) throw new ArgumentException($"Specify {arg} only once.");
            } else files.Add(arg);
        }
        string? Value(string name) => options.GetValueOrDefault(name);
        string target = Value("--target") ?? "wasm";
        var input = Classify(files);

        if (input.Project != null) {
            if (Value("--root") != null || Value("--entry") != null || assets.Count > 0) throw new ArgumentException("TOML projects define their root, entry, sources and assets in the manifest.");
            var project = ProjectManifest.Load(input.Project);
            if (target == "wasm") {
                if (options.Keys.Any(k => k is not ("-o" or "--target" or "--platform"))) throw new ArgumentException("Platform options require their platform target.");
                string assetPlatform = Value("--platform") ?? target;
                if (assetPlatform != "wasm" && !ProjectManifest.KnownPlatforms.Contains(assetPlatform)) throw new ArgumentException("Unknown --platform: " + assetPlatform);
                string destination = Value("-o") ?? Path.Combine(project.Root, "dist", project.Name + ".wasm");
                ProjectBuilder.Compile(input.Project, destination, assetPlatform, flags.Contains("--locked"), flags.Contains("--offline"));
                Console.WriteLine($"Built {project.Type} {project.Name}: {Path.GetFullPath(destination)}"); return 0;
            }
            if (Value("--platform") != null) throw new ArgumentException("--platform applies to .wasm output; export targets select their own platform.");
            var backend = targets.Resolve(target);
            if (options.Keys.Any(k => k is not ("-o" or "--target") && !backend.OptionNames.Contains(k))) throw new ArgumentException($"Option does not apply to {target}.");
            string output = Value("-o") ?? throw new ArgumentException($"{target} builds require -o output.");
            // Manifest build settings become target options; an explicit command-line value overrides them.
            if (project.Onnx.Cuda && backend.OptionNames.Contains(ExecutableTarget.OnnxCudaOption)) options.TryAdd(ExecutableTarget.OnnxCudaOption, "true");
            if (project.Desktop.HideTerminal && backend.OptionNames.Contains(ExecutableTarget.HideTerminalOption)) options.TryAdd(ExecutableTarget.HideTerminalOption, "true");
            string intermediate = Path.Combine(Path.GetTempPath(), "aquarius-project-build-" + Guid.NewGuid().ToString("N") + ".wasm");
            try {
                var build = ProjectBuilder.Compile(input.Project, intermediate, target, flags.Contains("--locked"), flags.Contains("--offline"));
                backend.Build(new WasmBuildRequest(intermediate, output, null, TargetOptions(options, backend), build.Libraries));
            } finally { if (File.Exists(intermediate)) File.Delete(intermediate); }
            Console.WriteLine($"{backend.ArtifactDescription} built: {Path.GetFullPath(output)}"); return 0;
        }
        if (flags.Count != 0) throw new ArgumentException("--locked and --offline require a TOML project.");
        if (Value("--platform") != null) throw new ArgumentException("--platform requires a TOML project.");
        if (target != "wasm") {
            var backend = targets.Resolve(target);
            if (options.Keys.Any(k => k is not ("-o" or "--root" or "--entry" or "--target") && !backend.OptionNames.Contains(k))) throw new ArgumentException($"Option does not apply to {target}.");
            string output = Value("-o") ?? throw new ArgumentException($"{target} builds require -o output.");
            if (input.Wasm != null) {
                if (Value("--root") != null || assets.Count != 0) throw new ArgumentException("Compiled inputs already contain modules and assets.");
                backend.Build(new WasmBuildRequest(input.Wasm, output, Value("--entry"), TargetOptions(options, backend), WasmLibraries(input.Wasm)));
            } else {
                string temporary = Path.Combine(Path.GetTempPath(), "aquarius-build-" + Guid.NewGuid().ToString("N") + ".wasm");
                try {
                    WasmApplication.Compile(input.Sources, temporary, Value("--root"), assets, Value("--entry"), libraries: input.Libraries);
                    backend.Build(new WasmBuildRequest(temporary, output, Value("--entry"), TargetOptions(options, backend), input.Libraries));
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            Console.WriteLine($"{backend.ArtifactDescription} built: {Path.GetFullPath(output)}"); return 0;
        }
        if (input.Wasm != null) throw new ArgumentException("Wasm builds require .aqua source files; export a .wasm with --target.");
        if (options.Keys.Any(k => k is not ("-o" or "--root" or "--entry" or "--target"))) throw new ArgumentException("Platform options require their platform target.");
        string wasm = Value("-o") ?? Path.GetFileNameWithoutExtension(input.Sources[0]) + ".wasm";
        WasmApplication.Compile(input.Sources, wasm, Value("--root"), assets, Value("--entry"), libraries: input.Libraries);
        Console.WriteLine($"Compiled {input.Sources.Count} script(s) to {Path.GetFullPath(wasm)}"); return 0;
    }

    private static Dictionary<string, string> TargetOptions(Dictionary<string, string> options, IWasmBuildTarget backend) =>
        options.Where(o => backend.OptionNames.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value);

    /// <summary>What a build or run names: a TOML project, one compiled .wasm, or .aqua sources (with the libraries they
    /// may import: the default ones, plus the library itself for sources inside a library folder).</summary>
    internal sealed record Input(string? Project, string? Wasm, IReadOnlyList<string> Sources, LibrarySet? Libraries);

    internal static Input Classify(IReadOnlyList<string> files) {
        if (files.Count == 0) {
            string? found = ProjectManifest.Find(Environment.CurrentDirectory);
            if (found == null || ProjectManifest.IsLibraryManifest(found)) throw new ArgumentException("Provide .aqua sources, a .wasm application or a TOML project.");
            return new(found, null, [], null);
        }
        if (files.Count == 1 && (Directory.Exists(files[0]) || files[0].EndsWith(".toml", StringComparison.OrdinalIgnoreCase)))
            return new(ProjectManifest.Resolve(files[0]), null, [], null);
        if (files.Count == 1 && files[0].EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) return new(null, files[0], [], null);
        if (files.Any(f => !f.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Provide .aqua sources, one .wasm application or a TOML project.");
        var manifests = files.Select(ProjectManifest.Find).Distinct().ToArray();
        if (manifests.Length != 1 && manifests.Any(m => m != null)) throw new ArgumentException("Do not mix sources from separate TOML projects.");
        string? manifest = manifests[0];
        if (manifest == null) return new(null, null, files, null);
        if (ProjectManifest.IsLibraryManifest(manifest)) {
            // Programs inside a library folder (its tests) import the library like any application would.
            var library = ProjectManifest.Load(manifest);
            bool isDefault = LibrarySet.Default.Libraries.Any(l => Path.GetFullPath(l.Root).Equals(library.Root, StringComparison.OrdinalIgnoreCase));
            return new(null, null, files, isDefault ? LibrarySet.Default : ProjectBuilder.Inspect(library));
        }
        var declared = ProjectManifest.Load(manifest).SourceFiles();
        if (files.Any(f => !declared.Contains(Path.GetFullPath(f), StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Source is not declared in project.sources.");
        return new(manifest, null, [], null);
    }

    /// <summary>The libraries an already compiled application was built with, when it sits inside a project or library.</summary>
    private static LibrarySet? WasmLibraries(string wasm) {
        string? manifest = ProjectManifest.Find(wasm);
        if (manifest == null) return null;
        try { return ProjectBuilder.Inspect(ProjectManifest.Load(manifest)); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    // aqua run (app.wasm | main.aqua | project) [--entry script.aqua] [argument ...]
    private static int RunCommand(string[] args) {
        int first = 0;
        string? entry = null;
        if (args.Length >= 2 && args[0] == "--entry") { entry = args[1]; first = 2; }
        if (first < args.Length && args[first] == "--") first++;
        if (first >= args.Length) throw new ArgumentException("run requires an application .wasm, .aqua source or project.");
        string file = args[first];
        var rest = args.Skip(first + 1).ToList();
        if (entry == null && rest.Count >= 2 && rest[0] == "--entry") { entry = rest[1]; rest.RemoveRange(0, 2); }
        if (rest.Count > 0 && rest[0] == "--") rest.RemoveAt(0);
        var input = Classify([file]);
        // Sources compile to a hidden file in their own folder: the runtime's application folder (目前工作目錄, files the
        // program writes, libraries/) is then the source folder, as for a .wasm built there.
        string? temporary = null;
        try {
            string wasm; LibrarySet? libraries;
            if (input.Wasm != null) { wasm = input.Wasm; libraries = WasmLibraries(wasm); }
            else if (input.Project != null) {
                if (entry != null) throw new ArgumentException("TOML projects define their entry in the manifest.");
                wasm = temporary = Path.Combine(Path.GetDirectoryName(input.Project)!, $".aquarius-run-{Guid.NewGuid():N}.wasm");
                libraries = ProjectBuilder.Compile(input.Project, wasm, AquariusInstallation.HostTarget.Split('-')[0]).Libraries;
            } else {
                string root = Path.GetDirectoryName(Path.GetFullPath(file))!;
                wasm = temporary = Path.Combine(root, $".aquarius-run-{Guid.NewGuid():N}.wasm");
                WasmApplication.Compile(ReferencedScripts(Path.GetFullPath(file), root), wasm, root, libraries: input.Libraries);
                libraries = input.Libraries;
            }
            var application = WasmApplication.Load(wasm);
            if (entry != null) application.ResolveScript(entry);
            return NativeRuntime.Run(wasm, entry, rest, NativeRuntime.LibraryFiles(application, libraries ?? LibrarySet.Default));
        } finally {
            if (temporary != null && File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    /// <summary>A source and every script it names, transitively: each .aqua string literal resolved against the folder
    /// of the script holding it (so <c>匯入("lib/a.aqua")</c> and <c>匯入(目前工作目錄 + "/a.aqua")</c> both count), kept
    /// when the file exists inside the application folder. <c>aqua run main.aqua</c> runs programs split into modules
    /// without listing them; <c>aqua build</c> compiles exactly the sources it is given.</summary>
    internal static IReadOnlyList<string> ReferencedScripts(string entry, string root)
    {
        var found = new List<string> { entry };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };
        for (int i = 0; i < found.Count && found.Count < WasmApplication.MaxEntries; i++) {
            LoweredProgram program;
            try { program = new LoweringCompiler().Compile(File.ReadAllText(found[i])); }
            catch (CompilationException) { continue; } // compiling the application reports it
            string directory = Path.GetDirectoryName(found[i])!;
            foreach (string reference in ApplicationLinker.ScriptReferences(program)) {
                string relative = reference.Replace('\\', '/').TrimStart('/');
                if (relative.Length == 0 || relative.Contains(':')) continue;
                string path = Path.GetFullPath(Path.Combine(directory, relative));
                if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                if (seen.Add(path)) found.Add(path);
            }
        }
        return found;
    }

    // aqua library build [folder] [--targets a,b]
    private static int Library(string[] args) {
        if (args.Length == 0 || args[0] != "build") throw new ArgumentException("Use: " + LibraryBuilder.Usage);
        string folder = "."; IReadOnlyList<string>? list = null;
        for (int i = 1; i < args.Length; i++) {
            if (args[i] == "--targets") {
                if (++i == args.Length || list != null) throw new ArgumentException("--targets needs one comma-separated list.");
                list = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            } else if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown option: {args[i]}");
            else folder = args[i];
        }
        foreach (string built in LibraryBuilder.Build(folder, list, Console.WriteLine)) Console.WriteLine($"Built {built}");
        return 0;
    }
}
