using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.Compiler;
using AquariusLang.Build;
using AquariusLang.Desktop.runtime;

namespace AquariusLang.Cli;

public static class CompilerCommandLine {
    public const string Usage = "aqua build main.aqua [more.aqua ...] [--root directory] [--assets path ...] [-o app.wasm]\n" +
        "aqua build app.wasm --target web -o output-directory [--entry main.aqua]\n" +
        "aqua build app.wasm --target windows -o app.exe [--entry main.aqua]\n" +
        "aqua run app.wasm [--entry main.aqua]\naqua repl\n" +
        "Exit codes: 0 success, 1 compilation/runtime/IO failure, 2 invalid command or options.";
    public static int Run(string[] args) {
        try {
            if (args.Length == 0 || args is ["--help"] or ["-h"] || args is [_, "--help"]) { Console.WriteLine(Usage); return 0; }
            if (args[0] == "repl") {
                if (args.Length != 1) throw new ArgumentException("repl takes no arguments.");
                return CommandLine.Run(Array.Empty<string>());
            }
            if (args[0] is not ("build" or "run")) throw new ArgumentException($"Unknown command: {args[0]}");
            var files = new List<string>(); var assets = new List<string>();
            var options = new Dictionary<string, string>(); bool positional = false;
            for (int i = 1; i < args.Length; i++) {
                string arg = args[i];
                if (!positional && arg == "--") { positional = true; continue; }
                if (!positional && arg.StartsWith('-')) {
                    if (arg is not ("-o" or "--root" or "--assets" or "--entry" or "--target")) throw new ArgumentException($"Unknown option: {arg}");
                    if (++i == args.Length || args[i].StartsWith('-') || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException($"Missing value for {arg}.");
                    if (arg == "--assets") assets.Add(args[i]);
                    else if (!options.TryAdd(arg, args[i])) throw new ArgumentException($"Specify {arg} only once.");
                } else files.Add(arg);
            }
            string? Value(string name) => options.GetValueOrDefault(name);
            if (args[0] == "run") {
                if (files.Count != 1 || options.Keys.Any(k => k != "--entry") || assets.Count != 0) throw new ArgumentException("run requires one wasm and an optional --entry.");
                if (!files[0].EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("run requires a .wasm file.");
                var result = ScriptRunner.RunWasm(files[0], Value("--entry"));
                return result is ErrorObj ? 1 : 0;
            }
            string target = Value("--target") ?? "wasm";
            if (target != "wasm") {
                var backend = CompilerBuildTargets.Default.Resolve(target);
                if (Value("-o") == null) throw new ArgumentException($"{target} builds require -o output.");
                if (files.Count == 1 && files[0].EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) {
                    if (Value("--root") != null || assets.Count != 0) throw new ArgumentException("Compiled inputs already contain modules and assets.");
                    backend.Build(new WasmBuildRequest(files[0], Value("-o")!, Value("--entry")));
                } else {
                    if(files.Count==0 || files.Any(f=>!f.EndsWith(".aqua",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Provide .aqua sources or one .wasm application.");
                    string temporary=Path.Combine(Path.GetTempPath(),"aquarius-build-"+Guid.NewGuid().ToString("N")+".wasm");
                    try { WasmApplication.Compile(files,temporary,Value("--root"),assets,Value("--entry")); backend.Build(new WasmBuildRequest(temporary,Value("-o")!,Value("--entry"))); }
                    finally { if(File.Exists(temporary)) File.Delete(temporary); }
                }
                Console.WriteLine($"{backend.ArtifactDescription} built: {Path.GetFullPath(Value("-o")!)}"); return 0;
            }
            if (files.Count == 0 || files.Any(p => !p.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Wasm builds require .aqua source files.");
            string output = Value("-o") ?? Path.GetFileNameWithoutExtension(files[0]) + ".wasm";
            WasmApplication.Compile(files, output, Value("--root"), assets, Value("--entry"));
            Console.WriteLine($"Compiled {files.Count} script(s) to {Path.GetFullPath(output)}"); return 0;
        } catch (ArgumentException error) { Console.Error.WriteLine(error.Message + "\n" + Usage); return 2; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CompilationException or NotSupportedException or OverflowException) {
            Console.Error.WriteLine(error.Message); return 1;
        }
    }
}
