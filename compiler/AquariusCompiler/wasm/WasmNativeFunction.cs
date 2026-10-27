using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;
using static AquariusLang.Wasm.RuntimeLayout;
using static AquariusLang.Wasm.WasmEncoding;

namespace AquariusLang.Wasm;

/// <summary>
/// The native tier of a fully typed function (every parameter and the result annotated). Its variables are the promoted
/// generic body's frame slots, kept in typed Wasm locals; outer typed bindings it uses are passed by address; calls to
/// other native functions of the same scope are direct Wasm calls; loops become Wasm loops. Integer, float and double
/// arithmetic follow the generic runtime exactly.
///
/// Execution stays observable like the generic body's: fuel is consumed at the entry and at loop heads with the generic
/// checkpoint weights, and when the budget runs out, a collection is pending or the native recursion depth bound is
/// reached, the native activations deoptimize into generic frames of the same bodies (variables in their slots, operands
/// on the frame stack) and the scheduler resumes at that exact block. A language error (integer division by zero) is
/// raised directly with the generic message. A native function returns only its typed result; the module's signal
/// global stays 0 on a normal return, holds 5 after an error and the outermost generic frame after a deoptimization,
/// and every caller tests it after a call. The entry adapter checks every outer binding (tag, closure identity,
/// unshadowed core conversions) and the argument types before any effect, and otherwise leaves the call to the generic
/// body (status 0).
/// </summary>
internal sealed class WasmNativeFunction
{
    private const int Unknown = -1, VoidType = 0, IntType = 1, FloatType = 2, DoubleType = 3, BoolType = 4, NullType = 11;
    private const int CallableBase = 1000, BuiltinBase = 2000, MixedType = 999;
    private const int ConvertFirst = 46;

    internal int Body { get; }
    internal NativeSignature Signature { get; }
    private readonly LoweredProgram body;
    private readonly WasmScopePlan plan;
    /// <summary>Outer bindings passed by address (own and every direct callee's), in parameter order.</summary>
    internal string[] Pointers { get; private set; } = Array.Empty<string>();
    /// <summary>Core conversions the body (or a callee) calls by an unshadowed name, with their builtin id.</summary>
    internal SortedDictionary<string, int> Guards { get; } = new(StringComparer.Ordinal);
    /// <summary>The expected tag of each own pointer: a scalar type code, or the closure tag with the callee's body.</summary>
    internal readonly SortedDictionary<string, (int Tag, int Callee)> OwnPointers = new(StringComparer.Ordinal);
    /// <summary>Names that must hold a given body's closure but are not passed: recursive calls use the activation's
    /// own closure (own and every direct callee's).</summary>
    internal SortedDictionary<string, int> ClosureGuards { get; } = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Written = new(StringComparer.Ordinal);
    internal readonly HashSet<int> Callees = new();
    /// <summary>Why the analysis rejected the body (for AQUARIUS_WASM_TIER_DIAGNOSTICS).</summary>
    internal string? Rejection { get; private set; }

    private readonly int[] slotTypes;
    private readonly Dictionary<int, int[]> states = new();
    private readonly List<string> callNames = new();
    private int capacity = 1;

    private WasmNativeFunction(int bodyIndex, NativeSignature signature, LoweredProgram body, WasmScopePlan plan)
    {
        Body = bodyIndex; Signature = signature; this.body = body; this.plan = plan;
        slotTypes = Enumerable.Repeat(Unknown, plan.Locals).ToArray();
        for (int p = 0; p < signature.Parameters.Length; p++) slotTypes[p] = signature.Parameters[p];
    }

    private static bool Numeric(int type) => type is IntType or FloatType or DoubleType;
    private static bool Wide(int type) => type is FloatType or DoubleType;
    /// <summary>Types a native local holds: numbers, Booleans and typed arrays (an Array* in an i32).</summary>
    private static bool Value(int type) => type is IntType or FloatType or DoubleType or BoolType || IsArray(type);
    private static bool IsArray(int type) => (type & 16) != 0 && (type & 15) is >= IntType and <= BoolType;
    private const int LengthBuiltin = 1;
    private static bool Widens(int from, int to) => from == to || from is IntType or FloatType && to is FloatType or DoubleType && from < to;
    private static int BuiltinOf(string name) => name switch {
        "int" or "整數" => ConvertFirst, "float" or "浮點數" => ConvertFirst + 1, "double" or "雙精度" => ConvertFirst + 2,
        "len" or "長度" => LengthBuiltin, _ => -1 };

    // ---- selection ----------------------------------------------------------------------------------------------

    /// <summary>The fully typed bodies the native tier accepts. A function calls another directly only when both are
    /// declared in the same scope, so their outer names resolve to the same bindings.</summary>
    internal static List<WasmNativeFunction> Select(IReadOnlyList<LoweredProgram> bodies, IReadOnlyList<FunctionCode?> codes, IReadOnlyList<WasmScopePlan> plans,
        Action<int, string>? rejected = null)
    {
        var bodyOf = new Dictionary<AquariusLang.ast.FunctionLiteral, int>(ReferenceEqualityComparer.Instance);
        for (int f = 0; f < codes.Count; f++) if (codes[f] is { } code) bodyOf[code.Literal] = f;
        var candidates = new HashSet<int>(Enumerable.Range(0, bodies.Count).Where(f => codes[f]?.Native != null && plans[f].PromotedFunction));
        Dictionary<int, WasmNativeFunction> accepted;
        while (true) {
            accepted = new();
            foreach (int f in candidates) {
                var native = new WasmNativeFunction(f, codes[f]!.Native!, bodies[f], plans[f]);
                if (native.Analyze(literal => bodyOf.TryGetValue(literal, out int g) && candidates.Contains(g)
                        && codes[g]!.Native!.DefiningScope == codes[f]!.Native!.DefiningScope ? g : -1))
                    accepted.Add(f, native);
                else rejected?.Invoke(f, native.Rejection ?? "its control flow does not nest");
            }
            if (accepted.Count == candidates.Count) break;
            candidates = accepted.Keys.ToHashSet();
        }
        // Pointers and guards are transitive over direct calls.
        bool changed = true;
        var pointers = accepted.Values.ToDictionary(n => n, n => n.OwnPointers.Keys.ToHashSet(StringComparer.Ordinal));
        while (changed) {
            changed = false;
            foreach (var native in accepted.Values)
                foreach (int callee in native.Callees) {
                    var target = accepted[callee];
                    foreach (string name in pointers[target]) changed |= pointers[native].Add(name);
                    foreach (var guard in target.Guards) changed |= native.Guards.TryAdd(guard.Key, guard.Value);
                    foreach (var guard in target.ClosureGuards) changed |= native.ClosureGuards.TryAdd(guard.Key, guard.Value);
                }
        }
        foreach (var native in accepted.Values) native.Pointers = pointers[native].OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return accepted.Values.OrderBy(n => n.Body).ToList();
    }

    // ---- analysis -----------------------------------------------------------------------------------------------

    /// <summary>The definite slot an access resolves to at pc, -2 when it may be declared or not (rejected), or -1 for
    /// an outer name.</summary>
    private int SlotAt(int pc, WasmScopePlan.Access access)
    {
        var declared = plan.SlotDecl[pc]!;
        foreach (int slot in access.Slots) {
            if (declared[slot] == WasmScopePlan.Yes) return slot;
            if (declared[slot] == WasmScopePlan.Maybe) return -2;
        }
        return -1;
    }

