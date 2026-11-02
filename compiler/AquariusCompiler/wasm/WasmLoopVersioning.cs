using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Wasm;

/// <summary>
/// Loop versioning in the native tier. An innermost counted loop, <c>迴圈 (變數 k = ...; k &lt; bound; k++)</c> with a
/// bound the loop does not change, whose body makes no call (only inlined leaves), allocates nothing and does not return,
/// is emitted twice: the original, and a fast copy taken when a test before the loop proves what the original checks
/// on every iteration.
///
/// Fuel: the fast copy is taken only when the budget covers every head the loop can reach (at most the trip count plus
/// one, each charging the head's weight and any inlined callee's), so no yield can fall inside it; it skips the loop
/// head's checkpoint and charges the weight of the heads it ran when it leaves (k - k0 + 1, since k advances by one per
/// head and a break leaves before the next). The budget, and so every yield point, is exactly the original's.
///
/// Bounds: an element read or write whose index is affine in k (a sum, difference or product of k, constants and
/// bindings the loop does not change, never k times k) on an array the loop does not change is checked at both ends of
/// the range before the loop, together with every intermediate value fitting a 32-bit integer (so the loop's wrapping
/// arithmetic equals the exact one). When all pass, the fast copy reads and writes those elements unchecked; otherwise
/// the original runs and raises the error where it would.
/// </summary>
internal sealed partial class WasmNativeFunction
{
    /// <summary>An index term: the counter (IncrementPostfix), an invariant int (a Constant or Load at Pc) or an operation
    /// on two terms.</summary>
    private sealed record Term(IrOperation Operation, int Pc = -1, Term? Left = null, Term? Right = null)
    {
        internal bool HasCounter => Operation == IrOperation.IncrementPostfix || (Left?.HasCounter ?? false) || (Right?.HasCounter ?? false);
    }
    /// <summary>A versioned loop: its head and end, the counter slot, the bound's instruction, the head's fuel weight, the
    /// most fuel one iteration charges, and the element accesses (Index or CheckArrayWrite pc) proven before the loop
    /// with their array's Load and index term.</summary>
    private sealed record VersionedLoop(int Head, int End, int Counter, int Bound, int Weight, int PerIteration,
        Dictionary<int, (int Array, Term Index)> Proven);

    private Dictionary<int, VersionedLoop>? versioned;

    private Dictionary<int, VersionedLoop> VersionedLoops(IReadOnlyDictionary<int, (int Function, WasmNativeFunction Native)> natives)
    {
        if (versioned != null) return versioned;
        versioned = new();
        if (IsRegion) return versioned;
        var fuel = WasmFuel.Checkpoints(body);
        foreach (var (head, end, loop) in constructs!)
            if (loop && !constructs!.Any(c => c.Loop && c.Start > head && c.Start < end)
                && Counted(head, end, natives, fuel) is { } counted) versioned[head] = counted;
        return versioned;
    }

