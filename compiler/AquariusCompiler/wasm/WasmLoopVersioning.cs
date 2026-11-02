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
/// on every iteration: that the fuel budget lasts.
///
/// Fuel: the fast copy is taken only when the budget covers every head the loop can reach (at most the trip count plus
/// one, each charging the head's weight and any inlined callee's), so no yield can fall inside it; it skips the loop
/// head's checkpoint and charges the weight of the heads it ran when it leaves (k - k0 + 1, since k advances by one per
/// head and a break leaves before the next). The budget, and so every yield point, is exactly the original's.
/// </summary>
internal sealed partial class WasmNativeFunction
{
    /// <summary>A versioned loop: its head and end, the counter slot, the bound's instruction, the head's fuel weight and
    /// the most fuel one iteration charges.</summary>
    private sealed record VersionedLoop(int Head, int End, int Counter, int Bound, int Weight, int PerIteration);

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
        return new VersionedLoop(head, end, counter, head + 1, weight, perIteration);
    }

    /// <summary>The definite slot of the access at pc (see SlotAt), or -1 for an outer name, -2 when unknown.</summary>
    private int SlotOf(int pc) => plan.Depth[pc] >= 0 && plan.Accesses[pc] is { } access ? SlotAt(pc, access) : -2;
}