    private string NameOf(int constant) => body.Pool[constant] is Assignment a ? a.Name : (string)body.Pool[constant];

    /// <summary>The type of an outer name used as a value or call target, recording what the adapter must check.</summary>
    private int Outer(string name, Func<AquariusLang.ast.FunctionLiteral, int> nativeBody)
    {
        if (Signature.Outer.TryGetValue(name, out var outer)) {
            if (!outer.Sibling) return Unknown;
            if (outer.Function != null && !outer.Assigned) {
                int callee = nativeBody(outer.Function);
                if (callee < 0) return Unknown;
                if (callee == Body) ClosureGuards[name] = callee; else OwnPointers[name] = (ClosureTag, callee);
                Callees.Add(callee);
                int index = callNames.IndexOf(name); if (index < 0) { index = callNames.Count; callNames.Add(name); }
                return CallableBase + index;
            }
            if (!Value(outer.Type)) return Unknown;
            OwnPointers[name] = (outer.Type, -1);
            return outer.Type;
        }
        int builtin = BuiltinOf(name);
        if (builtin < 0) return Unknown;
        Guards[name] = builtin;
        return BuiltinBase + builtin;
    }

    private bool Analyze(Func<AquariusLang.ast.FunctionLiteral, int> nativeBody)
    {
        // Slot types are fixed per variable: iterate until the dataflow no longer assigns a new one.
        for (int round = 0; round < 8; round++) {
            var before = (int[])slotTypes.Clone();
            if (!Flow(nativeBody)) return false;
            if (before.SequenceEqual(slotTypes)) return Structure();
        }
        return false;
    }

    private List<(int Start, int End, bool Loop)>? constructs;

