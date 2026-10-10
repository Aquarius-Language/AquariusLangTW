using System.Runtime.CompilerServices;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.Object;
using AquariusLang.Wasm;

namespace AquariusLang.Compiler;

/// <summary>Compile-and-run convenience API for interactive hosts. No AST or instruction interpreter is used.</summary>
public sealed class CompiledEvaluator
{
    private static readonly ConditionalWeakTable<INode, WasmProgram> compiled = new();
    private readonly WasmRuntime runtime;
    private CompiledEvaluator(Builtins builtins) { runtime = new WasmRuntime(builtins); }
    public static CompiledEvaluator NewInstance(Builtins builtins) => new(builtins);
    public IObject Eval(INode node, AquariusLang.Object.Environment environment)
    {
        try { return runtime.Execute(compiled.GetValue(node, n => new WasmCompiler().Compile(n)), environment); }
        catch (CompilationException e) { return new ErrorObj(e.Message); }
    }
    public IObject Evaluate(string source, AquariusLang.Object.Environment? environment = null)
    {
        try { return runtime.Execute(new WasmCompiler().Compile(source), environment); }
        catch (CompilationException e) { return new ErrorObj(e.Message); }
    }
    public IObject Invoke(IObject function, params IObject[] arguments) => runtime.Invoke(function, arguments);
    public ErrorObj NewError(string message) => new(message);
}
