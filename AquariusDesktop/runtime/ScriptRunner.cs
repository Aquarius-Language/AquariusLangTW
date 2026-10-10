using AquariusLang.Compiler;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.utils;
using Environment = AquariusLang.Object.Environment;

namespace AquariusLang.Desktop.runtime; 

public class ScriptRunner {
    const string PROMPT = ">> ";

    /// <summary>
    /// Read, Evaluate, Print, Loop.
    /// </summary>
    public static void REPL() {
        var environment = Environment.NewEnvironment();
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

            CompiledEvaluator evaluator = CompiledEvaluator.NewInstance(desktopBuiltins);
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
    /// Compile and execute the given script file through WebAssembly.
    /// </summary>
    /// <param name="fileName">Path of file.</param>
    public static IObject RunFile(string fileName, IEnumerable<string>? launchFiles = null) {
        if (fileName.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) return RunWasm(fileName, launchFiles: launchFiles);
        using DesktopBuiltins desktopBuiltins = new(new Application.DesktopApplicationHost(launchFiles: launchFiles));
        desktopBuiltins.NewDefaultBuiltins(fileName);
        
        string contents = File.ReadAllText(fileName);
        Lexer lexer = Lexer.NewInstance(contents);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        if (parser.Errors.Count != 0 || lexer.Errors.Count != 0) {
            printParserErrors(lexer.Errors.Select(error => error.Message).Concat(parser.Errors).ToArray());
            return new ErrorObj(string.Join("\n", lexer.Errors.Select(error => error.Message).Concat(parser.Errors)));
        }
        CompiledEvaluator evaluator = CompiledEvaluator.NewInstance(desktopBuiltins);
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

    /// <summary>Load a WebAssembly application and execute its default or selected compiled module.</summary>
    public static IObject RunWasm(string fileName, string? entryPoint = null, IEnumerable<string>? launchFiles = null) {
        var package = WasmApplication.Load(fileName);
        return RunPackage(package, fileName, entryPoint, launchFiles);
    }

    /// <summary>Run a validated package using the artifact's location as its virtual module root.</summary>
    public static IObject RunPackage(WasmApplication package, string fileName, string? entryPoint = null, IEnumerable<string>? launchFiles = null) {
        using var runtime = new WasmApplicationRuntime(package, fileName, launchFiles);
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
