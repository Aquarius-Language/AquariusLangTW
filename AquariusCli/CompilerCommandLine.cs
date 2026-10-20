using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.Compiler;
using AquariusLang.Build;
using AquariusLang.Desktop.runtime;
using AquariusLang.Bottle;

namespace AquariusLang.Cli;

public static class CompilerCommandLine {
    public const string Usage = "aqua build main.aqua [more.aqua ...] [--root directory] [--assets path ...] [-o app.wasm]\n" +
        "aqua build app.wasm --target web -o output-directory [--entry main.aqua]\n" +
        "aqua build app.wasm --target windows -o app.exe [--entry main.aqua] [--onnx-cuda true|false]\n" +
        "aqua build app.wasm --target linux -o app [--entry main.aqua] [--onnx-cuda true|false]\n" +
        "--onnx-cuda true bundles ONNX CUDA providers and NVIDIA inference runtime libraries (opt-in; projects use [onnx] cuda = true).\n" +
        "aqua build main.aqua --target android -o android-project [--root directory] [--assets path]\n" +
        "aqua build app.wasm --target android -o app.apk [--entry main.aqua]\n" +
        "Android options: [--application-id org.example.app] [--app-name DisplayName]\n" +
        "aqua build [aqua.toml|project-directory] [--target PLATFORM] [--platform PLATFORM] [-o output] [--locked] [--offline]\n" +
        "aqua run app.wasm [--entry main.aqua]\naqua repl\n" + BottleCommandLine.Usage + "\n" +
        "Exit codes: 0 success, 1 compilation/runtime/IO failure, 2 invalid command or options.";
    public static int Run(string[] args) {
        try {
            if (args.Length == 0 || args is ["--help"] or ["-h"] || args is [_, "--help"]) { Console.WriteLine(Usage); return 0; }
            if (args[0] == "bottle") return BottleCommandLine.Run(args[1..]);
            if (args[0] == "new") return BottleCommandLine.Run(args);
            if (args[0] == "repl") {
                if (args.Length != 1) throw new ArgumentException("repl takes no arguments.");
                return CommandLine.Run(Array.Empty<string>());
            }
            if (args[0] is not ("build" or "run")) throw new ArgumentException($"Unknown command: {args[0]}");
            var files = new List<string>(); var assets = new List<string>();
            var options = new Dictionary<string, string>(); var flags = new HashSet<string>(); bool positional = false;
            for (int i = 1; i < args.Length; i++) {
                string arg = args[i];
                if (!positional && arg == "--") { positional = true; continue; }
                if (!positional && arg.StartsWith('-')) {
                    if (arg is "--locked" or "--offline") { if (!flags.Add(arg)) throw new ArgumentException($"Specify {arg} only once."); continue; }
                    if (arg is not ("-o" or "--root" or "--assets" or "--entry" or "--target" or "--platform") && !CompilerBuildTargets.Default.OptionNames.Contains(arg)) throw new ArgumentException($"Unknown option: {arg}");
                    if (++i == args.Length || args[i].StartsWith('-') || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException($"Missing value for {arg}.");
                    if (arg == "--assets") assets.Add(args[i]);
                    else if (!options.TryAdd(arg, args[i])) throw new ArgumentException($"Specify {arg} only once.");
                } else files.Add(arg);
            }
            string? Value(string name) => options.GetValueOrDefault(name);
            if (args[0] == "run") {
                if (files.Count != 1 || options.Keys.Any(k => k != "--entry") || assets.Count != 0 || flags.Count != 0) throw new ArgumentException("run requires one wasm and an optional --entry.");
                if (!files[0].EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("run requires a .wasm file.");
                var result = ScriptRunner.RunWasm(files[0], Value("--entry"));
                return result is ErrorObj ? 1 : 0;
            }
            string target = Value("--target") ?? "wasm";
            string? manifest = null;
            if (files.Count == 0) manifest = ProjectManifest.Find(System.Environment.CurrentDirectory);
            else if (files.Count == 1 && (Directory.Exists(files[0]) || files[0].EndsWith(".toml", StringComparison.OrdinalIgnoreCase))) manifest = ProjectManifest.Resolve(files[0]);
            else if (files.All(f => f.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))) {
                var manifests = files.Select(ProjectManifest.Find).Distinct().ToArray();
                if (manifests.Any(m => m != null)) {
                    if (manifests.Length != 1 || manifests[0] == null) throw new ArgumentException("Do not mix sources from separate TOML projects.");
                    manifest = manifests[0]!;
                    var declared = ProjectManifest.Load(manifest).SourceFiles();
                    if (files.Any(f => !declared.Contains(Path.GetFullPath(f), StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Source is not declared in project.sources.");
                }
            }
            if (manifest != null) {
                if (Value("--root") != null || Value("--entry") != null || assets.Count > 0) throw new ArgumentException("TOML projects define their root, entry, sources and assets in the manifest.");
                var project = ProjectManifest.Load(manifest);
                if (target == "wasm") {
                    if (options.Keys.Any(k => k is not ("-o" or "--target" or "--platform"))) throw new ArgumentException("Platform options require their platform target.");
                    string assetPlatform = Value("--platform") ?? target;
                    if (assetPlatform != "wasm" && !ProjectManifest.KnownPlatforms.Contains(assetPlatform)) throw new ArgumentException("Unknown --platform: " + assetPlatform);
                    string destination = Value("-o") ?? Path.Combine(project.Root, "dist", project.Name + ".wasm");
                    ProjectBuilder.Compile(manifest, destination, assetPlatform, flags.Contains("--locked"), flags.Contains("--offline"));
                    Console.WriteLine($"Built {project.Type} {project.Name}: {Path.GetFullPath(destination)}"); return 0;
                }
                var projectBackend = CompilerBuildTargets.Default.Resolve(target);
                if (options.Keys.Any(k => k is not ("-o" or "--target") && !projectBackend.OptionNames.Contains(k))) throw new ArgumentException($"Option does not apply to {target}.");
                string projectOutput = Value("-o") ?? throw new ArgumentException($"{target} builds require -o output.");
                // Manifest build settings become target options; an explicit command-line value overrides them.
                if (project.Onnx.Cuda && projectBackend.OptionNames.Contains(SelfContainedExecutableTarget.OnnxCudaOption))
                    options.TryAdd(SelfContainedExecutableTarget.OnnxCudaOption, "true");
                string intermediate = Path.Combine(Path.GetTempPath(), "aquarius-project-build-" + Guid.NewGuid().ToString("N") + ".wasm");
                try {
                    ProjectBuilder.Compile(manifest, intermediate, target, flags.Contains("--locked"), flags.Contains("--offline"));
                    projectBackend.Build(new WasmBuildRequest(intermediate, projectOutput, null, options.Where(o => projectBackend.OptionNames.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value)));
                } finally { if (File.Exists(intermediate)) File.Delete(intermediate); }
                Console.WriteLine($"{projectBackend.ArtifactDescription} built: {Path.GetFullPath(projectOutput)}"); return 0;
            }
            if (flags.Count != 0) throw new ArgumentException("--locked and --offline require a TOML project.");
            if (target != "wasm") {
                var backend = CompilerBuildTargets.Default.Resolve(target);
                var platformOptions = options.Where(o => backend.OptionNames.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value);
                if (options.Keys.Any(k => k is not ("-o" or "--root" or "--entry" or "--target") && !backend.OptionNames.Contains(k))) throw new ArgumentException($"Option does not apply to {target}.");
                if (Value("-o") == null) throw new ArgumentException($"{target} builds require -o output.");
                if (files.Count == 1 && files[0].EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) {
                    if (Value("--root") != null || assets.Count != 0) throw new ArgumentException("Compiled inputs already contain modules and assets.");
                    backend.Build(new WasmBuildRequest(files[0], Value("-o")!, Value("--entry"), platformOptions));
                } else {
                    if(files.Count==0 || files.Any(f=>!f.EndsWith(".aqua",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Provide .aqua sources or one .wasm application.");
                    string temporary=Path.Combine(Path.GetTempPath(),"aquarius-build-"+Guid.NewGuid().ToString("N")+".wasm");
                    try { WasmApplication.Compile(files,temporary,Value("--root"),assets,Value("--entry")); backend.Build(new WasmBuildRequest(temporary,Value("-o")!,Value("--entry"),platformOptions)); }
                    finally { if(File.Exists(temporary)) File.Delete(temporary); }
                }
                Console.WriteLine($"{backend.ArtifactDescription} built: {Path.GetFullPath(Value("-o")!)}"); return 0;
            }
            if (files.Count == 0 || files.Any(p => !p.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Wasm builds require .aqua source files.");
            if (options.Keys.Any(k => k is not ("-o" or "--root" or "--entry" or "--target"))) throw new ArgumentException("Platform options require their platform target.");
            string output = Value("-o") ?? Path.GetFileNameWithoutExtension(files[0]) + ".wasm";
            WasmApplication.Compile(files, output, Value("--root"), assets, Value("--entry"));
            Console.WriteLine($"Compiled {files.Count} script(s) to {Path.GetFullPath(output)}"); return 0;
        } catch (ArgumentException error) { Console.Error.WriteLine(error.Message + "\n" + Usage); return 2; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CompilationException or NotSupportedException or OverflowException) {
            Console.Error.WriteLine(error.Message); return 1;
        }
    }
}
