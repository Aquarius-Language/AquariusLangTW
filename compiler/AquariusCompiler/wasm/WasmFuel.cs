using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Compiler;

namespace AquariusLang.Wasm;

/// <summary>
/// Shared execution-fuel model of every emitted body. Fuel is consumed only at the body entry and at
/// targets of backward branches; every cycle passes through one of them. Each checkpoint consumes the
/// longest path, in generic basic blocks, to the next checkpoint or exit, so a quantum never runs more
/// generic blocks than before while straight-line work does not pay for a runtime check per block.
/// Optimized and unoptimized emitters use this model, so a given quantum yields at the same points.
/// </summary>
internal static class WasmFuel
{
    internal static Dictionary<int, int> Checkpoints(LoweredProgram body)
    {
        var code = body.Code; int n = code.Length;
        var leaders = new SortedSet<int> { 0, n };
        var checkpoints = new SortedSet<int> { 0 };
        for (int i = 0; i < n; i++) {
            var op = code[i];
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.JumpIfBreak or IrOperation.LoopCondition or IrOperation.Break) {
                leaders.Add(op.Operand);
                if (op.Operand <= i) checkpoints.Add(op.Operand);
            }
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.JumpIfBreak or IrOperation.LoopCondition or IrOperation.Break
                or IrOperation.Call or IrOperation.ResolveMemberFunction or IrOperation.Return) leaders.Add(i + 1);
        }
        var starts = leaders.ToArray();
        int Block(int position) => Array.BinarySearch(starts, position);
        var successors = new List<int>[starts.Length];
        for (int b = 0; b < starts.Length; b++) {
            successors[b] = new List<int>();
            if (starts[b] >= n) continue;
            int last = starts[b + 1] - 1; var op = code[last];
            switch (op.Code) {
                case IrOperation.Jump: case IrOperation.Break: successors[b].Add(Block(op.Operand)); break;
                case IrOperation.Return: break;
                case IrOperation.JumpIfFalse: case IrOperation.LoopCondition: case IrOperation.JumpIfBreak:
                    successors[b].Add(Block(op.Operand)); successors[b].Add(b + 1); break;
                default: successors[b].Add(b + 1); break;
            }
        }
        var checkpointBlocks = checkpoints.Select(Block).ToHashSet();
        // Every edge into a non-checkpoint block goes forward, so one reverse pass gives the longest paths.
        var longest = new int[starts.Length];
        for (int b = starts.Length - 1; b >= 0; b--) {
            int best = 0;
            foreach (int s in successors[b]) {
                if (s < 0 || s >= starts.Length || checkpointBlocks.Contains(s)) continue;
                if (s <= b) throw new InvalidOperationException("Fuel region is cyclic.");
                best = Math.Max(best, longest[s]);
            }
            longest[b] = Math.Min(best + 1, 1 << 20);
        }
        return checkpoints.ToDictionary(p => p, p => longest[Block(p)]);
    }
}
