using AquariusLang.Object;
using AquariusLang.VM;

namespace AquariusREPL.runtime;

public static class CommandLine {
    private const string Usage = "AquariusDesktopVMREPL [script.aqua | package.bottle]\n" +
        "AquariusDesktopVMREPL -c|-cr [-o output.bottle] [--root directory] script.aqua [more.aqua ...]\n" +
        "AquariusDesktopVMREPL --entry path/script.rius package.bottle\n" +
        "AquariusDesktopVMREPL --disassemble script.aqua";

    public static int Run(string[] args) {
        try {
            if (args.Length == 1 && args[0] is "--help" or "-h") { Console.WriteLine(Usage); return 0; }
            if (args.Length == 0) {
                Console.WriteLine("Hello, this is the Aquarius VM programming language!");
                Console.WriteLine("We're now in REPL mode!");
                ScriptRunner.REPL();
                return 0;
            }
            if (args[0] is "-c" or "-cr") {
                var files = new List<string>();
                string? output = null, root = null;
                bool positional = false;
                for (int i = 1; i < args.Length; i++) {
                    if (!positional && args[i] == "--") { positional = true; continue; }
                    if (!positional && args[i] is "-o" or "--root") {
                        string option = args[i];
                        if (++i == args.Length || args[i].StartsWith('-')) throw new ArgumentException($"Missing value for {option}.");
                        if (option == "-o") {
                            if (output != null) throw new ArgumentException("Specify -o only once.");
                            output = args[i];
                        } else {
                            if (root != null) throw new ArgumentException("Specify --root only once.");
                            root = args[i];
                        }
                    } else {
                        if (!positional && args[i].StartsWith('-')) throw new ArgumentException($"Unknown option: {args[i]}");
                        files.Add(args[i]);
                    }
                }
                if (files.Count == 0) throw new ArgumentException("Provide at least one .aqua source file.");
                output ??= Path.GetFileNameWithoutExtension(files[0]) + ".bottle";
                BottlePackage.Compile(files, output, root);
                Console.WriteLine($"Compiled {files.Count} script(s) to {Path.GetFullPath(output)}");
                return args[0] == "-cr" ? ResultCode(ScriptRunner.RunBottle(output)) : 0;
            }
            if (args.Length == 2 && args[0] == "--disassemble") {
                Console.Write(new VmCompiler().Compile(File.ReadAllText(args[1])).Disassemble());
                return 0;
            }
            if (args.Length == 3 && args[0] == "--entry") return ResultCode(ScriptRunner.RunBottle(args[2], args[1]));
            if (args.Length == 1 && !args[0].StartsWith('-')) return ResultCode(ScriptRunner.RunFile(args[0]));
            Console.Error.WriteLine(Usage);
            return 1;
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or VmCompilationException or ArgumentException or NotSupportedException) {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    // A valid script may end with a declaration or 印出, both of which yield no value.
    private static int ResultCode(IObject? result) => result is ErrorObj ? 1 : 0;
}
