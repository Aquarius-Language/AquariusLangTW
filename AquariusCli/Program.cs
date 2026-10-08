namespace AquariusLang.Cli;

public static class Program {
    public static int Main(string[] args) {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        return CompilerCommandLine.Run(args);
    }
}
