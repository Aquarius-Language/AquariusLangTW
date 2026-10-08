using AquariusLang.VM;
using System.Diagnostics;
using System.Text;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.utils;
using Environment = AquariusLang.Object.Environment;
using AquariusREPL.Graphics;

namespace AquariusREPL;

public class DesktopBuiltins : Builtins, IDisposable {
    private readonly GraphicsRuntime graphics;
    private readonly bool ownsGraphics;
    internal Func<string, IObject>? ScriptImport { get; set; }
    public DesktopBuiltins() : this(new GraphicsRuntime(), true) { }
    internal DesktopBuiltins(GraphicsRuntime graphics) : this(graphics, false) { }
    private DesktopBuiltins(GraphicsRuntime graphics, bool ownsGraphics) {
        this.graphics = graphics;
        this.ownsGraphics = ownsGraphics;
        graphics.InvokeAqua = (callback, args) => {
            if (callback is BuiltinObj builtin) return builtin.Fn(args);
            if (callback is not FunctionObj function) return new ErrorObj("Expected an Aquarius function callback.");
            if (function.Parameters.Length != args.Length) return new ErrorObj($"Callback expects {function.Parameters.Length} arguments, got {args.Length}.");
            return VmEvaluator.NewInstance(this).Invoke(function, args);
        };
        builtinFuncs = new Dictionary<string, BuiltinObj> {
            {
                "長度", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("長度", 1, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;

                    var arg0 = args[0];
                    var arg0Type = arg0.GetType();
                    if (arg0Type == typeof(StringObj)) {
                        var arg0StrObj = (StringObj)arg0;
                        return new IntegerObj(arg0StrObj.Value.Length);
                    }

                    if (arg0Type == typeof(ArrayObj)) {
                        var arg0ArrObj = (ArrayObj)arg0;
                        return new IntegerObj(arg0ArrObj.Elements.Length);
                    }

                    return newError($"Argument to `長度` not supported, got {arg0.Type()}");
                })
            }, {
                "最後一個", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("最後一個", 1, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    if (args[0].Type() != ObjectType.ARRAY_OBJ)
                        return newError($"Argument to `最後一個` must be ARRAY, got {args[0].Type()}");

                    var array = (ArrayObj)args[0];
                    var length = array.Elements.Length;

                    return length > 0 ? array.Elements[length - 1] : RepeatedPrimitives.NULL;
                })
            }, {
                "其餘", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("其餘", 1, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    if (args[0].Type() != ObjectType.ARRAY_OBJ)
                        return newError($"Argument to `其餘` must be ARRAY, got {args[0].Type()}");
                    var arrayObj = (ArrayObj)args[0];
                    var length = arrayObj.Elements.Length;
                    if (length > 0) {
                        var newElements = arrayObj.Elements.Skip(1).ToArray();
                        return new ArrayObj(newElements);
                    }

                    return RepeatedPrimitives.NULL;
                })
            }, {
                "加入", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("加入", 2, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    if (args[0].Type() != ObjectType.ARRAY_OBJ)
                        return newError($"Argument to `加入` must be ARRAY, got {args[0].Type()}");

                    var arrayObj = (ArrayObj)args[0];
                    var newElements = Utils.PushToArray(arrayObj.Elements, args[1]);

                    return new ArrayObj(newElements);
                })
            }, {
                "印出", new BuiltinObj(args => {
                    for (var i = 0; i < args.Length; i++) {
                        Console.Write(args[i].Inspect());
                        if (i < args.Length - 1) Console.Write(" ");
                    }

                    Console.WriteLine();

                    return null;
                })
            }, { 
                "匯入", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("匯入", 1, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    if (args[0] is StringObj stringObj) {
                        if (graphics.TryImport(stringObj.Value, out ModuleObj nativeModule)) return nativeModule;
                        if (ScriptImport != null) return ScriptImport(stringObj.Value);
                        try {
                            String fileStr = File.ReadAllText(stringObj.Value);
                            Lexer lexer = Lexer.NewInstance(fileStr);
                            Parser parser = Parser.NewInstance(lexer);
                            AbstractSyntaxTree tree = parser.ParseAST();
                            if (lexer.Errors.Count != 0 || parser.Errors.Count != 0)
                                return newError(string.Join("\n", lexer.Errors.Select(error => error.Message).Concat(parser.Errors)));

                            VmEvaluator evaluator = VmEvaluator.NewInstance(this);
                            
                            Environment moduleEnv = Environment.NewEnvironment();
                            IObject result = evaluator.Eval(tree, moduleEnv);
                            if (result is ErrorObj) return result;
                            return new ModuleObj(moduleEnv);
                        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                            return newError($"匯入: {e.Message}");
                        }
                    } else {
                        return newError($"Argument 0 in built-in function '匯入' not STRING.");
                    }
                }) 
            }, {
               "是Windows", new BuiltinObj(args => {
                   ErrorObj argsCountMatch = checkArgsCount("是Windows", 0, args.Length);
                   if (argsCountMatch != null) return argsCountMatch;

                   return new BooleanObj(OperatingSystem.IsWindows());
               }) 
            }, {
                "是Linux", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("是Linux", 0, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;

                    return new BooleanObj(OperatingSystem.IsLinux());
                }) 
            }, {
                "是MacOS", new BuiltinObj(args => {
                    ErrorObj argsCountMatch = checkArgsCount("是MacOS", 0, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    return new BooleanObj(OperatingSystem.IsMacOS());
                }) 
            }, {
                "執行檔案", new BuiltinObj(args => { // Executes file synchronously.
                    ErrorObj argsCountMatch = checkArgsCount("執行檔案", 2, args.Length);
                    if (argsCountMatch != null) return argsCountMatch;
                    
                    StringBuilder builder = new StringBuilder();
                    ArrayObj args1Arr = (ArrayObj)args[1];
                    foreach (var args1ArrElement in args1Arr.Elements) {
                        builder.Append(((StringObj)args1ArrElement).Value).Append(' ');
                    }

                    string arguments = builder.ToString();

                    Process p = new Process();
                    p.StartInfo.FileName = ((StringObj)args[0]).Value;
                    p.StartInfo.Arguments = arguments;

                    bool started = p.Start();
                    if (!started) {
                        return RepeatedPrimitives.FALSE;
                    }

                    p.WaitForExit();
                    
                    return new BooleanObj(true);
                }) 
            }
        };
        foreach (var function in LibraryCatalog.GlobalFunctions)
            FunctionRegistration.Define(builtinFuncs, builtinFuncs[function.TraditionalChineseName], englishName: function.EnglishName);
        builtins = new Dictionary<string, IObject>();
    }
    
    public void NewDefaultBuiltins(string filePath) {
        _Builtins.Add("目前工作目錄",
            Utils.IsFullPath(filePath)
                ? new StringObj(Path.GetDirectoryName(filePath))
                : new StringObj(Path.GetDirectoryName(Path.Combine(System.Environment.CurrentDirectory, filePath))));
        _Builtins.Add("currWorkingDir", _Builtins["目前工作目錄"]);
    }

    public void Dispose() { if (ownsGraphics) graphics.Dispose(); }

    private ErrorObj checkArgsCount(string funcName, int expected, int actual) {
        if (expected != actual) {
            return newError($"Wrong number of arguments for '${funcName}'. Got{actual}, want ${expected}.");
        }

        return null;
    }
}
