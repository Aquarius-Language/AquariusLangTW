namespace AquariusLang.Cli;

public static class Program {
    public static int Main(string[] args) {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        // REPL input, typed or piped, is UTF-8 like source files.
        if (args is ["repl"]) Console.InputEncoding = new System.Text.UTF8Encoding(false);
        return CompilerCommandLine.Run(args);
    }
}
