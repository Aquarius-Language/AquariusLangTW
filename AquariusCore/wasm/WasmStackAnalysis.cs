using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Compiler;

namespace AquariusLang.Wasm;

/// <summary>Computes live operand capacity across branches, calls and loop exits.</summary>
internal static class WasmStackAnalysis
{
    internal static int Capacity(LoweredProgram program)
    {
        var visited = new Dictionary<int, (int Depth, int[] Loops)>();
        var work = new Queue<(int Pc, int Depth, int[] Loops)>();
        work.Enqueue((0, 0, Array.Empty<int>()));
        int maximum = 0;
        while (work.TryDequeue(out var state))
        {
            var (pc, depth, loops) = state;
            // Both a completed body and a false condition can enter LeaveLoop.
            // Its runtime implementation discards temporary operands first.
            if (pc >= 0 && pc < program.Code.Length && program.Code[pc].Code == IrOperation.LeaveLoop && loops.Length != 0)
                depth = loops[^1];
            if (visited.TryGetValue(pc, out var prior))
            {
                if (prior.Depth != depth || !prior.Loops.SequenceEqual(loops))
                    throw new CompilationException($"Inconsistent operand stack at instruction {pc}.");
                continue;
            }
            visited.Add(pc, (depth, loops));
            maximum = Math.Max(maximum, depth);
            if (pc == program.Code.Length) continue;
            if (pc < 0 || pc > program.Code.Length)
                throw new CompilationException("Invalid compiler branch destination.");
            var instruction = program.Code[pc];
            int required = 0, change = 0;
            switch (instruction.Code)
            {
                case IrOperation.Constant: case IrOperation.Void: case IrOperation.Null:
                case IrOperation.Load: case IrOperation.Closure:
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    change = 1; break;
                case IrOperation.Duplicate: required = 1; change = 1; break;
                case IrOperation.Pop: case IrOperation.JumpIfFalse: case IrOperation.LoopCondition:
                    required = 1; change = -1; break;
                case IrOperation.Declare: case IrOperation.Negate: case IrOperation.Not:
                case IrOperation.CheckHashKey: case IrOperation.Member: case IrOperation.MemberFunction:
                case IrOperation.ResolveMemberFunction: case IrOperation.JumpIfBreak:
                    required = 1; break;
                case IrOperation.Assign: case IrOperation.CompoundAssign:
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                case IrOperation.Index:
                    required = 2; change = -1; break;
                case IrOperation.CheckArrayWrite: required = 2; break;
                case IrOperation.WriteIndex: required = 3; change = -2; break;
                case IrOperation.Call: required = instruction.Operand + 1; change = -instruction.Operand; break;
                case IrOperation.Array: required = instruction.Operand; change = 1 - required; break;
                case IrOperation.Hash: required = checked(instruction.Operand * 2); change = 1 - required; break;
                case IrOperation.EnterLoop: loops = loops.Append(depth).ToArray(); break;
                case IrOperation.Break:
                    if (loops.Length == 0) throw new CompilationException("Invalid compiler loop exit.");
                    depth = loops[^1]; break;
                case IrOperation.LeaveLoop:
                    if (loops.Length == 0) throw new CompilationException("Invalid compiler loop scope.");
                    depth = loops[^1] + 1; loops = loops[..^1]; break;
                case IrOperation.Return: case IrOperation.Error: continue;
                case IrOperation.Jump: case IrOperation.NextIteration: break;
                default: throw new CompilationException("Unknown compiler operation.");
            }
            if (depth < required) throw new CompilationException($"Operand stack underflow at instruction {pc}.");
            depth += change;
            maximum = Math.Max(maximum, depth);
            if (instruction.Code is IrOperation.Jump or IrOperation.Break)
                work.Enqueue((instruction.Operand, depth, loops));
            else
            {
                work.Enqueue((pc + 1, depth, loops));
                if (instruction.Code is IrOperation.JumpIfFalse or IrOperation.LoopCondition or IrOperation.JumpIfBreak)
                    work.Enqueue((instruction.Operand, depth, loops));
            }
        }
        return Math.Max(16, maximum);
    }
}
