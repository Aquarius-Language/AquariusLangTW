using AquariusREPL.interpret;

string[] cmdArgs = Environment.GetCommandLineArgs();

if (cmdArgs.Length <= 1) {
    Console.WriteLine("Hello, this is the Aquarius programming language!");
    Console.WriteLine("We're now in REPL mode!");

    Interpreter.REPL();
} else {
    var result = Interpreter.Interpret(cmdArgs[1]);
    if (result == null || result is AquariusLang.Object.ErrorObj) Environment.ExitCode = 1;
}
