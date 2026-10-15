namespace AquariusLang.Bottle;

public static class BottleCommandLine {
    public const string Usage = "aqua new NAME [--type executable|library] [-o directory]\n" +
        "aqua bottle init [--name NAME] [--type executable|library] [--project directory]\n" +
        "aqua bottle install SOURCE [--version RANGE] [--rev TAG_OR_COMMIT] [--subdirectory PATH] [--project PATH|--global]\n" +
        "aqua bottle uninstall NAME [--project PATH]\n" +
        "aqua bottle restore|update [--project PATH] [--target PLATFORM] [--offline] [--locked]\n" +
        "aqua bottle list [--project PATH|--global]\n" +
        "aqua bottle cache path|list|remove NAME VERSION\n" +
        "SOURCE: local folder, local .toml, or https://github.com/owner/repository.git.\n" +
        "AQUA_BOTTLE_HOME overrides the per-user ~/.aquarius/bottles cache.";

    public static int Run(string[] args) {
        if (args.Length == 0 || args is ["--help"] or ["-h"]) { Console.WriteLine(Usage); return 0; }
        string command = args[0]; var positional = new List<string>(); var options = new Dictionary<string, string>();
        var flags = new HashSet<string>(); bool literal = false;
        for (int i = 1; i < args.Length; i++) {
            string argument = args[i];
            if (!literal && argument == "--") { literal = true; continue; }
            if (!literal && argument.StartsWith('-')) {
                if (argument is "--global" or "--offline" or "--locked") { if (!flags.Add(argument)) throw new ArgumentException($"Duplicate option {argument}."); continue; }
                if (argument is not ("--project" or "--version" or "--rev" or "--subdirectory" or "--target" or "--type" or "--name" or "-o")) throw new ArgumentException($"Unknown Bottle option: {argument}");
                if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-') || !options.TryAdd(argument, args[i])) throw new ArgumentException($"Missing or duplicate value for {argument}.");
            } else positional.Add(argument);
        }
        void Allow(int count, string[] allowed, params string[] allowedFlags) {
            if (positional.Count != count || options.Keys.Except(allowed).Any() || flags.Except(allowedFlags).Any()) throw new ArgumentException("Invalid Bottle arguments.\n" + Usage);
        }
        string Project() => options.GetValueOrDefault("--project") ?? ProjectManifest.Find(System.Environment.CurrentDirectory) ?? throw new InvalidDataException("No aqua.toml project found. Run 'aqua bottle init' or pass --project PATH.");
        var manager = new BottleManager();
        switch (command) {
            case "new":
                Allow(1, ["--type", "-o"]);
                Initialize(options.GetValueOrDefault("-o") ?? positional[0], positional[0], options.GetValueOrDefault("--type") ?? "executable"); break;
            case "init": {
                Allow(0, ["--project", "--name", "--type"]); string directory = Path.GetFullPath(options.GetValueOrDefault("--project") ?? ".");
                Initialize(directory, options.GetValueOrDefault("--name") ?? Path.GetFileName(directory), options.GetValueOrDefault("--type") ?? "executable"); break;
            }
            case "install":
                Allow(1, ["--project", "--version", "--rev", "--subdirectory"], "--global");
                if (flags.Contains("--global") && options.ContainsKey("--project")) throw new ArgumentException("Choose --global or --project.");
                manager.Install(positional[0], flags.Contains("--global") ? "." : Project(), options.GetValueOrDefault("--version"), options.GetValueOrDefault("--rev"), options.GetValueOrDefault("--subdirectory"), flags.Contains("--global"));
                Console.WriteLine("Library installed and cached."); break;
            case "uninstall":
                Allow(1, ["--project"]); manager.Uninstall(positional[0], Project()); Console.WriteLine($"Removed direct dependency {positional[0]}. Shared cache retained."); break;
            case "restore": case "update": {
                Allow(0, ["--project", "--target"], "--offline", "--locked");
                if (command == "update" && flags.Contains("--locked")) throw new ArgumentException("update cannot use --locked.");
                string target = options.GetValueOrDefault("--target") ?? "wasm"; ValidateTarget(target);
                var resolved = manager.Restore(ProjectManifest.Load(Project()), target, command == "update", flags.Contains("--locked"), flags.Contains("--offline"));
                foreach (var package in resolved.Packages) Console.WriteLine($"{package.Name} {package.Version}");
                Console.WriteLine($"Restored {resolved.Packages.Count} libraries."); break;
            }
            case "list":
                Allow(0, ["--project"], "--global");
                if (flags.Contains("--global")) foreach (var package in manager.Cache.List()) Console.WriteLine($"{package.Name} {package.Version} {package.Hash}");
                else foreach (var dependency in ProjectManifest.Load(Project()).Dependencies.Values) Console.WriteLine($"{dependency.Name} {dependency.Version}");
                break;
            case "cache":
                if (options.Count != 0 || flags.Count != 0) throw new ArgumentException("Invalid cache options.");
                if (positional is ["path"]) Console.WriteLine(manager.Cache.Root);
                else if (positional is ["list"]) foreach (var package in manager.Cache.List()) Console.WriteLine($"{package.Name} {package.Version} {package.Hash}");
                else if (positional is ["remove", var name, var version]) { using var held = manager.Cache.Acquire(); manager.Cache.Remove(name, version); Console.WriteLine($"Removed cached {name} {version}. Projects using it must restore from source."); }
                else throw new ArgumentException("Use 'aqua bottle cache path|list|remove NAME VERSION'.");
                break;
            default: throw new ArgumentException($"Unknown Bottle command: {command}\n{Usage}");
        }
        return 0;
    }
    internal static void ValidateTarget(string target) { if (target != "wasm" && !ProjectManifest.KnownPlatforms.Contains(target)) throw new ArgumentException($"Unknown platform: {target}"); }
    private static void Initialize(string directory, string name, string type) {
        if (!ProjectManifest.ValidName(name)) throw new InvalidDataException("Project names cannot contain whitespace and must use letters, digits, underscores or hyphens.");
        if (type is not ("library" or "executable")) throw new ArgumentException("--type must be executable or library.");
        directory = Path.GetFullPath(directory); string manifest = Path.Combine(directory, ProjectManifest.FileName);
        if (File.Exists(manifest)) throw new IOException($"Manifest already exists: {manifest}");
        Directory.CreateDirectory(directory);
        string text = $"manifest_version = 1\n\n[project]\nname = \"{name}\"\ntype = \"{type}\"\nversion = \"0.1.0\"\nentry = \"main.aqua\"\nsources = [\"main.aqua\"]\nplatforms = [\"windows\", \"linux\", \"web\", \"android\"]\n\n[dependencies]\n";
        ProjectManifest.Parse(manifest, text);
        string entry = Path.Combine(directory, "main.aqua");
        if (!File.Exists(entry)) File.WriteAllText(entry, type == "library" ? "變數 加法, add = 函式(a, b) { 回傳 a + b; };\n" : "印出(\"Hello, Aquarius!\");\n");
        BottleCache.AtomicText(manifest, text); Console.WriteLine($"Created {type} project: {manifest}");
    }
}