    private VersionedLoop? Counted(int head, int end, IReadOnlyDictionary<int, (int Function, WasmNativeFunction Native)> natives, Dictionary<int, int> fuel)
    {
        var code = body.Code;
        int jump = end - 1;
        if (!fuel.TryGetValue(head, out int weight) || jump - 3 <= head + 3 || code[jump].Code != IrOperation.Jump || code[jump].Operand != head) return null;
        if (code[jump - 1].Code != IrOperation.Pop || code[jump - 2].Code is not (IrOperation.IncrementPostfix or IrOperation.IncrementPrefix)
            || code[jump - 3].Code != IrOperation.NextIteration) return null;
        int counter = SlotOf(jump - 2);
        if (counter < 0 || slotTypes[counter] != IntType || code[head].Code != IrOperation.Load || SlotOf(head) != counter
            || code[head + 2].Code != IrOperation.Less || code[head + 3].Code != IrOperation.LoopCondition || code[head + 3].Operand != end) return null;
        // Slots and outer bindings the loop writes (its update writes the counter).
        var slots = new HashSet<int>(); var names = new HashSet<string>(StringComparer.Ordinal);
        for (int pc = head; pc < end; pc++) {
            if (!states.ContainsKey(pc)) continue;
            switch (code[pc].Code) {
                case IrOperation.Declare: slots.Add(plan.DeclareSlot[pc]); break;
                case IrOperation.Assign: case IrOperation.CompoundAssign: case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    if (pc == jump - 2) break;
                    if (SlotOf(pc) is >= 0 and var slot) slots.Add(slot); else if (SlotOf(pc) == -1) names.Add(NameOf(code[pc].Operand)); else return null;
                    break;
            }
        }
        if (slots.Contains(counter)) return null;
        bool Invariant(int pc) {
            if (!states.TryGetValue(pc + 1, out var after) || after.Length <= states[pc].Length || after[states[pc].Length] != IntType) return false;
            if (code[pc].Code == IrOperation.Constant) return body.Pool[code[pc].Operand] is IntegerObj;
            if (code[pc].Code != IrOperation.Load) return false;
            int slot = SlotOf(pc);
            if (slot >= 0) return slot != counter && !slots.Contains(slot) && plan.SlotDecl[head]![slot] == WasmScopePlan.Yes;
            return slot == -1 && !names.Contains(NameOf(code[pc].Operand));
        }
        if (!Invariant(head + 1)) return null;
        // The body: nothing that calls out, allocates, deoptimizes other than with an error, or leaves the function.
        int perIteration = weight;
        for (int pc = head + 4; pc < jump - 3; pc++) {
            if (!states.TryGetValue(pc, out var stack)) continue;
            var op = code[pc];
            switch (op.Code) {
                case IrOperation.Return: case IrOperation.Array: case IrOperation.CallNamed: case IrOperation.EnterLoop: case IrOperation.LeaveLoop:
                    return null;
                case IrOperation.Add when stack[^1] == StringType: return null;
                case IrOperation.Index when stack[^2] == StringType || IsLoose(stack[^2]): return null;
                case IrOperation.Call: {
                    int callee = stack[stack.Length - op.Operand - 1];
                    if (callee >= BuiltinBase) { if (callee is BuiltinBase + MakeBuiltin or BuiltinBase + AppendBuiltin) return null; break; }
                    if (callee < CallableBase || StructCallee(callee, out _)) return null;
                    var target = natives[OwnPointers.TryGetValue(callNames[callee - CallableBase], out var own) ? own.Callee : Body].Native;
                    if (target == this || !target.Inlinable || target.Allocates || stack.Skip(stack.Length - op.Operand).Contains(AnyValueType)) return null;
                    perIteration += WasmFuel.Checkpoints(target.body)[0];
                    break;
                }
            }
        }
        // Element accesses whose index is affine in the counter, on an array the loop does not change.
        var proven = new Dictionary<int, (int Array, Term Index)>();
        (Term Term, int Start)? Parse(int at) {
            if (at <= head || !states.ContainsKey(at) || !states.TryGetValue(at + 1, out var after) || after.Length == 0 || after[^1] != IntType) return null;
            var op = code[at];
            if (op.Code == IrOperation.Load && SlotOf(at) == counter) return (new Term(IrOperation.IncrementPostfix), at);
            if (op.Code is IrOperation.Load or IrOperation.Constant) return Invariant(at) ? (new Term(op.Code, at), at) : null;
            if (op.Code is not (IrOperation.Add or IrOperation.Subtract or IrOperation.Multiply)) return null;
            if (Parse(at - 1) is not { } right || Parse(right.Start - 1) is not { } left) return null;
            if (op.Code == IrOperation.Multiply && left.Term.HasCounter && right.Term.HasCounter) return null;
            if (states[left.Start].Length != after.Length - 1) return null;
            return (new Term(op.Code, -1, left.Term, right.Term), left.Start);
        }
        for (int pc = head + 4; pc < jump - 3; pc++) {
            if (!states.TryGetValue(pc, out var stack) || code[pc].Code is not (IrOperation.Index or IrOperation.CheckArrayWrite)) continue;
            if (!IsArray(stack[^2]) || IsLoose(stack[^2]) || Parse(pc - 1) is not { } index || !index.Term.HasCounter) continue;
            int array = index.Start - 1;
            if (code[array].Code != IrOperation.Load || states[array].Length != stack.Length - 2) continue;
            int slot = SlotOf(array);
            bool fixedArray = slot >= 0 ? !slots.Contains(slot) && plan.SlotDecl[head]![slot] == WasmScopePlan.Yes
                : slot == -1 && !names.Contains(NameOf(code[array].Operand)) && !constants.ContainsKey(NameOf(code[array].Operand));
            if (fixedArray) proven[pc] = (array, index.Term);
        }
        return new VersionedLoop(head, end, counter, head + 1, weight, perIteration, proven);
    }

    /// <summary>The definite slot of the access at pc (see SlotAt), or -1 for an outer name, -2 when unknown.</summary>
    private int SlotOf(int pc) => plan.Depth[pc] >= 0 && plan.Accesses[pc] is { } access ? SlotAt(pc, access) : -2;
}
