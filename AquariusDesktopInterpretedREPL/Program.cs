using AquariusREPL.interpret;

string[] cmdArgs = Environment.GetCommandLineArgs();

if (cmdArgs.Length <= 1) {
    Console.WriteLine("Hello, this is the Aquarius programming language!");
    Console.WriteLine("We're now in REPL mode!");

    Interpreter.REPL();
} else {
    Interpreter.Interpret(cmdArgs[1]);
}
