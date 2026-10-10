using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.VM;
using AquariusLang.Build;
using AquariusREPL.runtime;

namespace AquariusLang.Cli;

public static class CompilerCommandLine {
    public const string Usage = "aqua build main.aqua [more.aqua ...] [--root directory] [--assets path ...] [-o app.bottle]\n" +
        "aqua build app.bottle --target web -o output-directory [--entry main.rius]\n" +
        "aqua build app.bottle --target windows -o app.exe [--entry main.rius]\n" +
        "aqua run app.bottle [--entry main.rius]\naqua repl\n" +
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
                if (files.Count != 1 || options.Keys.Any(k => k != "--entry") || assets.Count != 0) throw new ArgumentException("run requires one bottle and an optional --entry.");
                if (!files[0].EndsWith(".bottle", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("run requires a .bottle file.");
                var result = ScriptRunner.RunBottle(files[0], Value("--entry"));
                return result is ErrorObj ? 1 : 0;
            }
            string target = Value("--target") ?? "bottle";
            if (target != "bottle") {
                var backend = CompilerBuildTargets.Default.Resolve(target);
                if (files.Count != 1 || !files[0].EndsWith(".bottle", StringComparison.OrdinalIgnoreCase) || Value("-o") == null ||
                    Value("--root") != null || assets.Count != 0) throw new ArgumentException($"{target} builds require one .bottle input and -o output; resources come from the bottle.");
                backend.Build(new BottleBuildRequest(files[0], Value("-o")!, Value("--entry")));
                Console.WriteLine($"{backend.ArtifactDescription} built: {Path.GetFullPath(Value("-o")!)}"); return 0;
            }
            if (files.Count == 0 || files.Any(p => !p.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Bottle builds require .aqua source files.");
            string output = Value("-o") ?? Path.GetFileNameWithoutExtension(files[0]) + ".bottle";
            BottlePackage.Compile(files, output, Value("--root"), assets, Value("--entry"));
            Console.WriteLine($"Compiled {files.Count} script(s) to {Path.GetFullPath(output)}"); return 0;
        } catch (ArgumentException error) { Console.Error.WriteLine(error.Message + "\n" + Usage); return 2; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or VmCompilationException or NotSupportedException or OverflowException) {
            Console.Error.WriteLine(error.Message); return 1;
        }
    }
}
