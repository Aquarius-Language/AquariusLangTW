using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Compiler;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Wasm;

/// <summary>Dynamic language services and continuation scheduling. All program control flow executes in WebAssembly.</summary>
public sealed class WasmRuntime
{
    public static Func<IWasmEngine>? EngineFactory { get; set; }
    private static readonly ConditionalWeakTable<FunctionObj, Closure> Closures = new();
    private sealed record Closure(WasmProgram Program, int Function, Builtins Builtins);
    [ThreadStatic] private static List<Execution>? activeExecutions;
    /// <summary>Trace language roots at a host safe point, including callback reentry and native arguments.</summary>
    public static HashSet<object> Reachable(IEnumerable<IObject> roots)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        void Add(object? value) {
            if (value is AquaEnvironment or ModuleObj or FunctionObj or BuiltinObj or ArrayObj or HashObj or ReturnValueObj && seen.Add(value)) pending.Push(value);
        }
        foreach (var root in roots) Add(root);
        foreach (var execution in activeExecutions ?? Enumerable.Empty<Execution>()) {
            foreach (var frame in execution.frames) {
                Add(frame.Environment);
                foreach (var value in frame.Values) Add(value);
                foreach (var loop in frame.Loops) Add(loop.Outer);
                foreach (var value in frame.Builtins._Builtins.Values) Add(value);
            }
            foreach (var call in execution.nativeArguments) { Add(call.Function); foreach (var value in call.Arguments) Add(value); }
        }
        while (pending.TryPop(out var value)) {
            switch (value) {
                case AquaEnvironment scope: Add(scope.Outer); foreach (var item in scope.StoredValues) Add(item); break;
                case ModuleObj module: Add(module._Environment); break;
                case FunctionObj function: Add(function.Env); break;
                case BuiltinObj builtin: Add(builtin.RetainedEnvironment); break;
                case ArrayObj array: foreach (var item in array.Elements) Add(item); break;
                case HashObj hash: foreach (var pair in hash.Pairs.Values) { Add(pair.Key); Add(pair.Value); } break;
                case ReturnValueObj returned: Add(returned.Value); break;
            }
        }
        return seen;
    }
    private readonly Builtins builtins;
    private readonly IWasmEngine engine;
    public WasmRuntime(Builtins? builtins = null, IWasmEngine? engine = null)
    {
        this.builtins = builtins ?? new Builtins();
        this.engine = engine ?? EngineFactory?.Invoke() ?? throw new InvalidOperationException("This host has not registered a WebAssembly engine.");
    }
    public IObject Execute(WasmProgram program, AquaEnvironment? environment = null, string? entry = null)
    {
        string name = entry ?? program.Metadata.Entry;
        if (!program.Metadata.Modules.TryGetValue(name, out int index)) return new ErrorObj($"Module not found: {name}");
        using var execution = new Execution(engine);
        return execution.Run(new Frame(program, index, environment ?? AquaEnvironment.NewEnvironment(), builtins), true)!;
    }
    public IObject Invoke(IObject function, params IObject[] arguments)
    {
        if (function is BuiltinObj b) return b.Fn(arguments);
        if (function is not FunctionObj f || !Closures.TryGetValue(f, out var c)) return new ErrorObj($"Not a compiled function: {function?.Type() ?? "NULL"}");
        if (arguments.Length != f.Parameters.Length) return new ErrorObj($"Function expects {f.Parameters.Length} arguments, got {arguments.Length}.");
        var env = AquaEnvironment.NewEnclosedEnvironment(f.Env);
        for (int i = 0; i < arguments.Length; i++) env.Create(f.Parameters[i].Value, arguments[i]);
        using var execution = new Execution(engine);
        return execution.Run(new Frame(c.Program, c.Function, env, c.Builtins), false)!;
    }
    private sealed class Frame(WasmProgram program, int function, AquaEnvironment env, Builtins builtins)
    {
        internal readonly WasmProgram Program = program;
        internal readonly int Function = function;
        internal AquaEnvironment Environment = env;
        internal readonly Builtins Builtins = builtins;
        internal readonly List<IObject?> Values = new();
        internal readonly Stack<(AquaEnvironment Outer, int Base, string? Binding)> Loops = new();
        internal int Continuation;
        internal bool Returned;
        internal WasmConstant Constant(int n) => Program.Metadata.Functions[Function].Pool[n];
    }
    private sealed class LanguageError(ErrorObj error) : Exception(error.Message) { internal ErrorObj Error = error; }
    private sealed class Execution(IWasmEngine engine) : IDisposable
    {
        private readonly Dictionary<WasmProgram, IWasmInstance> instances = new();
        internal readonly Stack<Frame> frames = new();
        internal readonly Stack<(IObject Function, IObject[] Arguments)> nativeArguments = new();
        private Frame current = null!;
        private int budget;
        private Frame? child;
        private void Fail(string message) => throw new LanguageError(new ErrorObj(message));
        private void Push(IObject? value) { if (value is ErrorObj e) throw new LanguageError(e); current.Values.Add(value); }
        private IObject? Pop() { int i = current.Values.Count - 1; var v = current.Values[i]; current.Values.RemoveAt(i); return v; }
        private IObject? Peek(int depth = 1) => current.Values[current.Values.Count - depth];
        private IObject? Load(string name)
        {
            var value = current.Environment.Get(name, out bool found); if (found) return value;
            if (current.Builtins.BuiltinFuncs.TryGetValue(name, out var fn)) return fn;
            if (current.Builtins._Builtins.TryGetValue(name, out value)) return value;
            Fail($"Identifier not found: {name}"); return null;
        }
        private void Set(string name, IObject? value) { current.Environment.Get(name, out bool found); if (!found) Fail($"Identifier not found: {name}"); current.Environment.Set(name, value!); }
        private IObject[] Arguments(int n) { var values = new IObject[n]; for (int i = n - 1; i >= 0; i--) values[i] = Pop()!; return values; }
        private void CheckArray() { var error = ValueOperations.CheckArrayWrite(Peek(2), Peek()); if (error != null) throw new LanguageError(error); }
        private void HashKey() { if (Peek() is not IHashable) Fail($"Unusable as hash key: {Peek()?.Type() ?? "NULL"}"); }
        private void Increment(int n, bool prefix)
        {
            string name = current.Constant(n).Text!; var v = current.Environment.Get(name, out bool found);
            if (!found) Fail($"Identifier not found: {name}");
            if (v is not INumberObj) Fail($"++ 只適用於數值變數，得到 {v?.Type() ?? "NULL"}。");
            var next = ValueOperations.Number(((INumberObj)v!).GetNumValue() + 1, v!); Set(name, next); Push(prefix ? next : v);
        }
        private void Call(int n)
        {
            var args = Arguments(n); var callee = Pop();
            if (callee is BuiltinObj builtin) {
                nativeArguments.Push((callee, args));
                try { Push(builtin.Fn(args)); } finally { nativeArguments.Pop(); }
                return;
            }
            if (callee is not FunctionObj f || !Closures.TryGetValue(f, out var closure)) { Fail($"Not a function: {callee?.Type() ?? "NULL"}"); return; }
            if (f.Parameters.Length != n) Fail($"Function expects {f.Parameters.Length} arguments, got {n}.");
            var env = AquaEnvironment.NewEnclosedEnvironment(f.Env);
            for (int i = 0; i < n; i++) env.Create(f.Parameters[i].Value, args[i]);
            child = new Frame(closure.Program, closure.Function, env, closure.Builtins);
        }
        private Dictionary<string, Delegate> Imports()
        {
            var map = new Dictionary<string, Delegate>();
            void A(string name, Action fn) => map.Add(name, fn);
            void N(string name, Action<int> fn) => map.Add(name, fn);
            void R(string name, Func<int> fn) => map.Add(name, fn);
            R("checkpoint", () => --budget < 0 ? 1 : 0);
            N("constant", n =>
            {
                var c = current.Constant(n); Push(c.Type switch
                {
                    "int" => new IntegerObj((int)c.Number),
                    "float" => new FloatObj((float)c.Number),
                    "double" => new DoubleObj(c.Number),
                    "string" => new StringObj(c.Text!),
                    "bool" => c.Boolean ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE,
                    "null" => RepeatedPrimitives.NULL,
                    "break" => RepeatedPrimitives.BREAK,
                    _ => throw new InvalidOperationException("Invalid literal")
                });
            });
            A("void", () => Push(null)); A("null", () => Push(RepeatedPrimitives.NULL)); A("pop", () => Pop()); A("duplicate", () => Push(Peek()));
            N("load", n => Push(Load(current.Constant(n).Text!)));
            N("declare", n => { current.Environment.Create(current.Constant(n).Text!, Pop()!); Push(null); });
            N("assign", n => { var v = Pop(); Pop(); Set(current.Constant(n).Text!, v); Push(null); });
            N("compoundAssign", n => { var c = current.Constant(n); var r = Pop(); Pop(); var v = ValueOperations.Compound(Enum.Parse<IrOperation>(c.Operation!), Load(c.Text!), r); if (v is ErrorObj e) throw new LanguageError(e); Set(c.Text!, v); Push(null); });
            foreach (var op in new[] { IrOperation.Add, IrOperation.Subtract, IrOperation.Multiply, IrOperation.Divide, IrOperation.Less, IrOperation.Greater, IrOperation.LessEqual, IrOperation.GreaterEqual, IrOperation.Equal, IrOperation.NotEqual, IrOperation.And, IrOperation.Or })
            {
                A(char.ToLowerInvariant(op.ToString()[0]) + op.ToString()[1..], () => { var r = Pop(); Push(ValueOperations.Binary(op, Pop(), r)); });
            }
            A("not", () => { var v = Pop(); Push(ValueOperations.Bool(v is BooleanObj b ? !b.Value : v == RepeatedPrimitives.NULL)); });
            A("negate", () => { var v = Pop(); Push(v is INumberObj n ? ValueOperations.Number(-n.GetNumValue(), v) : new ErrorObj($"Unknown operator: -{v?.Type() ?? "NULL"}")); });
            N("incrementPrefix", n => Increment(n, true)); N("incrementPostfix", n => Increment(n, false));
            R("truth", () => ValueOperations.Truthy(Pop()) ? 1 : 0); R("isBreak", () => Peek() is BreakObj ? 1 : 0);
            N("closure", n =>
            {
                var c = current.Constant(n); var f = new FunctionObj(c.Parameters!.Select(p => new Identifier(new AquariusLang.token.Token { Type = AquariusLang.token.TokenType.IDENT, Literal = p }, p)).ToArray(), null, current.Environment, c.Display);
                Closures.Add(f, new Closure(current.Program, c.Function, current.Builtins)); Push(f);
            });
            N("call", Call); A("returned", () => current.Returned = true);
            N("array", n => Push(new ArrayObj(Arguments(n)))); A("checkHashKey", HashKey);
            N("hash", n => { var pairs = new Dictionary<HashKey, HashPair>(); var a = Arguments(n * 2); for (int i = 0; i < a.Length; i += 2) pairs[((IHashable)a[i]).HashKey()] = new HashPair(a[i], a[i + 1]); Push(new HashObj(pairs)); });
            A("index", () => { var i = Pop(); Push(ValueOperations.Index(Pop(), i)); }); A("checkArrayWrite", CheckArray);
            A("writeIndex", () => { var v = Pop(); CheckArray(); var i = Pop(); var a = Pop(); ((ArrayObj)a!).Elements[((IntegerObj)i!).Value] = v!; Push(v); });
            N("member", n => { var m = Pop(); if (m is not ModuleObj) Fail($"Cannot access member of {m?.Type() ?? "NULL"}"); string name = current.Constant(n).Text!; var v = ((ModuleObj)m!)._Environment.Get(name, out bool found); if (!found) Fail($"Module member not found: {name}"); Push(v); });
            N("resolveMemberFunction", n => { var m = Pop(); if (m is not ModuleObj) Fail($"Cannot access member of {m?.Type() ?? "NULL"}"); child = new Frame(current.Program, current.Constant(n).Function, ((ModuleObj)m!)._Environment, current.Builtins); });
            N("enterLoop", n => { current.Loops.Push((current.Environment, current.Values.Count, n < 0 ? null : current.Constant(n).Text)); current.Environment = AquaEnvironment.NewEnclosedEnvironment(current.Environment); });
            R("loopCondition", () => { var v = Pop(); if (v == null) Fail("No boolean result for for loop condition evaluation. Got=NULL."); if (v is not BooleanObj) Fail($"Expected bool from for loop conditionals. Got={v!.Type()}"); return ((BooleanObj)v!).Value ? 1 : 0; });
            A("nextIteration", () => { var l = current.Loops.Peek(); var v = l.Binding == null ? null : current.Environment.Get(l.Binding, out _); current.Environment = AquaEnvironment.NewEnclosedEnvironment(l.Outer); if (l.Binding != null) current.Environment.Create(l.Binding, v!); });
            A("break", () => { int b = current.Loops.Peek().Base; current.Values.RemoveRange(b, current.Values.Count - b); });
            A("leaveLoop", () => { var l = current.Loops.Pop(); current.Values.RemoveRange(l.Base, current.Values.Count - l.Base); current.Environment = l.Outer; Push(null); });
            N("error", n => Fail(current.Constant(n).Text!)); return map;
        }
        internal IObject? Run(Frame root, bool wrapReturn)
        {
            frames.Push(root);
            (activeExecutions ??= new()).Add(this);
            try
            {
                while (frames.Count > 0)
                {
                    current = frames.Peek(); child = null; budget = WasmAbi.Quantum;
                    if (!instances.TryGetValue(current.Program, out var instance)) { instance = engine.Instantiate(current.Program.Bytes, Imports()); instances.Add(current.Program, instance); }
                    int next = instance.GetFunction(current.Function)(current.Continuation);
                    if (next < 0)
                    {
                        var result = current.Values.Count > 0 ? Pop() : null; bool returned = current.Returned; frames.Pop();
                        if (frames.Count == 0) return returned && wrapReturn && current.Program.Metadata.Functions[current.Function].WrapReturn ? new ReturnValueObj(result!) : result;
                        current = frames.Peek(); Push(result);
                    }
                    else { current.Continuation = next; if (child != null) frames.Push(child); }
                }
            }
            catch (LanguageError e) { return e.Error; }
            finally { activeExecutions.Remove(this); }
            return null;
        }
        public void Dispose() { foreach (var instance in instances.Values) instance.Dispose(); }
    }
}
