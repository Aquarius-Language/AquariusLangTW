using System;
using System.Runtime.CompilerServices;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.Object;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.VM;

/// <summary>Source and AST entry point backed exclusively by compiled VM instructions.</summary>
public sealed class VmEvaluator {
    private static readonly ConditionalWeakTable<INode, Bytecode> compiled = new();
    private static readonly ConditionalWeakTable<FunctionObj, Bytecode> functions = new();
    private readonly VirtualMachine machine;
    private VmEvaluator(Builtins builtins) { machine = new VirtualMachine(builtins); }
    public static VmEvaluator NewInstance(Builtins builtins) => new(builtins);
    internal static Bytecode GetCompiled(INode node) => compiled.GetValue(node, key => new VmCompiler().Compile(key));
    internal static Bytecode GetFunctionCode(FunctionObj function) => functions.GetValue(function, key => GetCompiled(key.Body));
    internal static void Register(FunctionObj function, Bytecode program) => functions.Add(function, program);

    public IObject Eval(INode node, AquaEnvironment environment) {
        try { return machine.Execute(GetCompiled(node), environment); }
        catch (VmCompilationException error) { return new ErrorObj(error.Message); }
    }
    public IObject Evaluate(string source, AquaEnvironment? environment = null) {
        try { return machine.Execute(new VmCompiler().Compile(source), environment); }
        catch (VmCompilationException error) { return new ErrorObj(error.Message); }
    }
    public IObject Invoke(IObject function, params IObject[] arguments) => machine.Invoke(function, arguments);
    public ErrorObj NewError(string message) => new(message);
}
