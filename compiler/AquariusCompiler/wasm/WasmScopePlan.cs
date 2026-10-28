using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Wasm;

/// <summary>
/// Lexical scope plan for one compiled body. A lexical scope that contains no closure creation can
/// never be captured, projected to a host or reached from another frame, so its bindings are stored in
/// private frame slots instead of a heap scope ("promoted" scopes). Scopes that contain closures keep
/// the dynamic representation. Promotion is upward closed: every scope nested in a promoted scope is
/// promoted, so the runtime scope chain always holds exactly the dynamic ancestors.
/// The plan also records static operand depths and a forward dataflow of known value tags and
/// definite declaration states that the emitter uses to remove redundant guards.
/// </summary>
internal sealed class WasmScopePlan
{
    internal const int Unknown = -1;
    private const long MaxSlotStates = 4_000_000;
    /// <summary>Register slots are reloaded on every entry and stored before every resumable exit.</summary>
    private const int MaxRegisterSlots = 32;
    internal const byte No = 0, Yes = 1, Maybe = 2;
    internal const int Int = 1, Float = 2, Double = 3, Bool = 4, String = 5, Array = 6, Hash = 7, Closure = 8, Null = 11, Break = 12;

    internal sealed class StaticScope
    {
        internal int Parent = -1;
        internal bool HasClosure, Promoted;
        internal string? LoopName;
        internal readonly Dictionary<string, int> Slots = new(StringComparer.Ordinal);
        internal readonly List<int> SlotList = new();
    }

    /// <summary>Resolution of a named access. Slots are promoted candidates from the innermost scope
    /// outwards; the dynamic candidate (if any) is the existing guarded lexical slot.</summary>
    internal sealed record Access(int[] Slots, WasmBindingSlots.Candidate? Dynamic, bool HasDynamic);

    internal readonly LoweredProgram Body;
    internal readonly StaticScope[] Scopes;
    internal readonly int[] ScopeAt;
    internal readonly int Locals;
    internal readonly bool PromotedFunction;
    internal readonly int[] Depth;
    internal readonly int[] LoopBase;
    internal readonly int[]?[] StackTags;
    /// <summary>Operand tags produced by instruction i before merging into its successors.</summary>
    internal readonly int[]?[] OutTags;
    internal readonly byte[]?[] SlotDecl;
    internal readonly int[]?[] SlotTags;
    internal readonly Access?[] Accesses;
    internal readonly int[] DeclareSlot;
    internal readonly int[] SlotScope;
    /// <summary>Slots whose every access is definite and whose every write has the same scalar tag. The
    /// emitter keeps their number in a Wasm local; the frame keeps the tag (declared state) and receives
    /// the number before the activation can be resumed.</summary>
    internal Dictionary<int, int> RegisterSlots { get; private set; } = new();

    private WasmScopePlan(LoweredProgram body, StaticScope[] scopes, int[] scopeAt, int locals, bool promotedFunction,
        int[] depth, int[] loopBase, int[]?[] stackTags, int[]?[] outTags, byte[]?[] slotDecl, int[]?[] slotTags, Access?[] accesses, int[] declareSlot, int[] slotScope)
    {
        Body = body; Scopes = scopes; ScopeAt = scopeAt; Locals = locals; PromotedFunction = promotedFunction;
        Depth = depth; LoopBase = loopBase; StackTags = stackTags; OutTags = outTags; SlotDecl = slotDecl; SlotTags = slotTags;
        Accesses = accesses; DeclareSlot = declareSlot; SlotScope = slotScope;
    }

    internal static string? NameOf(LoweredProgram body, Instruction op) => op.Code switch {
        IrOperation.Load or IrOperation.Assign or IrOperation.IncrementPrefix or IrOperation.IncrementPostfix or IrOperation.Declare => (string)body.Pool[op.Operand],
        IrOperation.CompoundAssign => ((Assignment)body.Pool[op.Operand]).Name,
        _ => null
    };

