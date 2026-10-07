using AquariusLang.Object;
using AquariusLang.VM;
using AquariusREPL.runtime;
using Environment = System.Environment;

if (args.Length == 1 && args[0] is "--help" or "-h") {
    Console.WriteLine("AquariusDesktopVMREPL [script.aqua]");
    Console.WriteLine("AquariusDesktopVMREPL --disassemble script.aqua");
} else if (args.Length == 0) {
    Console.WriteLine("Hello, this is the Aquarius VM programming language!");
    Console.WriteLine("We're now in REPL mode!");
    ScriptRunner.REPL();
} else {
    try {
        if (args.Length == 2 && args[0] == "--disassemble") {
            Console.Write(new VmCompiler().Compile(File.ReadAllText(args[1])).Disassemble());
        } else if (args.Length == 1) {
            var result = ScriptRunner.RunFile(args[0]);
            if (result == null || result is ErrorObj) Environment.ExitCode = 1;
        } else {
            Console.Error.WriteLine("Usage: AquariusDesktopVMREPL [script.aqua] | --disassemble script.aqua");
            Environment.ExitCode = 1;
        }
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or VmCompilationException) {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
}
