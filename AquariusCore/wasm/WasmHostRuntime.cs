using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Wasm;

/// <summary>Native capability adapter. Values, scopes and script execution live in Wasm.</summary>
public sealed class WasmRuntime : IDisposable
{
    public static Func<IWasmEngine>? EngineFactory { get; set; }
    private static readonly ConditionalWeakTable<FunctionObj, Callable> Callables = new();
    private static readonly ConditionalWeakTable<IObject, Origin> Origins = new();
    [ThreadStatic] private static List<Session>? active;
    private readonly Builtins builtins;
    private readonly IWasmEngine engine;
    private readonly ConditionalWeakTable<WasmProgram, Session> sessions = new();
    private bool disposed;
    private sealed record Callable(Session Session, int Value);
    private sealed record Origin(Session Session, int Tag, int Reference);
    public WasmRuntime(Builtins? builtins = null, IWasmEngine? engine = null)
    {
        this.builtins = builtins ?? new Builtins();
        this.engine = engine ?? EngineFactory?.Invoke() ?? throw new InvalidOperationException("This host has not registered a WebAssembly engine.");
    }
    public IObject Execute(WasmProgram program, AquaEnvironment? environment = null, string? entry = null, Builtins? context = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!program.Metadata.Modules.TryGetValue(entry ?? program.Metadata.Entry, out int function)) return new ErrorObj("Compiled module not found.");
        var session = active?.LastOrDefault(s => ReferenceEquals(s.Program, program));
        session ??= sessions.GetValue(program, p => new Session(p, engine));
        return session.Execute(function, environment, context ?? builtins)!;
    }
    public IObject Invoke(IObject function, params IObject[] arguments)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (function is BuiltinObj native) return native.BorrowsArguments ? native.InvokeBorrowed(arguments) : native.Fn(arguments);
        if (function is not FunctionObj script || !Callables.TryGetValue(script, out var callable)) return new ErrorObj("Not a compiled function.");
        if (script.Parameters.Length != arguments.Length) return new ErrorObj($"Function expects {script.Parameters.Length} arguments, got {arguments.Length}.");
        return callable.Session.Invoke(callable.Value, arguments)!;
    }
    public void Dispose()
    {
        if (disposed) return;
        var owned = sessions.Select(pair => pair.Value).ToArray();
        if (owned.Any(session => active?.Contains(session) == true))
            throw new InvalidOperationException("Cannot dispose a Wasm runtime during execution or callback reentry.");
        disposed = true;
        foreach (var session in owned) session.Dispose();
        sessions.Clear();
    }
    public static HashSet<object> Reachable(IEnumerable<IObject> roots)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance); var pending = new Stack<object>();
        void Add(object? item) { if (item is AquaEnvironment or ModuleObj or FunctionObj or BuiltinObj or ArrayObj or HashObj or ReturnValueObj && seen.Add(item)) pending.Push(item); }
        foreach (var root in roots) Add(root);
        foreach (var session in active ?? Enumerable.Empty<Session>()) foreach (var root in session.NativeRoots()) Add(root);
        while (pending.TryPop(out var item)) switch (item)
        {
            case AquaEnvironment scope: Add(scope.Outer); foreach (var v in scope.StoredValues) Add(v); break;
            case ModuleObj module: Add(module._Environment); break;
            case FunctionObj function: Add(function.Env); break;
            case BuiltinObj builtin: Add(builtin.RetainedEnvironment); break;
            case ArrayObj array: foreach (var v in array.Elements) Add(v); break;
            case HashObj hash: foreach (var pair in hash.Pairs.Values) { Add(pair.Key); Add(pair.Value); } break;
            case ReturnValueObj result: Add(result.Value); break;
        }
        return seen;
    }
    private sealed class Session : IDisposable
    {
        internal readonly WasmProgram Program;
        private readonly IWasmInstance instance;
        private bool disposed;
        private readonly List<Builtins> contexts = new();
        private readonly Dictionary<Builtins, int> contextIds = new(ReferenceEqualityComparer.Instance);
        private readonly List<IObject?> natives = new() { null };
        private readonly Dictionary<IObject, int> nativeIds = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<int> freeNativeIds = new();
        private readonly Dictionary<(int Tag, int Reference), WeakReference<IObject>> objects = new();
        private sealed record ScopeHandle(int Address);
        private readonly ConditionalWeakTable<AquaEnvironment, ScopeHandle> scopes = new();
        private readonly List<(WeakReference<AquaEnvironment> Owner, int Address, int Pin, bool Mirror)> scopeLeases = new();
        private readonly Dictionary<int, WeakReference<AquaEnvironment>> projectedScopes = new();
        private readonly Dictionary<string, int> texts = new(StringComparer.Ordinal);
        private readonly List<(WeakReference<IObject> Owner, int Pin)> leases = new();
        private readonly Stack<IObject[]> nativeArguments = new();
        private readonly HashSet<(int Tag, int Reference)> reading = new();
        private readonly HashSet<IObject> writing = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> dirtyArrays = new();
        private readonly Dictionary<int, List<WeakReference<AquaEnvironment>>> arrayOwners = new();
        internal Session(WasmProgram program, IWasmEngine engine)
        {
            // Native callback GC handles must not root the owning session forever.
            // Active executions and exported closures provide its actual lifetime.
            var owner = new WeakReference<Session>(this);
            Program = program; instance = engine.Instantiate(program.Bytes, new Dictionary<string, Delegate>
                { ["service"] = (Func<int, int, int, int, int, int, int, int>)((op, context, subject, name, arguments, count, output) =>
                    owner.TryGetTarget(out var session) ? session.Service(op, context, subject, name, arguments, count, output)
                        : throw new ObjectDisposedException(nameof(WasmRuntime))) });
            Call("aqua_initialize", program.Metadata.HeapStart);
        }
        ~Session() { try { Dispose(); } catch { /* Finalization cannot report native cleanup failures. */ } }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; instance?.Dispose();
            contexts.Clear(); contextIds.Clear(); natives.Clear(); nativeIds.Clear(); texts.Clear();
            objects.Clear(); scopeLeases.Clear(); leases.Clear(); projectedScopes.Clear();
            GC.SuppressFinalize(this);
        }
        private int Call(string name, params int[] args) { ObjectDisposedException.ThrowIf(disposed, this); return instance.Invoke(name, args); }
        private int Int(int address) => BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(address, 4));
        private void Int(int address, int n) => BinaryPrimitives.WriteInt32LittleEndian(instance.Memory(address, 4), n);
        private double Number(int address) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(instance.Memory(address, 8)));
        private void Number(int address, double n) => BinaryPrimitives.WriteInt64LittleEndian(instance.Memory(address, 8), BitConverter.DoubleToInt64Bits(n));
        private string Text(int address) { int length = Int(address); return Encoding.Unicode.GetString(instance.Memory(address + 4, checked(length * 2))); }
        private int Text(string text, bool intern = true)
        {
            if (texts.TryGetValue(text, out int address)) return address;
            address = Call("aqua_text", text.Length); Encoding.Unicode.GetBytes(text).CopyTo(instance.Memory(address + 4, text.Length * 2));
            if (intern) { int cell = Call("aqua_value"); WriteRaw(cell, 5, address); Call("aqua_pin", cell); texts.Add(text, address); } return address;
        }
        private void WriteRaw(int address, int tag, int reference = 0, double number = 0) { Int(address, tag); Int(address + 4, reference); Number(address + 8, number); }
        private int Native(IObject value) { if (nativeIds.TryGetValue(value, out int id)) return id; id = freeNativeIds.TryPop(out int reused) ? reused : natives.Count; if (id >= 65536) throw new InvalidOperationException("Native resource table capacity exceeded."); nativeIds.Add(value, id); if (id == natives.Count) natives.Add(value); else natives[id] = value; return id; }
        private void Lease(IObject value, int address, int tag, int reference)
        {
            Origins.Remove(value); Origins.Add(value, new Origin(this, tag, reference));
            int pin = Call("aqua_pin", address); leases.Add((new WeakReference<IObject>(value), pin));
            objects[(tag, reference)] = new(value);
            if (tag == 6) Call("aqua_array_expose", reference);
        }
        private void Collect()
        {
            for (int i = leases.Count - 1; i >= 0; i--) if (!leases[i].Owner.TryGetTarget(out _)) { Call("aqua_unpin", leases[i].Pin); leases.RemoveAt(i); }
            for (int i = scopeLeases.Count - 1; i >= 0; i--) if (!scopeLeases[i].Owner.TryGetTarget(out _)) { Call("aqua_unpin", scopeLeases[i].Pin); scopeLeases.RemoveAt(i); }
            Call("aqua_collect");
            for (int i = 1; i < natives.Count; i++) if (natives[i] is { } native && Call("aqua_native_live", i) == 0) { nativeIds.Remove(native); natives[i] = null; freeNativeIds.Push(i); }
        }
        internal IEnumerable<IObject> NativeRoots()
        {
            Collect(); for (int i = 1; i < natives.Count; i++) if (Call("aqua_native_live", i) != 0 && natives[i] != null) yield return natives[i]!;
            foreach (var arguments in nativeArguments) foreach (var value in arguments) yield return value;
        }
        private int EncodeScope(AquaEnvironment environment)
        {
            if (scopes.TryGetValue(environment, out var existing)) return existing.Address;
            int outer = environment.Outer == null ? 0 : EncodeScope(environment.Outer), scope = Call("aqua_scope", outer); scopes.Add(environment, new(scope));
            int root = Call("aqua_value"); WriteRaw(root, 9, scope); scopeLeases.Add((new(environment), scope, Call("aqua_pin", root), true));
            foreach (var binding in environment.OwnedBindings) Set(scope, binding.Key, binding.Value); return scope;
        }
        private void Set(int scope, string name, IObject? value) { int cell = Call("aqua_value"); Write(cell, value); Call("aqua_scope_set", scope, Text(name), cell); }
        private IReadOnlyDictionary<string, IObject> Bindings(int scope)
        {
            var result = new Dictionary<string, IObject>(); int count = Call("aqua_scope_count", scope);
            for (int i = 0; i < count; i++) { int binding = Call("aqua_scope_binding", scope, i); result.Add(Text(Int(binding)), Read(binding + 8)!); } return result;
        }
        private AquaEnvironment Project(int scope)
        {
            if (projectedScopes.TryGetValue(scope, out var cached) && cached.TryGetTarget(out var found)) return found;
            int outer = Int(scope);
            var environment = outer == 0 ? AquaEnvironment.NewEnvironment() : AquaEnvironment.NewEnclosedEnvironment(Project(outer)); projectedScopes[scope] = new(environment); scopes.Add(environment, new(scope));
            int root = Call("aqua_value"); WriteRaw(root, 9, scope); scopeLeases.Add((new(environment), scope, Call("aqua_pin", root), false));
            environment.ExternalBindings = () => Bindings(scope);
            environment.ExternalLookup = name => { int cell = Call("aqua_scope_get", scope, Text(name)); return (cell != 0, cell == 0 ? null : Read(cell)); };
            environment.ExternalCreate = (name, value) => Set(scope, name, value);
            environment.ExternalSet = (name, value) => { int cell = Call("aqua_value"); Write(cell, value); if (Call("aqua_scope_assign", scope, Text(name), cell) == 0) throw new KeyNotFoundException($"Identifier not found: {name}"); }; return environment;
        }
        private void SynchronizeIn() { foreach (var pair in scopeLeases.ToArray()) if (pair.Mirror && pair.Owner.TryGetTarget(out var environment)) foreach (var binding in environment.OwnedBindings) Set(pair.Address, binding.Key, binding.Value); }
        private void ArrayOwner(IObject? value, AquaEnvironment owner)
        {
            if (value is not ArrayObj || !Origins.TryGetValue(value, out var origin) || !ReferenceEquals(origin.Session, this)) return;
            if (!arrayOwners.TryGetValue(origin.Reference, out var owners)) arrayOwners.Add(origin.Reference, owners = new());
            if (!owners.Any(w => w.TryGetTarget(out var environment) && ReferenceEquals(environment, owner))) owners.Add(new(owner));
        }
        private void FlushDirtyArrays(AquaEnvironment? owner = null)
        {
            int reference;
            while ((reference = Call("aqua_take_dirty_array")) != 0) dirtyArrays.Add(reference);
            foreach (int item in dirtyArrays.ToArray())
            {
                if (!objects.TryGetValue((6, item), out var cached) || !cached.TryGetTarget(out _)) { dirtyArrays.Remove(item); arrayOwners.Remove(item); continue; }
                if (owner != null && (!arrayOwners.TryGetValue(item, out var owners) || !owners.Any(w => w.TryGetTarget(out var environment) && ReferenceEquals(environment, owner)))) continue;
                Refresh(6, item); dirtyArrays.Remove(item);
            }
        }
        private void SynchronizeOut()
        {
            FlushDirtyArrays();
            foreach (var pair in scopeLeases.ToArray()) if (pair.Mirror && pair.Owner.TryGetTarget(out var environment)) foreach (var binding in Bindings(pair.Address)) environment.Create(binding.Key, binding.Value);
        }
        internal IObject? Execute(int function, AquaEnvironment? environment, Builtins builtins)
        {
            if (!contextIds.TryGetValue(builtins, out int context)) {
                context = contexts.Count;
                if (context >= 65536) throw new InvalidOperationException("Wasm capability context capacity exceeded.");
                contexts.Add(builtins); contextIds.Add(builtins, context);
            }
            // Anonymous executions have no observable host scope to mirror or pin.
            int scope = environment == null ? Call("aqua_scope", 0) : EncodeScope(environment);
            if (active?.Contains(this) != true) SynchronizeIn();
            return Run(Call("aqua_execute", Program.Metadata.ProgramAddress, function, scope, context));
        }
        internal IObject? Invoke(int callable, IObject[] arguments)
        {
            if (active?.Contains(this) != true) SynchronizeIn(); int args = Call("aqua_allocate", checked(arguments.Length * 16)); for (int i = 0; i < arguments.Length; i++) Write(args + i * 16, arguments[i]);
            return Run(Call("aqua_invoke", Int(callable + 4), args, arguments.Length));
        }
        private IObject? Run(int execution)
        {
            (active ??= new()).Add(this);
            try { while (true) { int status = Call("aqua_run", execution, WasmAbi.Quantum); if (status == 1) { Collect(); continue; }
                if (status == 2) throw new InvalidOperationException("Unresolved desktop async operation."); return Read(Call("aqua_result", execution)); } }
            finally { SynchronizeOut(); Call("aqua_release_execution", execution); active.RemoveAt(active.Count - 1); }
        }
        private int Service(int operation, int context, int subject, int name, int arguments, int count, int output)
        {
            if (operation == 0) { string key = Text(name); var builtin = contexts[context];
                if (builtin.BuiltinFuncs.TryGetValue(key, out var function)) { Write(output, function); return 0; }
                if (builtin._Builtins.TryGetValue(key, out var value)) { Write(output, value); return 0; } return 1; }
            if (operation == 2) { if (natives[subject] is not ModuleObj module) return 1; FlushDirtyArrays(module._Environment);
                var member = module._Environment.Get(Text(name), out bool found); if (!found) return 1; Write(output, member); ArrayOwner(member, module._Environment); return 0; }
            if (operation == 3) { if (natives[subject] is not ModuleObj module) return 1; WriteRaw(output, 9, EncodeScope(module._Environment)); return 0; }
            if (operation != 1 || natives[subject] == null) throw new InvalidOperationException("Invalid native service request.");
            var args = new IObject[count]; for (int i = 0; i < count; i++) args[i] = Read(arguments + i * 16)!; nativeArguments.Push(args);
            bool synchronize = natives[subject] is not BuiltinObj { RetainedEnvironment: not null };
            try { if (synchronize) SynchronizeOut(); else FlushDirtyArrays(((BuiltinObj)natives[subject]!).RetainedEnvironment); IObject? result = natives[subject] switch { BuiltinObj fn => fn.BorrowsArguments ? fn.InvokeBorrowed(args) : fn.Fn(args),
                FunctionObj fn when Callables.TryGetValue(fn, out var compiled) => compiled.Session.Invoke(compiled.Value, args), _ => new ErrorObj("Not a function.") };
                if (synchronize) SynchronizeIn(); for (int i = 0; i < count; i++) Write(arguments + i * 16, args[i]); Write(output, result); if (!synchronize) ArrayOwner(result, ((BuiltinObj)natives[subject]!).RetainedEnvironment!); return 0; }
            finally { nativeArguments.Pop(); }
        }
        private void Write(int address, IObject? value)
        {
            switch (value) {
                case null: WriteRaw(address, 0); return;
                case IntegerObj n: WriteRaw(address, 1, number: n.Value); return;
                case FloatObj n: WriteRaw(address, 2, number: n.Value); return;
                case DoubleObj n: WriteRaw(address, 3, number: n.Value); return;
                case BooleanObj b: WriteRaw(address, 4, number: b.Value ? 1 : 0); return;
                case NullObj: WriteRaw(address, 11); return;
                case BreakObj: WriteRaw(address, 12); return;
                case StringObj text: WriteRaw(address, 5, Text(text.Value, false)); return;
                case ErrorObj error: WriteRaw(address, 13, Text(error.Message, false)); return;
                case BuiltinObj portable when portable.PortableId != 0: WriteRaw(address, 14, portable.PortableId); return;
            }
            if (Origins.TryGetValue(value, out var origin) && ReferenceEquals(origin.Session, this)) {
                WriteRaw(address, origin.Tag, origin.Reference);
                if (!writing.Add(value)) return;
                try {
                    if (value is ArrayObj array) { if (Call("aqua_array_count", origin.Reference) != array.Elements.Length) Call("aqua_array_resize", origin.Reference, array.Elements.Length); int items = Call("aqua_array_values", origin.Reference); for (int i = 0; i < array.Elements.Length; i++) Write(items + i * 16, array.Elements[i]); }
                    else if (value is HashObj mirroredHash) { Call("aqua_hash_resize", origin.Reference, mirroredHash.Pairs.Count); int items = Call("aqua_hash_pairs", origin.Reference), i = 0; foreach (var pair in mirroredHash.Pairs.Values) { Write(items + i * 32, pair.Key); Write(items + i * 32 + 16, pair.Value); i++; } }
                }
                finally { writing.Remove(value); } return;
            }
            if (value is ArrayObj a) { int reference = Call("aqua_array", a.Elements.Length); WriteRaw(address, 6, reference); Lease(a, address, 6, reference);
                int items = Call("aqua_array_values", reference); for (int i = 0; i < a.Elements.Length; i++) Write(items + i * 16, a.Elements[i]); return; }
            if (value is HashObj hash) { int reference = Call("aqua_hash", hash.Pairs.Count); WriteRaw(address, 7, reference); Lease(hash, address, 7, reference); int items = Call("aqua_hash_pairs", reference), i = 0;
                foreach (var pair in hash.Pairs.Values) { Write(items + i * 32, pair.Key); Write(items + i * 32 + 16, pair.Value); i++; } return; }
            if (value is ModuleObj module && scopes.TryGetValue(module._Environment, out var moduleScope)) { WriteRaw(address, 9, moduleScope.Address); Lease(value, address, 9, moduleScope.Address); return; }
            WriteRaw(address, 10, Native(value));
        }
        private IObject? Read(int address)
        {
            int tag = Int(address), reference = Int(address + 4); double number = Number(address + 8);
            switch (tag) { case 0: return null; case 1: return new IntegerObj((int)number); case 2: return new FloatObj((float)number); case 3: return new DoubleObj(number);
                case 4: return number != 0 ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE; case 5: return new StringObj(Text(reference)); case 10: return natives[reference];
                case 11: return RepeatedPrimitives.NULL; case 12: return RepeatedPrimitives.BREAK; case 13: return new ErrorObj(Text(reference));
                case 14: return contexts.SelectMany(c => c.BuiltinFuncs.Values).FirstOrDefault(f => f.PortableId == reference) ?? throw new InvalidOperationException("Unknown portable builtin."); }
            if (objects.TryGetValue((tag, reference), out var cached) && cached.TryGetTarget(out var existing)) { Refresh(tag, reference); return existing; }
            IObject result;
            switch (tag) {
                case 6: var array = new ArrayObj(new IObject[Call("aqua_array_count", reference)]); result = array; objects[(tag, reference)] = new(array); Lease(result, address, tag, reference);
                    Refresh(tag, reference); return result;
                case 7: var pairs = new Dictionary<HashKey, HashPair>(); result = new HashObj(pairs); objects[(tag, reference)] = new(result); Lease(result, address, tag, reference);
                    Refresh(tag, reference); return result;
                case 8: int function = Int(reference + 4), scope = Int(reference + 8); var metadata = Program.Metadata.Functions[function];
                    var parameters = (metadata.Parameters ?? Array.Empty<string>()).Select(p => new Identifier(new AquariusLang.token.Token { Type = AquariusLang.token.TokenType.IDENT, Literal = p }, p)).ToArray();
                    string display = Program.Metadata.Functions.SelectMany(f => f.Pool).FirstOrDefault(c => c.Type == "function" && c.Function == function)?.Display ?? "";
                    var fn = new FunctionObj(parameters, null, Project(scope), display); result = fn;
                    int callable = Call("aqua_pin", address); Callables.Add(fn, new Callable(this, callable)); leases.Add((new(fn), callable)); break;
                case 9: result = new ModuleObj(Project(reference)); break;
                default: throw new InvalidOperationException($"Invalid Wasm value tag: {tag}");
            }
            objects[(tag, reference)] = new(result); Lease(result, address, tag, reference); return result;
        }
        private void Refresh(int tag, int reference)
        {
            if (!objects.TryGetValue((tag, reference), out var owner) || !owner.TryGetTarget(out var value) || !reading.Add((tag, reference))) return;
            try
            {
                if (value is ArrayObj array)
                {
                    int count = Call("aqua_array_count", reference); if (array.Elements.Length != count) array.Elements = new IObject[count];
                    int items = Call("aqua_array_values", reference);
                    for (int i = 0; i < array.Elements.Length; i++) array.Elements[i] = Read(items + i * 16)!;
                }
                else if (value is HashObj hash)
                {
                    int items = Call("aqua_hash_pairs", reference), count = Call("aqua_hash_count", reference); hash.Pairs.Clear();
                    for (int i = 0; i < count; i++) { var key = Read(items + i * 32)!; hash.Pairs[((IHashable)key).HashKey()] = new HashPair(key, Read(items + i * 32 + 16)!); }
                }
            }
            finally { reading.Remove((tag, reference)); }
        }
    }
}