    /// <summary>Wasm blocks and loops for the reachable jumps: a backward jump closes a loop from its target; a forward
    /// jump needs a block ending at its target, opened before its first source, before every loop it leaves and
    /// around every block it would otherwise overlap (the arms of a conditional chain share the block of its end).
    /// Rejects control flow that does not nest.</summary>
    private bool Structure()
    {
        var code = body.Code; int n = code.Length;
        var loops = new List<(int Head, int End)>();
        var forward = new Dictionary<int, int>(); // target -> block start
        for (int pc = 0; pc < n; pc++) {
            if (!states.ContainsKey(pc)) continue;
            var op = code[pc];
            if (op.Code is IrOperation.Jump && op.Operand <= pc) { loops.Add((op.Operand, pc + 1)); continue; }
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.LoopCondition or IrOperation.Break)
                forward[op.Operand] = Math.Min(forward.GetValueOrDefault(op.Operand, pc), pc);
        }
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (int target in forward.Keys.ToArray()) {
                int start = forward[target];
                foreach (var loop in loops)
                    if (loop.Head < start && start < loop.End && target >= loop.End) start = loop.Head;
                foreach (var (other, otherStart) in forward)
                    if (otherStart < start && start < other && other < target) start = otherStart;
                if (start != forward[target]) { forward[target] = start; changed = true; }
            }
        }
        constructs = loops.Select(l => (l.Head, l.End, true)).Concat(forward.Select(f => (f.Value, f.Key, false))).ToList();
        foreach (var a in constructs) foreach (var b in constructs)
            if (a.Start < b.Start && b.Start < a.End && a.End < b.End) return false;
        return true;
    }

    private bool Flow(Func<AquariusLang.ast.FunctionLiteral, int> nativeBody)
    {
        var code = body.Code; int n = code.Length;
        states.Clear(); Callees.Clear(); OwnPointers.Clear(); Guards.Clear(); ClosureGuards.Clear(); Written.Clear(); callNames.Clear();
        var work = new Queue<(int Pc, int[] Stack)>(); work.Enqueue((0, Array.Empty<int>()));
        int pc = 0;
        bool Reject(int at) { Rejection = at < code.Length ? $"{code[at].Code} at instruction {at}" : "the result"; return false; }
        bool returns = false;
        while (work.TryDequeue(out var state)) {
            pc = state.Pc;
            if (states.TryGetValue(pc, out var old)) {
                if (old.SequenceEqual(state.Stack)) continue;
                // Operands whose types differ between the paths (a conditional's result that is only popped) merge
                // into a value no instruction may use; the merged state is propagated again.
                if (old.Length != state.Stack.Length) return Reject(pc);
                var merged = old.Zip(state.Stack, (a, b) => a == b ? a : MixedType).ToArray();
                if (merged.SequenceEqual(old)) continue;
                states[pc] = merged; state = (pc, merged);
            } else states.Add(pc, state.Stack); capacity = Math.Max(capacity, state.Stack.Length + 1);
            if (pc == n) { if (state.Stack.Length == 0 || state.Stack[^1] != Signature.Result) return Reject(pc); returns = true; continue; }
            if (plan.Depth[pc] < 0) return Reject(pc);
            var stack = state.Stack.ToList(); var op = code[pc]; int operand = op.Operand;
            int Top(int k = 1) => stack.Count >= k ? stack[^k] : Unknown;
            void Pop(int k) => stack.RemoveRange(stack.Count - k, k);
            bool Store(int slot, int type) {
                if (slot < 0 || !Value(type)) return Reject(pc);
                if (slotTypes[slot] == Unknown) slotTypes[slot] = type;
                return slotTypes[slot] == type;
            }
            // The type of an assignment target: a slot's, or an outer typed binding's (recorded as written).
            int Target(WasmScopePlan.Access access, string name) {
                int slot = SlotAt(pc, access);
                if (slot >= 0) return slotTypes[slot];
                if (slot == -2) return Unknown;
                int type = Outer(name, nativeBody);
                if (!Value(type)) return Unknown;
                Written.Add(name); return type;
            }
            switch (op.Code) {
                case IrOperation.Constant:
                    int constant = body.Pool[operand] switch {
                        IntegerObj => IntType, FloatObj => FloatType, DoubleObj => DoubleType, BooleanObj => BoolType, NullObj => NullType, _ => Unknown };
                    if (constant == Unknown) return Reject(pc);
                    stack.Add(constant); break;
                case IrOperation.Void: stack.Add(VoidType); break;
                case IrOperation.Null: stack.Add(NullType); break;
                case IrOperation.Pop: if (stack.Count == 0) return Reject(pc); Pop(1); break;
                case IrOperation.Load: {
                    int slot = SlotAt(pc, plan.Accesses[pc]!);
                    if (slot == -2) return Reject(pc);
                    int type = slot >= 0 ? slotTypes[slot] : Outer(NameOf(operand), nativeBody);
                    if (type == Unknown) return Reject(pc);
                    stack.Add(type); break;
                }
                case IrOperation.Declare:
                    if (!Store(plan.DeclareSlot[pc], Top())) return Reject(pc);
                    stack[^1] = VoidType; break;
                case IrOperation.Assign: {
                    if (stack.Count < 2) return Reject(pc);
                    int slot = SlotAt(pc, plan.Accesses[pc]!);
                    if (slot >= 0) { if (!Store(slot, Top())) return Reject(pc); }
                    else if (Target(plan.Accesses[pc]!, NameOf(operand)) != Top()) return Reject(pc);
                    Pop(2); stack.Add(VoidType); break;
                }
                case IrOperation.CompoundAssign: {
                    if (stack.Count < 2 || !Numeric(Top()) || !Numeric(Target(plan.Accesses[pc]!, NameOf(operand)))) return Reject(pc);
                    Pop(2); stack.Add(VoidType); break;
                }
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: {
                    int type = Target(plan.Accesses[pc]!, NameOf(operand));
                    if (!Numeric(type)) return Reject(pc);
                    stack.Add(type); break;
                }
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: case IrOperation.Modulo:
                    if (!Numeric(Top()) || !Numeric(Top(2))) return Reject(pc);
                    { int type = Math.Max(Top(), Top(2)); Pop(2); stack.Add(type); }
                    break;
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                    if (!Numeric(Top()) || !Numeric(Top(2))) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.Equal: case IrOperation.NotEqual:
                    if (!(Numeric(Top()) && Numeric(Top(2)) || Top() == BoolType && Top(2) == BoolType)) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.And: case IrOperation.Or:
                    if (Top() != BoolType || Top(2) != BoolType) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.Negate: if (!Numeric(Top())) return Reject(pc); break;
                case IrOperation.Not: if (Top() != BoolType) return Reject(pc); break;
                case IrOperation.CheckType: {
                    int want = operand & 31;
                    if (IsArray(want) ? Top() != want : !Widens(Top(), want)) return Reject(pc);
                    stack[^1] = want; break;
                }
                case IrOperation.Index:
                    if (!IsArray(Top(2)) || Top() != IntType) return Reject(pc);
                    { int element = Top(2) & 15; Pop(2); stack.Add(element); }
                    break;
                case IrOperation.CheckArrayWrite:
                    if (!IsArray(Top(2)) || Top() != IntType) return Reject(pc);
                    break;
                case IrOperation.WriteIndex:
                    // An element of the array's type only; the generic write reports any other value.
                    if (stack.Count < 3 || !IsArray(Top(3)) || Top(2) != IntType || Top() != (Top(3) & 15)) return Reject(pc);
                    { int value = Top(); Pop(3); stack.Add(value); }
                    break;
                case IrOperation.Jump:
                    work.Enqueue((operand, stack.ToArray())); continue;
                case IrOperation.JumpIfFalse: case IrOperation.LoopCondition:
                    if (Top() != BoolType) return Reject(pc);
                    Pop(1); work.Enqueue((operand, stack.ToArray())); break;
                case IrOperation.JumpIfBreak: break; // Typed values are never a break value.
                case IrOperation.Break: {
                    int base_ = plan.LoopBase[pc];
                    if (base_ < 0 || base_ > stack.Count) return Reject(pc);
                    work.Enqueue((operand, stack.Take(base_).ToArray())); continue;
                }
                case IrOperation.EnterLoop: case IrOperation.NextIteration: break;
                case IrOperation.LeaveLoop: stack.Add(VoidType); break;
                case IrOperation.Call: {
                    int count = operand;
                    if (stack.Count < count + 1) return Reject(pc);
                    int callee = stack[stack.Count - count - 1];
                    if (stack.Contains(MixedType)) return Reject(pc);
                    var arguments = stack.Skip(stack.Count - count).ToArray();
                    int result;
                    if (callee == BuiltinBase + LengthBuiltin) {
                        if (count != 1 || !IsArray(arguments[0])) return Reject(pc);
                        result = IntType;
                    } else if (callee >= BuiltinBase) {
                        if (count != 1 || !Numeric(arguments[0])) return Reject(pc);
                        result = callee - BuiltinBase - ConvertFirst + IntType;
                    } else if (callee >= CallableBase) {
                        var target = Signature.Outer[callNames[callee - CallableBase]].Function!;
                        var parameters = target.Parameters ?? Array.Empty<AquariusLang.ast.Identifier>();
                        if (parameters.Length != count) return Reject(pc);
                        for (int i = 0; i < count; i++) if (!Widens(arguments[i], parameters[i].Type!.Code)) return Reject(pc);
                        result = target.ReturnType!.Code;
                    } else return Reject(pc);
                    Pop(count + 1); stack.Add(result); break;
                }
                case IrOperation.Return:
                    if (Top() != Signature.Result) return Reject(pc);
                    returns = true; continue;
                default: return Reject(pc); // Closures, heap objects, members, strings and errors stay generic.
            }
            work.Enqueue((pc + 1, stack.ToArray()));
        }
        // A loop head is a deoptimization site: its operands must all be materializable.
        foreach (var (head, operands) in states)
            if (operands.Contains(MixedType) && code.Take(n).Select((op, at) => (op, at)).Any(x => x.op.Code == IrOperation.Jump && x.op.Operand == head && head <= x.at)) {
                Rejection = $"loop head {head} has operands of mixed types ({string.Join(",", operands)})"; return false;
            }
        return returns;
    }

    // ---- emission -----------------------------------------------------------------------------------------------

    private static byte WasmType(int type) => Wide(type) ? (byte)0x7c : (byte)0x7f;

    internal void WriteType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 2 + Pointers.Length + Signature.Parameters.Length);
        for (int i = 0; i < 2 + Pointers.Length; i++) w.Write((byte)0x7f);
        foreach (int type in Signature.Parameters) w.Write(WasmType(type));
        U(w, 1); w.Write(WasmType(Signature.Result));
    }

    /// <summary>The resume implementation: (depth, closure, pointers..., site, frame) -> result.</summary>
    internal void WriteResumeType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 4 + Pointers.Length);
        for (int i = 0; i < 4 + Pointers.Length; i++) w.Write((byte)0x7f);
        U(w, 1); w.Write(WasmType(Signature.Result));
    }

    /// <summary>The loop heads a deoptimized activation can resume at natively; site 1 is the entry, site 2 + i the i-th
    /// loop head.</summary>
    private int[] LoopHeads => constructs!.Where(c => c.Loop).Select(c => c.Start).OrderBy(pc => pc).ToArray();
    private int SiteOf(int pc) => pc == 0 ? 1 : 2 + Array.IndexOf(LoopHeads, pc);

    /// <summary>The entry adapter, (args, out, execution) -> status, called by rt_call through the function's numeric
    /// table slot; or, with <paramref name="resume"/>, the resume adapter, (frame, out, execution) -> status, called by
    /// native_resume through the next slot for a deoptimized frame marked with a native site.</summary>
    internal void EmitAdapter(BinaryWriter w, int implementation, IReadOnlyDictionary<string, int> runtime, Func<string, int> text,
        IReadOnlyDictionary<int, WasmNativeFunction> natives, int signal, int execution, int? resume = null)
    {
        // Locals after (args or frame 0, out 1, execution 2): closure 3, scope 4, pointer k at 5 + k, then status, i, f.
        int closure = 3, scope = 4;
        int Pointer(int k) => 5 + k;
        int status = 5 + Pointers.Length, resultI = status + 1, scratch = status + 2, resultF = status + 3;
        U(w, 2); U(w, Pointers.Length + 5); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7c);
        // A changed binding sends this call (0) or this frame from now on (6) to the generic body.
        void Bail() { I(w, resume.HasValue ? 6 : 0); w.Write((byte)0x0f); }
        void Unless(Action condition) { condition(); w.Write(new byte[] { 0x45, 0x04, 0x40 }); Bail(); w.Write((byte)0x0b); }
        if (resume.HasValue) {
            // Resume only with budget left and no collection pending; otherwise the generic body yields first.
            Get(w, 2); Memory(w, I32Load, 2, ExecutionBudget); w.Write((byte)0x45);
            Get(w, 2); Memory(w, I32Load, 2, ExecutionPoll); w.Write(new byte[] { 0x72, 0x04, 0x40 }); I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
            Get(w, 0); Memory(w, I32Load, 2, FrameNativeClosure); Set(w, closure);
        } else { Get(w, 2); Memory(w, I32Load, 2, ExecutionNumericClosure); Set(w, closure); }
        Get(w, closure); Memory(w, I32Load, 2, ClosureScope); Set(w, scope);
        for (int k = 0; k < Pointers.Length; k++) {
            string name = Pointers[k];
            Get(w, scope); I(w, text(name)); w.Write(Call); U(w, runtime["aqua_scope_get"]); Set(w, Pointer(k));
            Unless(() => Get(w, Pointer(k)));
            var expected = natives.Values.First(n => n.OwnPointers.ContainsKey(name)).OwnPointers[name];
            Unless(() => { Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueTag); I(w, IsArray(expected.Tag) ? ArrayTag : expected.Tag); w.Write((byte)0x46); });
            // A typed array binding must hold an array locked to that element type.
            if (IsArray(expected.Tag))
                Unless(() => { Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, expected.Tag & 15); w.Write((byte)0x46); });
            if (expected.Callee >= 0) {
                // The callee's binding must still hold the closure of that body from this program.
                Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueRef); Set(w, scratch);
                Unless(() => {
                    Get(w, scratch); Memory(w, I32Load, 2, ClosureFunction); I(w, expected.Callee); w.Write((byte)0x46);
                    Get(w, scratch); Memory(w, I32Load, 2, ClosureProgram); Get(w, closure); Memory(w, I32Load, 2, ClosureProgram); w.Write(new byte[] { 0x46, 0x71 });
                });
            }
        }
        foreach (var (name, callee) in ClosureGuards) {
            Get(w, scope); I(w, text(name)); w.Write(Call); U(w, runtime["aqua_scope_get"]); Set(w, scratch);
            Unless(() => Get(w, scratch));
            Unless(() => { Get(w, scratch); Memory(w, I32Load, 2, ValueTag); I(w, ClosureTag); w.Write((byte)0x46); });
            Get(w, scratch); Memory(w, I32Load, 2, ValueRef); Set(w, scratch);
            Unless(() => {
                Get(w, scratch); Memory(w, I32Load, 2, ClosureFunction); I(w, callee); w.Write((byte)0x46);
                Get(w, scratch); Memory(w, I32Load, 2, ClosureProgram); Get(w, closure); Memory(w, I32Load, 2, ClosureProgram); w.Write(new byte[] { 0x46, 0x71 });
            });
        }
        foreach (var (name, id) in Guards) {
            Get(w, scope); I(w, text(name)); w.Write(Call); U(w, runtime["aqua_scope_get"]); Set(w, scratch);
            Unless(() => Get(w, scratch));
            Unless(() => {
                Get(w, scratch); Memory(w, I32Load, 2, ValueTag); I(w, BuiltinTag); w.Write((byte)0x46);
                Get(w, scratch); Memory(w, I32Load, 2, ValueRef); I(w, id); w.Write(new byte[] { 0x46, 0x71 });
            });
        }
        // Arguments (in the frame's parameter slots when resuming at the entry): the exact type, or an integer for
        // float/double and a float for double (widened like CheckType).
        int argumentBase = resume.HasValue ? FrameValues : 0;
        if (!resume.HasValue)
            for (int p = 0; p < Signature.Parameters.Length; p++) {
                int type = Signature.Parameters[p], offset = p * ValueSize;
                Unless(() => {
                    Get(w, 0); Memory(w, I32Load, 2, offset + ValueTag); Set(w, scratch);
                    if (IsArray(type)) {
                        // Only an array an annotation check already locked to this element type.
                        Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46);
                        Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46); w.Write(new byte[] { 0x04, 0x7f });
                        Get(w, 0); Memory(w, I32Load, 2, offset + ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, type & 15); w.Write((byte)0x46);
                        w.Write((byte)0x05); I(w, 0); w.Write(new byte[] { 0x0b, 0x71 });
                    }
                    else if (type is IntType or BoolType) { Get(w, scratch); I(w, type); w.Write((byte)0x46); }
                    else { Get(w, scratch); I(w, IntType); w.Write((byte)0x4f); Get(w, scratch); I(w, type); w.Write(new byte[] { 0x4d, 0x71 }); }
                });
            }
        Get(w, 2); w.Write((byte)0x24); U(w, execution);
        void CallEntry() {
            I(w, 0); Get(w, closure);
            for (int k = 0; k < Pointers.Length; k++) Get(w, Pointer(k));
            for (int p = 0; p < Signature.Parameters.Length; p++) {
                int type = Signature.Parameters[p];
                if (IsArray(type)) { Get(w, 0); Memory(w, I32Load, 2, argumentBase + p * ValueSize + ValueRef); continue; }
                Get(w, 0); Memory(w, F64Load, 3, argumentBase + p * ValueSize + ValueNumber);
                if (type is IntType or BoolType) w.Write(new byte[] { 0xfc, 0x02 });
                else if (type == FloatType) w.Write(new byte[] { 0xb6, 0xbb });
            }
            w.Write(Call); U(w, implementation);
        }
        if (resume.HasValue) {
            Get(w, 0); Memory(w, I32Load, 2, FrameNativeSite); I(w, 1); w.Write(new byte[] { 0x46, 0x04, WasmType(Signature.Result) });
            CallEntry();
            w.Write((byte)0x05);
            I(w, 0); Get(w, closure);
            for (int k = 0; k < Pointers.Length; k++) Get(w, Pointer(k));
            Get(w, 0); Memory(w, I32Load, 2, FrameNativeSite); Get(w, 0);
            w.Write(Call); U(w, resume.Value);
            w.Write((byte)0x0b);
        } else CallEntry();
        if (Wide(Signature.Result)) Set(w, resultF); else Set(w, resultI);
        w.Write((byte)0x23); U(w, signal); Set(w, status); I(w, 0); w.Write((byte)0x24); U(w, signal);
        // Signal 0: a result; 5: a raised error; otherwise the outermost generic frame of a deoptimization.
        Get(w, status); w.Write(new byte[] { 0x45, 0x04, 0x40 });
        Get(w, 1); I(w, IsArray(Signature.Result) ? ArrayTag : Signature.Result); Memory(w, I32Store, 2, ValueTag);
        Get(w, 1); if (IsArray(Signature.Result)) Get(w, resultI); else I(w, 0); Memory(w, I32Store, 2, ValueRef);
        Get(w, 1);
        if (Signature.Result == IntType) { Get(w, resultI); w.Write((byte)0xb7); }
        else if (Signature.Result == BoolType) { Get(w, resultI); w.Write((byte)0xb8); }
        else if (IsArray(Signature.Result)) F(w, 0);
        else Get(w, resultF);
        Memory(w, F64Store, 3, ValueNumber);
        I(w, 1); w.Write((byte)0x0f);
        w.Write((byte)0x0b);
        Get(w, status); I(w, 5); w.Write(new byte[] { 0x46, 0x04, 0x40 }); I(w, 5); w.Write(new byte[] { 0x0f, 0x0b });
        Get(w, 1); Get(w, status); Memory(w, I32Store, 2, ValueRef);
        I(w, 4); w.Write((byte)0x0b);
    }

    internal void EmitImplementation(BinaryWriter w, IReadOnlyDictionary<int, (int Function, WasmNativeFunction Native)> natives,
        IReadOnlyDictionary<string, int> runtime, IReadOnlySet<string> cacheable, int signal, int execution, bool resume = false)
    {
        var code = body.Code; int n = code.Length;
        var fuel = WasmFuel.Checkpoints(body);
        var starts = WasmBodyEmitter.BlockStarts(body, plan);
        int BlockOf(int pc) { int b = Array.BinarySearch(starts, pc); if (b < 0) throw new InvalidOperationException("Native deoptimization target is not a generic block."); return b; }

        // ---- locals --------------------------------------------------------------------------------------------
        // Parameters: recursion depth 0, own closure 1, pointers, then the arguments, or (resuming) the site and the
        // deoptimized frame. The execution is a global the adapter sets.
        int pointerBase = 2, argumentBase = 2 + Pointers.Length, first = argumentBase + (resume ? 2 : Signature.Parameters.Length);
        int siteParameter = argumentBase, frameParameter = argumentBase + 1;
        void Execution() { Op(0x23); U(w, execution); }
        int slots = plan.Locals, cap = capacity;
        var cached = Pointers.Where(p => OwnPointers.TryGetValue(p, out var e) && e.Callee < 0 && cacheable.Contains(p)).ToArray();
        int i32Count = slots * 2 + cap + 8 + cached.Length, f64Count = slots + cap + 3 + cached.Length;
        int SlotI(int s) => first + s;
        int Flag(int s) => first + slots + s;
        int StackI(int k) => first + slots * 2 + k;
        int status = first + slots * 2 + cap, resultI = status + 1, frame = status + 2, tempI = status + 3, tempJ = status + 4;
        // The execution budget lives in a local between the entry checkpoint and every exit or call: nothing else
        // changes it during native code, and loop-head checkpoints then cost no memory access.
        int budget = status + 5, site = status + 6, resumeAt = status + 7;
        int CacheI(int c) => status + 8 + c;
        // The local may go negative where the generic checkpoint saturates at 0: both yield at the next checkpoint, and
        // the stored budget is clamped to the generic value.
        void SaveBudget() { Execution(); Get(w, budget); I(w, 0); Get(w, budget); I(w, 0); Op(0x4a, 0x1b); Memory(w, I32Store, 2, ExecutionBudget); }
        void LoadBudget() { Execution(); Memory(w, I32Load, 2, ExecutionBudget); Set(w, budget); }
        int f64First = first + i32Count;
        int SlotF(int s) => f64First + s;
        int StackF(int k) => f64First + slots + k;
        int resultF = f64First + slots + cap, tempF = resultF + 1, tempG = resultF + 2;
        int CacheF(int c) => resultF + 3 + c;
        U(w, 2); U(w, i32Count); w.Write((byte)0x7f); U(w, f64Count); w.Write((byte)0x7c);
        int PointerOf(string name) => pointerBase + Array.IndexOf(Pointers, name);
        int CacheOf(string name) => Array.IndexOf(cached, name);

        void Op(params byte[] bytes) => w.Write(bytes);
        void Local(int type, int i, int f) => Get(w, Wide(type) ? f : i);
        void SetLocal(int type, int i, int f) => Set(w, Wide(type) ? f : i);
        void PushStack(int type, int k) => Local(type, StackI(k), StackF(k));
        void PopStack(int type, int k) => SetLocal(type, StackI(k), StackF(k));
        void AsDouble(int type, int k) { PushStack(type, k); if (!Wide(type)) Op(0xb7); }
        void RoundFloat() => Op(0xb6, 0xbb);
        // A typed value as the f64 payload of a generic Value.
        void AsPayload(int type) { if (type == IntType) Op(0xb7); else if (type == BoolType) Op(0xb8); }
        // A generic Value at base + offset as a typed local value: an array's reference, or the converted payload.
        void LoadPayload(Action pushBase, int offset, int type) {
            pushBase();
            if (IsArray(type)) { Memory(w, I32Load, 2, offset + ValueRef); return; }
            Memory(w, F64Load, 3, offset + ValueNumber); if (!Wide(type)) Op(0xfc, 0x02);
        }

        // Outer typed bindings: a cached local when no native code writes them, else the binding's payload.
        void ReadOuter(string name, int type) {
            int c = CacheOf(name);
            if (c >= 0) { Local(type, CacheI(c), CacheF(c)); return; }
            LoadPayload(() => Get(w, PointerOf(name)), 0, type);
        }
        void WriteOuterFrom(string name, int type, Action value) {
            Get(w, PointerOf(name)); value();
            if (IsArray(type)) Memory(w, I32Store, 2, ValueRef); else { AsPayload(type); Memory(w, F64Store, 3, ValueNumber); }
        }

        void Zero() { if (Wide(Signature.Result)) F(w, 0); else I(w, 0); }
        void Raise(Action value) { value(); Op(0x24); U(w, signal); Zero(); Op(Return); }
        void ExitWith(int type, int k) { SaveBudget(); PushStack(type, k); Op(Return); }
        // Errors and deoptimizations leave the body through one handler after it, so loops contain no runtime calls
        // (which would make every value live across them spill).
        var sites = new List<(int Kind, int Block, int[] Operands, int Message)>();
        const int ErrorSite = 0, CheckpointSite = 1, CallSite = 2;
        void ToHandler(int kind, int block, int[] operands, int message) {
            sites.Add((kind, block, operands, message)); I(w, sites.Count - 1); Set(w, site);
            Op(Br); U(w, Label(c => c.End == int.MaxValue) + 1); // Every site sits in one if.
        }
        void Fail(int message) => ToHandler(ErrorSite, 0, Array.Empty<int>(), message);

        // Deoptimization: a generic frame resuming at block of pc with the given operands, declared slots written.
        void StoreValue(int offset, int type, Action payload) {
            if (IsArray(type)) {
                Get(w, frame); I(w, ArrayTag); Memory(w, I32Store, 2, offset + ValueTag);
                Get(w, frame); payload(); Memory(w, I32Store, 2, offset + ValueRef);
                Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                return;
            }
            Get(w, frame); I(w, type == NullType ? NullTag : type); Memory(w, I32Store, 2, offset + ValueTag);
            Get(w, frame); I(w, 0); Memory(w, I32Store, 2, offset + ValueRef);
            Get(w, frame); payload(); Memory(w, F64Store, 3, offset + ValueNumber);
        }
        void Materialize(int block, int[] operands) {
            Execution(); Get(w, 1); I(w, block); I(w, operands.Length); Op(Call); U(w, runtime["rt_nativeFrame"]); Set(w, frame);
            for (int s = 0; s < slots; s++) {
                int type = slotTypes[s];
                if (type == Unknown) continue;
                Get(w, Flag(s)); Op(If, 0x40);
                StoreValue(FrameValues + ValueSize * s, type, () => { Local(type, SlotI(s), SlotF(s)); if (!IsArray(type)) AsPayload(type); });
                Op(End);
            }
            for (int k = 0; k < operands.Length; k++) {
                int type = operands[k], offset = FrameValues + ValueSize * (plan.Locals + k);
                if (type >= BuiltinBase) {
                    Get(w, frame); I(w, BuiltinTag); Memory(w, I32Store, 2, offset + ValueTag);
                    Get(w, frame); I(w, type - BuiltinBase); Memory(w, I32Store, 2, offset + ValueRef);
                    Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                } else if (type >= CallableBase) {
                    string name = callNames[type - CallableBase];
                    if (Array.IndexOf(Pointers, name) < 0) {
                        // A recursive call's callee is this activation's own closure.
                        Get(w, frame); I(w, ClosureTag); Memory(w, I32Store, 2, offset + ValueTag);
                        Get(w, frame); Get(w, 1); Memory(w, I32Store, 2, offset + ValueRef);
                        Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                    } else {
                        int pointer = PointerOf(name);
                        for (int o = 0; o <= 8; o += 8) { Get(w, frame); Get(w, pointer); Memory(w, I64Load, 3, o); Memory(w, I64Store, 3, offset + o); }
                    }
                } else if (Value(type)) StoreValue(offset, type, () => { PushStack(type, k); if (!IsArray(type)) AsPayload(type); });
                else StoreValue(offset, type, () => F(w, 0));
            }
        }

        int depthLimit = Math.Clamp(262144 / (32 + 8 * (slots + cap + Pointers.Length)), 64, 8192);
        // The generic checkpoint: yield (here: deoptimize) when the budget is exhausted, a collection is pending or, at
        // the entry, the native depth bound is reached; otherwise consume the weight with saturation. A pending
        // collection is armed only by allocation, which native code never does, so loops need not poll.
        void Checkpoint(int pc, bool entry) {
            if (!fuel.TryGetValue(pc, out int weight)) return;
            if (entry) {
                LoadBudget();
                Get(w, 0); I(w, depthLimit); Op(0x4f);
                Get(w, budget); Op(0x45, 0x72);
                Execution(); Memory(w, I32Load, 2, ExecutionPoll); Op(0x72);
            } else { Get(w, budget); I(w, 0); Op(0x4c); }
            Op(If, 0x40); ToHandler(CheckpointSite, BlockOf(pc), states[pc], pc); Op(End);
            Get(w, budget); I(w, weight); Op(0x6b); Set(w, budget);
        }

        // ---- structured control flow -------------------------------------------------------------------------------
        var constructs = this.constructs!;
        // The outermost block is the handler's entry.
        var open = new List<(int Start, int End, bool Loop)> { (-1, int.MaxValue, false) };
        int Label(Func<(int Start, int End, bool Loop), bool> match) {
            for (int d = open.Count - 1; d >= 0; d--) if (match(open[d])) return open.Count - 1 - d;
            throw new InvalidOperationException("Native branch target is not open.");
        }

        // ---- resuming: skip blocks lead from the top through every enclosing construct to the resumed loop head ------
        var loopHeads = LoopHeads;
        var skips = new List<(int Owner, int End, int[] Sites)>(); // Owner: index into constructs, or -1 for the top.
        if (resume)
            foreach (int head in loopHeads) {
                var path = constructs.Select((c, i) => (c, i)).Where(p => p.c.Start <= head && head < p.c.End)
                    .OrderBy(p => p.c.Start).ThenByDescending(p => p.c.End).ThenBy(p => p.c.Loop).ToList();
                for (int k = 0; k < path.Count; k++) {
                    int owner = k == 0 ? -1 : path[k - 1].i, next = path[k].c.Start;
                    // Nothing to skip between two constructs opened at the same pc unless the outer one is a loop (its checkpoint).
                    if (k > 0 && path[k - 1].c.Start == next && !path[k - 1].c.Loop) continue;
                    int existing = skips.FindIndex(x => x.Owner == owner && x.End == next);
                    if (existing < 0) skips.Add((owner, next, new[] { SiteOf(head) }));
                    else skips[existing] = (owner, next, skips[existing].Sites.Append(SiteOf(head)).ToArray());
                }
            }
        void OpenSkips(int owner) {
            foreach (var skip in skips.Where(x => x.Owner == owner).OrderByDescending(x => x.End)) {
                open.Add((owner, skip.End, false)); Op(Block, 0x40);
                for (int k = 0; k < skip.Sites.Length; k++) { Get(w, resumeAt); I(w, skip.Sites[k]); Op(0x46); if (k > 0) Op(0x72); }
                Op(0x0d); U(w, 0);
            }
        }

        // ---- body -----------------------------------------------------------------------------------------------
        if (resume) {
            // The deoptimized frame: declared slots (an undeclared slot keeps its flag clear), then the operands below
            // the resumed loop head.
            for (int s = 0; s < slots; s++) {
                int type = slotTypes[s];
                if (type == Unknown) { I(w, 0); Set(w, Flag(s)); continue; }
                Get(w, frameParameter); Memory(w, I32Load, 2, FrameValues + ValueSize * s + ValueTag); I(w, -1); Op(0x47); Set(w, Flag(s));
                LoadPayload(() => Get(w, frameParameter), FrameValues + ValueSize * s, type);
                SetLocal(type, SlotI(s), SlotF(s));
            }
            foreach (int head in loopHeads) {
                var operands = states[head];
                if (!operands.Any(Value)) continue;
                Get(w, siteParameter); I(w, SiteOf(head)); Op(0x46, If, 0x40);
                for (int k = 0; k < operands.Length; k++) {
                    if (!Value(operands[k])) continue;
                    LoadPayload(() => Get(w, frameParameter), FrameValues + ValueSize * (plan.Locals + k), operands[k]);
                    PopStack(operands[k], k);
                }
                Op(End);
            }
            Get(w, siteParameter); Set(w, resumeAt);
        } else {
            for (int s = 0; s < slots; s++) { I(w, s < Signature.Parameters.Length ? 1 : 0); Set(w, Flag(s)); }
            for (int p = 0; p < Signature.Parameters.Length; p++) { Get(w, argumentBase + p); SetLocal(slotTypes[p], SlotI(p), SlotF(p)); }
        }
        Op(Block, 0x40);
        if (resume) LoadBudget(); else Checkpoint(0, true);
        for (int c = 0; c < cached.Length; c++) { string name = cached[c]; int type = OwnPointers[name].Tag;
            LoadPayload(() => Get(w, PointerOf(name)), 0, type); SetLocal(type, CacheI(c), CacheF(c)); }
        OpenSkips(-1);

        for (int pc = 0; pc <= n; pc++) {
            while (open.Count > 1 && open[^1].End == pc) { open.RemoveAt(open.Count - 1); Op(End); }
            foreach (var c in constructs.Where(c => c.Start == pc).OrderByDescending(c => c.End).ThenBy(c => c.Loop)) {
                if (c.End > open[^1].End) throw new InvalidOperationException("Native control flow is not structured.");
                open.Add(c); Op(c.Loop ? Loop : Block, 0x40);
                if (resume && c.Loop) { Get(w, resumeAt); I(w, SiteOf(pc)); Op(0x46, If, 0x40); I(w, 0); Set(w, resumeAt); Op(End); }
                OpenSkips(constructs.IndexOf(c));
                if (c.Loop) Checkpoint(pc, false);
            }
            if (!states.TryGetValue(pc, out var stack)) continue;
            int depth = stack.Length, top = depth - 1, below = depth - 2;
            if (pc == n) { ExitWith(stack[top], top); break; }
            var op = code[pc]; int operand = op.Operand;
            int TopType(int k = 1) => stack[depth - k];
            switch (op.Code) {
                case IrOperation.Constant:
                    switch (body.Pool[operand]) {
                        case IntegerObj v: I(w, v.Value); Set(w, StackI(depth)); break;
                        case BooleanObj v: I(w, v.Value ? 1 : 0); Set(w, StackI(depth)); break;
                        case FloatObj v: F(w, v.Value); Set(w, StackF(depth)); break;
                        case DoubleObj v: F(w, v.Value); Set(w, StackF(depth)); break;
                    }
                    break;
                case IrOperation.Void: case IrOperation.Null: case IrOperation.Pop: case IrOperation.JumpIfBreak:
                    break;
                case IrOperation.Load: {
                    int slot = SlotAt(pc, plan.Accesses[pc]!), type = states[pc + 1][depth];
                    if (slot >= 0) { Local(type, SlotI(slot), SlotF(slot)); PopStack(type, depth); }
                    else if (Value(type)) { ReadOuter(NameOf(operand), type); PopStack(type, depth); }
                    break; // A callee occupies a compile-time-only operand.
                }
                case IrOperation.Declare: {
                    int slot = plan.DeclareSlot[pc], type = TopType();
                    PushStack(type, top); SetLocal(type, SlotI(slot), SlotF(slot)); I(w, 1); Set(w, Flag(slot)); break;
                }
                case IrOperation.Assign: {
                    int slot = SlotAt(pc, plan.Accesses[pc]!), type = TopType();
                    if (slot >= 0) { PushStack(type, top); SetLocal(type, SlotI(slot), SlotF(slot)); }
                    else {
                        string name = NameOf(operand);
                        WriteOuterFrom(name, type, () => PushStack(type, top));
                    }
                    break;
                }
                case IrOperation.CompoundAssign: {
                    var assignment = (Assignment)body.Pool[operand];
                    int slot = SlotAt(pc, plan.Accesses[pc]!), right = TopType();
                    string name = assignment.Name;
                    int left = slot >= 0 ? slotTypes[slot] : OwnPointers[name].Tag;
                    void LoadLeft() { if (slot >= 0) Local(left, SlotI(slot), SlotF(slot)); else ReadOuter(name, left); }
                    void StoreLeft(Action value) {
                        if (slot >= 0) { value(); SetLocal(left, SlotI(slot), SlotF(slot)); }
                        else WriteOuterFrom(name, left, value);
                    }
                    var operation = assignment.Operation;
                    if (left == IntType && right == IntType) {
                        IntCheck(operation, () => PushStack(IntType, top));
                        StoreLeft(() => IntOperation(operation, LoadLeft, () => PushStack(IntType, top)));
                    } else {
                        if (left == IntType && operation == IrOperation.Modulo) {
                            // Integer remainder by zero is an error even with a float or double right operand.
                            AsDouble(right, top); F(w, 0); Op(0x61, If, 0x40); Fail(1); Op(End);
                        }
                        LoadLeft(); if (!Wide(left)) Op(0xb7); Set(w, tempF);
                        AsDouble(right, top); Set(w, tempG);
                        FloatOperation(operation, tempF, tempG);
                        Set(w, tempF);
                        StoreLeft(() => { Get(w, tempF); if (left == IntType) Op(0xfc, 0x02); else if (left == FloatType) RoundFloat(); });
                    }
                    break;
                }
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: {
                    int slot = SlotAt(pc, plan.Accesses[pc]!), type = states[pc + 1][depth];
                    string name = NameOf(operand);
                    void LoadIt() { if (slot >= 0) Local(type, SlotI(slot), SlotF(slot)); else ReadOuter(name, type); }
                    void Next() { LoadIt(); if (type == IntType) { I(w, 1); Op(0x6a); } else { F(w, 1); Op(0xa0); if (type == FloatType) RoundFloat(); } }
                    if (op.Code == IrOperation.IncrementPostfix) { LoadIt(); PopStack(type, depth); }
                    if (slot >= 0) { Next(); SetLocal(type, SlotI(slot), SlotF(slot)); } else WriteOuterFrom(name, type, Next);
                    if (op.Code == IrOperation.IncrementPrefix) { LoadIt(); PopStack(type, depth); }
                    break;
                }
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: case IrOperation.Modulo: {
                    int left = TopType(2), right = TopType(), type = Math.Max(left, right);
                    if (type == IntType) {
                        IntCheck(op.Code, () => PushStack(IntType, top));
                        IntOperation(op.Code, () => PushStack(IntType, below), () => PushStack(IntType, top)); Set(w, StackI(below));
                    } else {
                        AsDouble(left, below); Set(w, tempF); AsDouble(right, top); Set(w, tempG);
                        FloatOperation(op.Code, tempF, tempG);
                        if (type == FloatType) RoundFloat();
                        Set(w, StackF(below));
                    }
                    break;
                }
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: {
                    int left = TopType(2), right = TopType();
                    if (left is IntType or BoolType && right is IntType or BoolType) {
                        PushStack(left, below); PushStack(right, top);
                        Op(op.Code switch { IrOperation.Less => (byte)0x48, IrOperation.Greater => (byte)0x4a, IrOperation.LessEqual => (byte)0x4c,
                            IrOperation.GreaterEqual => (byte)0x4e, IrOperation.Equal => (byte)0x46, _ => (byte)0x47 });
                    } else {
                        AsDouble(left, below); AsDouble(right, top);
                        Op(op.Code switch { IrOperation.Less => (byte)0x63, IrOperation.Greater => (byte)0x64, IrOperation.LessEqual => (byte)0x65,
                            IrOperation.GreaterEqual => (byte)0x66, IrOperation.Equal => (byte)0x61, _ => (byte)0x62 });
                    }
                    Set(w, StackI(below)); break;
                }
                case IrOperation.And: case IrOperation.Or:
                    Get(w, StackI(below)); Get(w, StackI(top)); Op(op.Code == IrOperation.And ? (byte)0x71 : (byte)0x72); Set(w, StackI(below)); break;
                case IrOperation.Negate:
                    if (TopType() == IntType) { I(w, 0); Get(w, StackI(top)); Op(0x6b); Set(w, StackI(top)); }
                    else { Get(w, StackF(top)); Op(0x9a); Set(w, StackF(top)); }
                    break;
                case IrOperation.Not: Get(w, StackI(top)); Op(0x45); Set(w, StackI(top)); break;
                case IrOperation.Index: {
                    // Typed reads are checked: an index outside the array is an error, not null.
                    Get(w, StackI(top)); Get(w, StackI(below)); Memory(w, I32Load, 2, ArrayCount); Op(0x49, 0x45, If, 0x40); Fail(2); Op(End);
                    int element = TopType(2) & 15;
                    Get(w, StackI(below)); Memory(w, I32Load, 2, ArrayValues); Get(w, StackI(top)); I(w, 4); Op(0x74, 0x6a);
                    Memory(w, F64Load, 3, ValueNumber); if (!Wide(element)) Op(0xfc, 0x02);
                    PopStack(element, below); break;
                }
                case IrOperation.CheckArrayWrite:
                    Get(w, StackI(top)); Get(w, StackI(below)); Memory(w, I32Load, 2, ArrayCount); Op(0x49, 0x45, If, 0x40); Fail(3); Op(End);
                    break;
                case IrOperation.WriteIndex: {
                    int array = depth - 3, index = depth - 2, type = TopType();
                    // Shared (copy-on-write) or host-exposed storage goes through the runtime first.
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayStorage); Memory(w, I32Load, 2, ArrayStorageReferences); I(w, 1); Op(0x47);
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayExposed); Op(0x72, If, 0x40);
                    Get(w, StackI(array)); Op(Call); U(w, runtime["rt_nativeWritable"]); Op(Drop);
                    Op(End);
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayValues); Get(w, StackI(index)); I(w, 4); Op(0x74, 0x6a);
                    PushStack(type, top); AsPayload(type); Memory(w, F64Store, 3, ValueNumber);
                    PushStack(type, top); PopStack(type, array); break;
                }
                case IrOperation.CheckType: {
                    int want = operand & 31, from = TopType();
                    if (from == want) break;
                    AsDouble(from, top); if (want == FloatType) RoundFloat(); Set(w, StackF(top));
                    break;
                }
                case IrOperation.Jump:
                    Op(Br); U(w, operand <= pc ? Label(c => c.Loop && c.Start == operand) : Label(c => !c.Loop && c.End == operand));
                    break;
                case IrOperation.Break:
                    Op(Br); U(w, Label(c => !c.Loop && c.End == operand)); break;
                case IrOperation.JumpIfFalse: case IrOperation.LoopCondition:
                    Get(w, StackI(top)); Op(0x45, 0x0d); U(w, Label(c => !c.Loop && c.End == operand)); break;
                case IrOperation.EnterLoop: ClearScope(plan.ScopeAt[pc + 1], -1); break;
                case IrOperation.NextIteration: {
                    int s = plan.ScopeAt[pc]; var scope = plan.Scopes[s];
                    ClearScope(s, scope.LoopName is string loopName ? scope.Slots[loopName] : -1); break;
                }
                case IrOperation.LeaveLoop: ClearScope(plan.ScopeAt[pc], -1); break;
                case IrOperation.Call: {
                    int count = operand, at = depth - count - 1, callee = stack[at];
                    if (callee == BuiltinBase + LengthBuiltin) { Get(w, StackI(top)); Memory(w, I32Load, 2, ArrayCount); Set(w, StackI(at)); break; }
                    if (callee >= BuiltinBase) {
                        int result = callee - BuiltinBase - ConvertFirst + IntType, argument = stack[top];
                        if (result == IntType) {
                            if (argument == IntType) Get(w, StackI(top)); else { Get(w, StackF(top)); Op(0xfc, 0x02); }
                            Set(w, StackI(at));
                        } else { AsDouble(argument, top); if (result == FloatType) RoundFloat(); Set(w, StackF(at)); }
                        break;
                    }
                    string name = callNames[callee - CallableBase];
                    var (function, target) = natives[OwnPointers.TryGetValue(name, out var own) ? own.Callee : Body];
                    Get(w, 0); I(w, 1); Op(0x6a);
                    // The callee's closure: this activation's own for a recursive call, else loaded from its binding.
                    if (ClosureGuards.ContainsKey(name) && !OwnPointers.ContainsKey(name)) Get(w, 1); else { Get(w, PointerOf(name)); Memory(w, I32Load, 2, ValueRef); }
                    foreach (string pointer in target.Pointers) Get(w, PointerOf(pointer));
                    for (int a = 0; a < count; a++) {
                        int from = stack[at + 1 + a], to = target.Signature.Parameters[a];
                        if (from == to) PushStack(from, at + 1 + a);
                        else { AsDouble(from, at + 1 + a); if (to == FloatType) RoundFloat(); }
                    }
                    SaveBudget();
                    Op(Call); U(w, function); PopStack(target.Signature.Result, at);
                    LoadBudget();
                    Op(0x23); U(w, signal); Op(If, 0x40);
                    // A raised error (5) unwinds every native activation with the signal kept.
                    Op(0x23); U(w, signal); I(w, 5); Op(0x46, If, 0x40); Zero(); Op(Return); Op(End);
                    // The callee deoptimized: this activation becomes its caller's generic frame, resuming after the call.
                    ToHandler(CallSite, BlockOf(pc + 1), stack.Take(at).ToArray(), 0);
                    Op(End);
                    break;
                }
                case IrOperation.Return: ExitWith(stack[top], top); break;
                default: throw new InvalidOperationException($"Unsupported native instruction {op.Code}.");
            }
        }
        while (open.Count > 1) { open.RemoveAt(open.Count - 1); Op(End); }
        Op(0x00); // Every path returns or reaches the handler.
        Op(End);
        // The handler: one case per site, selected by the site local.
        for (int k = sites.Count - 1; k >= 0; k--) Op(Block, 0x40);
        if (sites.Count > 0) { Get(w, site); Op(BrTable); U(w, sites.Count); for (int k = 0; k < sites.Count; k++) U(w, k); U(w, sites.Count - 1); }
        for (int k = 0; k < sites.Count; k++) {
            Op(End);
            var (kind, block, operands, message) = sites[k];
            if (kind == ErrorSite) { Raise(() => { Execution(); I(w, message); Op(Call); U(w, runtime["rt_nativeFailure"]); }); continue; }
            // A checkpoint consumed nothing yet; after a call the callee already stored the budget.
            if (kind == CheckpointSite) SaveBudget();
            Materialize(block, operands);
            // A checkpoint's frame resumes natively at the same site when the scheduler runs it again.
            if (kind == CheckpointSite) { Get(w, frame); I(w, SiteOf(message)); Memory(w, I32Store, 2, FrameNativeSite); }
            if (kind == CallSite) { Op(0x23); U(w, signal); Get(w, frame); Memory(w, I32Store, 2, FrameParent); }
            Raise(() => Get(w, frame));
        }
        Zero();
        Op(End);

        void ClearScope(int scope, int keep) { foreach (int slot in plan.Scopes[scope].SlotList) if (slot != keep) { I(w, 0); Set(w, Flag(slot)); } }
        // Integer division and remainder by zero are language errors.
        void IntCheck(IrOperation operation, Action right) {
            if (operation is not (IrOperation.Divide or IrOperation.Modulo)) return;
            right(); Op(0x45, If, 0x40); Fail(operation == IrOperation.Divide ? 0 : 1); Op(End);
        }
        void IntOperation(IrOperation operation, Action left, Action right) {
            switch (operation) {
                case IrOperation.Add: left(); right(); Op(0x6a); break;
                case IrOperation.Subtract: left(); right(); Op(0x6b); break;
                case IrOperation.Multiply: left(); right(); Op(0x6c); break;
                case IrOperation.Divide:
                    // The minimum integer divided by -1 wraps to itself (i32.div_s would trap).
                    left(); Set(w, tempI); right(); Set(w, tempJ);
                    Get(w, tempI); I(w, int.MinValue); Op(0x46); Get(w, tempJ); I(w, -1); Op(0x46, 0x71, If, 0x7f);
                    I(w, int.MinValue); Op(Else); Get(w, tempI); Get(w, tempJ); Op(0x6d); Op(End); break;
                default:
                    left(); Set(w, tempI); right(); Set(w, tempJ);
                    Get(w, tempJ); I(w, -1); Op(0x46, If, 0x7f); I(w, 0); Op(Else); Get(w, tempI); Get(w, tempJ); Op(0x6f); Op(End); break;
            }
        }
        // f64 arithmetic on two locals; the remainder is x - trunc(x / y) * y like the generic runtime.
        void FloatOperation(IrOperation operation, int x, int y) {
            if (operation == IrOperation.Modulo) { Get(w, x); Get(w, x); Get(w, y); Op(0xa3, 0x9d); Get(w, y); Op(0xa2, 0xa1); return; }
            Get(w, x); Get(w, y); Op(WasmIntegerFunction.Arithmetic(operation));
        }
    }
}