    /// <param name="parameters">Parameters for a function body; null for module or computed-member bodies,
    /// whose outermost scope is supplied by the host or a module and therefore stays dynamic.</param>
    /// <returns>Null when frame-slot promotion is requested but unsupported for this body; <paramref name="rejection"/>
    /// then says why. Without promotion every well-formed body has a plan.</returns>
    internal static WasmScopePlan? Analyze(LoweredProgram body, string[]? parameters, bool promote, out string? rejection)
    {
        rejection = null;
        var code = body.Code;
        // 1. Static scope tree.
        var scopes = new List<StaticScope> { new() };
        var scopeAt = new int[code.Length + 1];
        var stack = new Stack<int>(); stack.Push(0);
        for (int i = 0; i < code.Length; i++) {
            scopeAt[i] = stack.Peek();
            if (code[i].Code == IrOperation.EnterLoop) {
                var child = new StaticScope { Parent = stack.Peek(), LoopName = code[i].Operand < 0 ? null : (string)body.Pool[code[i].Operand] };
                scopes.Add(child); stack.Push(scopes.Count - 1);
            } else if (code[i].Code == IrOperation.LeaveLoop) {
                if (stack.Count == 1) throw new CompilationException("Invalid compiler loop scope.");
                stack.Pop();
            } else if (code[i].Code == IrOperation.Closure) {
                for (int s = stack.Peek(); s >= 0; s = scopes[s].Parent) scopes[s].HasClosure = true;
            }
        }
        scopeAt[code.Length] = 0;
        bool distinct = parameters == null || parameters.Distinct(StringComparer.Ordinal).Count() == parameters.Length;
        for (int s = 0; s < scopes.Count; s++)
            scopes[s].Promoted = promote && !scopes[s].HasClosure && (s != 0 || parameters != null && distinct);
        // Only the innermost scopes can be promoted: verify upward closure explicitly.
        for (int s = 1; s < scopes.Count; s++)
            if (scopes[scopes[s].Parent].Promoted && !scopes[s].Promoted) { rejection = "Promotion must be upward closed."; return null; }

        // 2. Slots for promoted scopes. Parameters occupy the first slots of a promoted function.
        int locals = 0; var slotScope = new List<int>();
        int AddSlot(int s, string name) {
            if (scopes[s].Slots.TryGetValue(name, out int slot)) return slot;
            scopes[s].Slots.Add(name, locals); scopes[s].SlotList.Add(locals); slotScope.Add(s); return locals++;
        }
        bool promotedFunction = scopes[0].Promoted;
        if (promotedFunction) foreach (var p in parameters!) AddSlot(0, p);
        var declareSlot = Enumerable.Repeat(-1, code.Length).ToArray();
        for (int i = 0; i < code.Length; i++)
            if (code[i].Code == IrOperation.Declare && scopes[scopeAt[i]].Promoted)
                declareSlot[i] = AddSlot(scopeAt[i], (string)body.Pool[code[i].Operand]);
        foreach (var scope in scopes)
            if (scope.Promoted && scope.LoopName != null && !scope.Slots.ContainsKey(scope.LoopName))
                { rejection = "A promoted loop binding must be declared in its loop scope."; return null; }
        // The dataflow keeps per-instruction slot states; bound its memory for very large generated bodies.
        if (promote && (long)(code.Length + 1) * Math.Max(1, locals) > MaxSlotStates)
            { rejection = "Body too large for frame-slot promotion."; return null; }

        // 3. Named accesses: promoted candidates, then the guarded dynamic lexical slot of WasmBindingSlots.
        var accesses = new Access?[code.Length];
        var dynamicOrder = new Dictionary<int, Dictionary<string, int>>();
        Dictionary<string, int> Order(int s) {
            if (!dynamicOrder.TryGetValue(s, out var order)) {
                dynamicOrder.Add(s, order = new(StringComparer.Ordinal));
                if (s == 0 && parameters != null) foreach (var p in parameters) if (!order.ContainsKey(p)) order.Add(p, order.Count);
            }
            return order;
        }
        if (!promotedFunction) Order(0);
        for (int i = 0; i < code.Length; i++) {
            var op = code[i]; string? name = NameOf(body, op);
            if (name == null) continue;
            int current = scopeAt[i];
            if (op.Code == IrOperation.Declare) {
                if (!scopes[current].Promoted) { var order = Order(current); if (!order.ContainsKey(name)) order.Add(name, order.Count); }
                continue;
            }
            var slots = new List<int>(); int s = current;
            for (; s >= 0 && scopes[s].Promoted; s = scopes[s].Parent)
                if (scopes[s].Slots.TryGetValue(name, out int slot)) slots.Add(slot);
            WasmBindingSlots.Candidate? dynamic = null; int depth = 0;
            for (; s >= 0; s = scopes[s].Parent, depth++) {
                if (Order(s).TryGetValue(name, out int slot)) { dynamic = new(slot, depth); break; }
            }
            accesses[i] = new(slots.ToArray(), dynamic, true);
        }

        // 4. Static depths and the type/declaration dataflow.
        int n = code.Length;
        var depthAt = Enumerable.Repeat(-1, n + 1).ToArray();
        var loopsAt = new int[]?[n + 1];
        var stackTags = new int[]?[n + 1];
        var outTags = new int[]?[n + 1];
        var slotDecl = new byte[]?[n + 1];
        var slotTags = new int[]?[n + 1];
        var loopBase = Enumerable.Repeat(-1, n + 1).ToArray();
        var work = new Queue<int>();
        void Flow(int pc, int depth, int[] loops, int[] tags, byte[] decl, int[] stags) {
            if (pc >= 0 && pc < n && code[pc].Code == IrOperation.LeaveLoop && loops.Length != 0) {
                depth = loops[^1]; tags = tags[..depth];
            }
            if (pc < 0 || pc > n) throw new CompilationException("Invalid compiler branch destination.");
            if (depthAt[pc] < 0) {
                depthAt[pc] = depth; loopsAt[pc] = loops; stackTags[pc] = (int[])tags.Clone(); slotDecl[pc] = (byte[])decl.Clone(); slotTags[pc] = (int[])stags.Clone();
                work.Enqueue(pc); return;
            }
            if (depthAt[pc] != depth || !loopsAt[pc]!.SequenceEqual(loops)) throw new CompilationException($"Inconsistent operand stack at instruction {pc}.");
            bool changed = false;
            var oldTags = stackTags[pc]!; var oldDecl = slotDecl[pc]!; var oldSlot = slotTags[pc]!;
            for (int k = 0; k < oldTags.Length; k++) if (oldTags[k] != tags[k] && oldTags[k] != Unknown) { oldTags[k] = Unknown; changed = true; }
            for (int k = 0; k < oldDecl.Length; k++) {
                if (oldDecl[k] != decl[k] && oldDecl[k] != Maybe) { oldDecl[k] = Maybe; changed = true; }
                if (oldSlot[k] != stags[k] && oldSlot[k] != Unknown) { oldSlot[k] = Unknown; changed = true; }
            }
            if (changed) work.Enqueue(pc);
        }
        var initialDecl = new byte[locals]; var initialSlot = Enumerable.Repeat(Unknown, locals).ToArray();
        if (promotedFunction) for (int p = 0; p < parameters!.Length; p++) initialDecl[p] = Yes;
        Flow(0, 0, System.Array.Empty<int>(), System.Array.Empty<int>(), initialDecl, initialSlot);
        static int Numeric(int a, int b) => a is >= Int and <= Double && b is >= Int and <= Double ? Math.Max(a, b) : Unknown;
        while (work.TryDequeue(out int pc)) {
            if (pc == n) continue;
            int depth = depthAt[pc]; var loops = loopsAt[pc]!;
            var tags = stackTags[pc]!.ToList(); var decl = (byte[])slotDecl[pc]!.Clone(); var stags = (int[])slotTags[pc]!.Clone();
            var op = code[pc]; int operand = op.Operand;
            int Top(int k = 1) => tags[^k];
            void Pop(int count) { if (tags.Count < count) throw new CompilationException($"Operand stack underflow at instruction {pc}."); tags.RemoveRange(tags.Count - count, count); }
            void Write(Access access, int tag, bool keep) {
                // The first declared candidate receives the value at run time.
                foreach (int slot in access.Slots) {
                    int value = keep ? stags[slot] : tag;
                    if (decl[slot] == Yes) { stags[slot] = value; return; }
                    if (decl[slot] == Maybe) stags[slot] = stags[slot] == value ? value : Unknown;
                }
            }
            int Read(Access access) {
                foreach (int slot in access.Slots) {
                    if (decl[slot] == Yes) return stags[slot];
                    if (decl[slot] == Maybe) return Unknown;
                }
                return Unknown;
            }
            void ResetScope(int s, int? keep) {
                foreach (int slot in scopes[s].SlotList) if (slot != keep) { decl[slot] = No; stags[slot] = Unknown; }
            }
            int[] nextLoops = loops; bool fallsThrough = true; var targets = new List<int>();
            switch (op.Code) {
                case IrOperation.Constant:
                    tags.Add(body.Pool[operand] switch {
                        IntegerObj => Int, FloatObj => Float, DoubleObj => Double, BooleanObj => Bool, StringObj => String,
                        NullObj => Null, BreakObj => Break, _ => Unknown });
                    break;
                case IrOperation.Void: tags.Add(0); break;
                case IrOperation.Null: tags.Add(Null); break;
                case IrOperation.Closure: tags.Add(Closure); break;
                case IrOperation.Load: tags.Add(Read(accesses[pc]!)); break;
                case IrOperation.Duplicate: if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}."); tags.Add(Top()); break;
                case IrOperation.Pop: Pop(1); break;
                case IrOperation.Declare:
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    if (declareSlot[pc] >= 0) { decl[declareSlot[pc]] = Yes; stags[declareSlot[pc]] = Top(); }
                    tags[^1] = 0; break;
                case IrOperation.Assign: {
                    if (tags.Count < 2) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    Write(accesses[pc]!, Top(), false); Pop(2); tags.Add(0); break;
                }
                case IrOperation.CompoundAssign: {
                    if (tags.Count < 2) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    // Successful compound assignment preserves the left operand's type.
                    Write(accesses[pc]!, 0, true); Pop(2); tags.Add(0); break;
                }
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: {
                    int tag = Read(accesses[pc]!); Write(accesses[pc]!, 0, true);
                    tags.Add(tag is >= Int and <= Double ? tag : Unknown); break;
                }
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: case IrOperation.Modulo: {
                    if (tags.Count < 2) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    int a = Top(2), b = Top(); Pop(2);
                    tags.Add(op.Code == IrOperation.Add && a == String && b == String ? String : Numeric(a, b)); break;
                }
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                    Pop(2); tags.Add(Bool); break;
                case IrOperation.Negate: { if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}."); int a = Top(); tags[^1] = a is >= Int and <= Double ? a : Unknown; break; }
                case IrOperation.Not: if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}."); tags[^1] = Bool; break;
                case IrOperation.CheckHashKey: case IrOperation.CheckArrayWrite:
                    if (tags.Count < (op.Code == IrOperation.CheckArrayWrite ? 2 : 1)) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    break;
                case IrOperation.CheckType:
                    // A passed annotation check proves the value's tag (arrays: the array tag).
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    tags[^1] = (operand & 16) != 0 ? Array : operand & 15; break;
                case IrOperation.Member: case IrOperation.MemberFunction:
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}."); tags[^1] = Unknown; break;
                case IrOperation.Index: case IrOperation.IncrementIndex: Pop(2); tags.Add(Unknown); break;
                case IrOperation.IncrementMember: if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}."); tags[^1] = Unknown; break;
                case IrOperation.StructType: tags.Add(Unknown); break;
                case IrOperation.CheckStruct:
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    tags[^1] = Unknown; break;
                case IrOperation.SetMember: { if (tags.Count < 2) throw new CompilationException($"Operand stack underflow at instruction {pc}."); int v = Top(); Pop(2); tags.Add(v); break; }
                case IrOperation.CompoundMember: Pop(2); tags.Add(0); break;
                case IrOperation.CompoundIndex: { if (tags.Count < 3) throw new CompilationException($"Operand stack underflow at instruction {pc}."); Pop(3); tags.Add(0); break; }
                case IrOperation.CallNamed: Pop(body.NamedArguments(operand) + 1); tags.Add(Unknown); break;
                case IrOperation.WriteIndex: { if (tags.Count < 3) throw new CompilationException($"Operand stack underflow at instruction {pc}."); int v = Top(); Pop(3); tags.Add(v); break; }
                case IrOperation.Call: Pop(operand + 1); tags.Add(Unknown); break;
                case IrOperation.ResolveMemberFunction: Pop(1); tags.Add(Unknown); break;
                case IrOperation.Array: Pop(operand); tags.Add(Array); break;
                case IrOperation.Hash: Pop(checked(operand * 2)); tags.Add(Hash); break;
                case IrOperation.EnterLoop: {
                    nextLoops = loops.Append(depth).ToArray();
                    int child = scopeAt[pc + 1];
                    if (scopes[child].Promoted) ResetScope(child, null);
                    break;
                }
                case IrOperation.NextIteration: {
                    int s = scopeAt[pc];
                    if (scopes[s].Promoted) {
                        int? keep = scopes[s].LoopName is string loopName ? scopes[s].Slots[loopName] : null;
                        if (keep is int k && decl[k] != Yes) { rejection = "Promoted loop binding is not definitely declared."; return null; }
                        ResetScope(s, keep);
                    }
                    break;
                }
                case IrOperation.LeaveLoop: {
                    if (loops.Length == 0) throw new CompilationException("Invalid compiler loop scope.");
                    int s = scopeAt[pc]; if (scopes[s].Promoted) ResetScope(s, null);
                    tags = tags.Take(loops[^1]).ToList(); tags.Add(0); nextLoops = loops[..^1]; break;
                }
                case IrOperation.Break:
                    if (loops.Length == 0) throw new CompilationException("Invalid compiler loop exit.");
                    tags = tags.Take(loops[^1]).ToList(); fallsThrough = false; targets.Add(operand); break;
                case IrOperation.Jump: fallsThrough = false; targets.Add(operand); break;
                case IrOperation.JumpIfFalse: case IrOperation.LoopCondition: Pop(1); targets.Add(operand); break;
                case IrOperation.JumpIfBreak:
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    if (Top() == Unknown || Top() == Break) targets.Add(operand);
                    if (Top() == Break) fallsThrough = false;
                    break;
                case IrOperation.Return:
                    if (tags.Count < 1) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
                    continue;
                case IrOperation.Error: continue;
                default: throw new CompilationException("Unknown compiler operation.");
            }
            var after = tags.ToArray(); outTags[pc] = after;
            foreach (int target in targets) Flow(target, after.Length, nextLoops, after, (byte[])decl.Clone(), (int[])stags.Clone());
            if (fallsThrough) Flow(pc + 1, after.Length, nextLoops, after, decl, stags);
        }
        for (int i = 0; i <= n; i++) if (loopsAt[i] is { Length: > 0 } l) loopBase[i] = l[^1];
        var registerSlots = RegisterCandidates(body, locals, promotedFunction ? parameters!.Length : 0, accesses, declareSlot, depthAt, stackTags, slotDecl, slotTags);
        return new WasmScopePlan(body, scopes.ToArray(), scopeAt, locals, promotedFunction, depthAt, loopBase,
            stackTags, outTags, slotDecl, slotTags, accesses, declareSlot, slotScope.ToArray()) { RegisterSlots = registerSlots };
    }

    private static Dictionary<int, int> RegisterCandidates(LoweredProgram body, int locals, int parameters, Access?[] accesses, int[] declareSlot,
        int[] depthAt, int[]?[] stackTags, byte[]?[] slotDecl, int[]?[] slotTags)
    {
        var tag = new int[locals]; var eligible = new bool[locals];
        for (int s = parameters; s < locals; s++) { eligible[s] = true; tag[s] = Unknown; }
        void Write(int slot, int value) {
            if (!eligible[slot]) return;
            if (value is not (Int or Float or Double or Bool) || tag[slot] != Unknown && tag[slot] != value) { eligible[slot] = false; return; }
            tag[slot] = value;
        }
        for (int i = 0; i < body.Code.Length; i++) {
            if (depthAt[i] < 0) continue;
            var op = body.Code[i]; int d = depthAt[i]; var decl = slotDecl[i]!; var tags = stackTags[i]!;
            if (op.Code == IrOperation.Declare) { if (declareSlot[i] >= 0) Write(declareSlot[i], tags[d - 1]); continue; }
            if (accesses[i] is not { } access) continue;
            // The access resolves to a slot only if the first possibly declared candidate is definite.
            int chosen = -1; bool uncertain = false;
            foreach (int slot in access.Slots) {
                if (uncertain) { eligible[slot] = false; continue; }
                if (decl[slot] == Yes) { chosen = slot; break; }
                if (decl[slot] == Maybe) { uncertain = true; eligible[slot] = false; }
            }
            if (chosen < 0) continue;
            switch (op.Code) {
                case IrOperation.Assign: Write(chosen, tags[d - 1]); break;
                case IrOperation.CompoundAssign: case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    Write(chosen, slotTags[i]![chosen]); break;
            }
        }
        // Keep the most frequently accessed eligible slots.
        var uses = new int[locals];
        for (int i = 0; i < body.Code.Length; i++) {
            if (declareSlot[i] >= 0) uses[declareSlot[i]]++;
            if (accesses[i] is { } access) foreach (int slot in access.Slots) uses[slot]++;
        }
        return Enumerable.Range(0, locals).Where(s => eligible[s] && tag[s] != Unknown)
            .OrderByDescending(s => uses[s]).ThenBy(s => s).Take(MaxRegisterSlots).ToDictionary(s => s, s => tag[s]);
    }
}
