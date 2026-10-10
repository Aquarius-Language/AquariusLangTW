using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using System.Runtime.CompilerServices;

namespace AquariusLang.Compiler;

/// <summary>Convenience host API for compiled programs and the compiler's intermediate representation.</summary>
public sealed class CompiledRuntime
{
    private static readonly ConditionalWeakTable<LoweredProgram, WasmProgram> compiled = new();
    private readonly WasmRuntime runtime;
    public CompiledRuntime(Builtins? builtins = null) { runtime = new WasmRuntime(builtins); }
    public IObject Execute(LoweredProgram program, AquariusLang.Object.Environment? environment = null) => runtime.Execute(compiled.GetValue(program, p => new WasmCompiler().Compile(p)), environment);
    public IObject Execute(WasmProgram program, AquariusLang.Object.Environment? environment = null, string? entry = null) => runtime.Execute(program, environment, entry);
    public IObject Invoke(IObject function, params IObject[] arguments) => runtime.Invoke(function, arguments);
}
