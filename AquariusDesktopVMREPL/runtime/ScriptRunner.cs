using AquariusLang.VM;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.utils;
using Environment = AquariusLang.Object.Environment;

namespace AquariusREPL.runtime; 

public class ScriptRunner {
    const string PROMPT = ">> ";
    static Environment environment = Environment.NewEnvironment();

    /// <summary>
    /// Read, Evaluate, Print, Loop.
    /// </summary>
    public static void REPL() {
        using DesktopBuiltins desktopBuiltins = newDefaultBuiltins("");
        
        while (true) {
            Console.Write(PROMPT);
            
            string? line = Console.ReadLine();
            if (line == null) return;
            Lexer lexer = Lexer.NewInstance(line);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();

            if (parser.Errors.Count != 0 || lexer.Errors.Count != 0) {
                printParserErrors(lexer.Errors.Select(error => error.Message).Concat(parser.Errors).ToArray());
                continue;
            }

            VmEvaluator evaluator = VmEvaluator.NewInstance(desktopBuiltins);
            IObject evaluated = evaluator.Eval(tree, environment);
            
            /*
             * Note: C#'s null shouldn't be printed out; but NullObj needs to be printed out.
             */
            if (evaluated != null) {
                Console.WriteLine(evaluated.Inspect());
            }
        }
    }

    /// <summary>
    /// Compile and execute the given script file on the VM.
    /// </summary>
    /// <param name="fileName">Path of file.</param>
    public static IObject RunFile(string fileName) {
        if (fileName.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase)) return RunBottle(fileName);
        using DesktopBuiltins desktopBuiltins = newDefaultBuiltins(fileName);
        
        string contents = File.ReadAllText(fileName);
        Lexer lexer = Lexer.NewInstance(contents);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        if (parser.Errors.Count != 0 || lexer.Errors.Count != 0) {
            printParserErrors(lexer.Errors.Select(error => error.Message).Concat(parser.Errors).ToArray());
            return new ErrorObj(string.Join("\n", lexer.Errors.Select(error => error.Message).Concat(parser.Errors)));
        }
        VmEvaluator evaluator = VmEvaluator.NewInstance(desktopBuiltins);
        IObject evaluated = evaluator.Eval(tree, Environment.NewEnvironment());
        if (evaluated != null) {
            Console.WriteLine(evaluated.Inspect());
        }

        return evaluated;
    }

    private static DesktopBuiltins newDefaultBuiltins(string filePath) {
        DesktopBuiltins builtins = new DesktopBuiltins();
        builtins.NewDefaultBuiltins(filePath);
        return builtins;
    }

    /// <summary>Load .rius instructions from a bottle and execute its default or selected entry point.</summary>
    public static IObject RunBottle(string fileName, string? entryPoint = null) {
        var package = BottlePackage.Load(fileName);
        using var runtime = new BottleRuntime(package, fileName);
        IObject result;
        try { result = runtime.Execute(package.ResolveScript(entryPoint ?? package.EntryPoint)); }
        catch (InvalidDataException error) { result = new ErrorObj(error.Message); }
        if (result != null) Console.WriteLine(result.Inspect());
        return result;
    }

    private static void printParserErrors(string[] errors) {
        Console.WriteLine("Parser errors:");
        foreach (var error in errors) {
            Console.WriteLine($"\t{error}");
        }
    }
}
