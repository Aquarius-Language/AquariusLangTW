using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Wasm;

/// <summary>A proven integer specialization of a closed scalar function. All
/// arguments and returns are integers; intermediate booleans stay in i32 locals.</summary>
internal sealed class WasmIntegerFunction
{
    private readonly WasmNumericFunction plan;
    private readonly Dictionary<string, int> variables;
    private WasmIntegerFunction(WasmNumericFunction plan)
    {
        this.plan = plan;
        variables = plan.Variables.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
    }

    internal static WasmIntegerFunction? Analyze(WasmNumericFunction plan)
    {
        var candidate = new WasmIntegerFunction(plan);
        int[] initial = Enumerable.Repeat(-1, plan.Variables.Length).ToArray();
        foreach (string name in plan.Parameters) initial[candidate.variables[name]] = 1;
        var states = new Dictionary<int, (int[] Stack, int[] Variables)>();
        var work = new Queue<(int Pc, int[] Stack, int[] Variables)>();
        work.Enqueue((0, Array.Empty<int>(), initial));
        bool returns = false;
        while (work.TryDequeue(out var state)) {
            if (states.TryGetValue(state.Pc, out var old)) {
                if (!old.Stack.SequenceEqual(state.Stack) || !old.Variables.SequenceEqual(state.Variables)) return null;
                continue;
            }
            states.Add(state.Pc, (state.Stack, state.Variables));
            var stack = state.Stack.ToList(); var locals = (int[])state.Variables.Clone();
            if (state.Pc == plan.Body.Code.Length) {
                if (stack.Count == 0 || stack[^1] != 1) return null;
                returns = true; continue;
            }
            var op = plan.Body.Code[state.Pc]; int n = op.Operand;
            bool Top(int tag) => stack.Count > 0 && stack[^1] == tag;
            bool Pair(int tag) => stack.Count > 1 && stack[^1] == tag && stack[^2] == tag;
            void Binary(int tag) { stack.RemoveAt(stack.Count - 1); stack[^1] = tag; }
            switch (op.Code) {
                case IrOperation.Constant:
                    if (plan.Body.Pool[n] is IntegerObj) stack.Add(1);
                    else if (plan.Body.Pool[n] is BooleanObj) stack.Add(4);
                    else if (plan.Body.Pool[n] is NullObj) stack.Add(11);
                    else return null;
                    break;
                case IrOperation.Void: stack.Add(0); break;
                case IrOperation.Null: stack.Add(11); break;
                case IrOperation.Load:
                    if (!candidate.variables.TryGetValue((string)plan.Body.Pool[n], out int variable)) stack.Add(8);
                    else { if (locals[variable] < 0) return null; stack.Add(locals[variable]); }
                    break;
                case IrOperation.Declare:
                    locals[candidate.variables[(string)plan.Body.Pool[n]]] = stack[^1]; stack[^1] = 0; break;
                case IrOperation.Assign:
                    locals[candidate.variables[(string)plan.Body.Pool[n]]] = stack[^1]; Binary(0); break;
                case IrOperation.CompoundAssign:
                    var assignment = (Assignment)plan.Body.Pool[n];
                    if (!Pair(1) || locals[candidate.variables[assignment.Name]] != 1) return null;
                    if (assignment.Operation is not (IrOperation.Add or IrOperation.Subtract or IrOperation.Multiply or IrOperation.Divide)) return null;
                    Binary(0); break;
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    if (locals[candidate.variables[(string)plan.Body.Pool[n]]] != 1) return null;
                    stack.Add(1); break;
                case IrOperation.Duplicate: stack.Add(stack[^1]); break;
                case IrOperation.Pop: stack.RemoveAt(stack.Count - 1); break;
                case IrOperation.Negate: if (!Top(1)) return null; break;
                case IrOperation.Not: if (!Top(4)) return null; break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                    if (!Pair(1)) return null; Binary(1); break;
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                    if (!Pair(1)) return null; Binary(4); break;
                case IrOperation.Equal: case IrOperation.NotEqual:
                    if (!Pair(1) && !Pair(4)) return null; Binary(4); break;
                case IrOperation.And: case IrOperation.Or:
                    if (!Pair(4)) return null; Binary(4); break;
                case IrOperation.JumpIfFalse:
                    if (!Top(4)) return null; stack.RemoveAt(stack.Count - 1); break;
                case IrOperation.Jump: case IrOperation.JumpIfBreak: break;
                case IrOperation.Call:
                    if (stack.Skip(stack.Count - n).Any(t => t != 1)) return null;
                    stack.RemoveRange(stack.Count - n - 1, n + 1); stack.Add(1); break;
                case IrOperation.Return:
                    if (!Top(1)) return null; returns = true; continue;
                default: return null;
            }
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse)
                work.Enqueue((n, stack.ToArray(), locals));
            if (op.Code != IrOperation.Jump) work.Enqueue((state.Pc + 1, stack.ToArray(), locals));
        }
        // Recursive returns are sound by induction: every terminating base return
        // is integer and every allowed operation preserves its proven type.
        return returns ? candidate : null;
    }

    internal void WriteType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 2 + plan.Parameters.Length);
        for (int i = 0; i < 2 + plan.Parameters.Length; i++) w.Write((byte)0x7f);
        U(w, 2); w.Write(new byte[] { 0x7f, 0x7f }); // status, integer result
    }

    internal void EmitImplementation(BinaryWriter w, int implementation)
    {
        int first = 2 + plan.Parameters.Length;
        int Variable(int v) => first + v;
        int Slot(int s) => first + plan.Variables.Length + s;
        int status = Slot(plan.Capacity), result = status + 1, wide = status + 2;
        U(w, 2); U(w, plan.Variables.Length + plan.Capacity + 2); w.Write((byte)0x7f);
        U(w, 1); w.Write((byte)0x7e);
        void Fail(int code) { I(w, code); I(w, 0); w.Write((byte)0x0f); }
        void Copy(int to, int from) { Get(w, from); Set(w, to); }
        void CheckedWide(int to) {
            Set(w, wide);
            Get(w, wide); L(w, int.MinValue); w.Write((byte)0x59);
            Get(w, wide); L(w, int.MaxValue); w.Write(new byte[] { 0x57, 0x71, 0x04, 0x7f });
            Get(w, wide); w.Write(new byte[] { 0xa7, 0x05 }); I(w, int.MinValue); w.Write((byte)0x0b); Set(w, to);
        }
        void Binary(IrOperation op, int left, int right) {
            if (op is IrOperation.Add or IrOperation.Subtract or IrOperation.Multiply) {
                Get(w, left); w.Write((byte)0xac); Get(w, right); w.Write((byte)0xac);
                w.Write(op == IrOperation.Add ? (byte)0x7c : op == IrOperation.Subtract ? (byte)0x7d : (byte)0x7e);
                CheckedWide(left); return;
            }
            if (op == IrOperation.Divide) {
                Get(w, right); w.Write((byte)0x45);
                Get(w, left); I(w, int.MinValue); w.Write((byte)0x46);
                Get(w, right); I(w, -1); w.Write(new byte[] { 0x46, 0x71, 0x72, 0x04, 0x7f });
                I(w, int.MinValue); w.Write((byte)0x05);
                Get(w, left); Get(w, right); w.Write(new byte[] { 0x6d, 0x0b }); Set(w, left); return;
            }
            Get(w, left); Get(w, right);
            w.Write(op switch {
                IrOperation.Less => (byte)0x48, IrOperation.Greater => (byte)0x4a,
                IrOperation.LessEqual => (byte)0x4c, IrOperation.GreaterEqual => (byte)0x4e,
                IrOperation.Equal => (byte)0x46, IrOperation.NotEqual => (byte)0x47,
                IrOperation.And => (byte)0x71, IrOperation.Or => (byte)0x72,
                _ => throw new InvalidOperationException("Invalid integer operation.") });
            Set(w, left);
        }
        void Fuel() {
            Get(w, 0); Memory(w, 0x28, 2, 24); w.Write(new byte[] { 0x45, 0x04, 0x40 }); Fail(2); w.Write((byte)0x0b);
            Get(w, 0); Get(w, 0); Memory(w, 0x28, 2, 24); I(w, 1); w.Write((byte)0x6b); Memory(w, 0x36, 2, 24);
        }
        int depthLimit = Math.Min(64, Math.Max(2, 1024 / (plan.Variables.Length + plan.Capacity + plan.Parameters.Length + 8)));
        Get(w, 1); I(w, depthLimit); w.Write(new byte[] { 0x4f, 0x04, 0x40 }); Fail(3); w.Write((byte)0x0b);
        for (int p = 0; p < plan.Parameters.Length; p++) Copy(Variable(variables[plan.Parameters[p]]), 2 + p);
        for (int i = plan.Body.Code.Length; i > 0; i--) w.Write(new byte[] { 0x02, 0x40 });
        var leaders = new HashSet<int> { 0 };
        for (int i = 0; i < plan.Body.Code.Length; i++) {
            var op = plan.Body.Code[i];
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse) { leaders.Add(op.Operand); leaders.Add(i + 1); }
            if (op.Code is IrOperation.Call or IrOperation.Return) leaders.Add(i + 1);
        }
        for (int i = 0; i < plan.Body.Code.Length; i++) {
            if (i > 0) w.Write((byte)0x0b);
            if (plan.Depths[i] < 0) continue;
            if (leaders.Contains(i)) Fuel();
            var op = plan.Body.Code[i]; int depth = plan.Depths[i], n = op.Operand;
            int top = Slot(Math.Max(0, depth - 1));
            switch (op.Code) {
                case IrOperation.Constant:
                    I(w, plan.Body.Pool[n] is IntegerObj integer ? integer.Value : plan.Body.Pool[n] is BooleanObj boolean && boolean.Value ? 1 : 0);
                    Set(w, Slot(depth)); break;
                case IrOperation.Void: case IrOperation.Null: I(w, 0); Set(w, Slot(depth)); break;
                case IrOperation.Load:
                    if (variables.TryGetValue((string)plan.Body.Pool[n], out int v)) Copy(Slot(depth), Variable(v));
                    break; // A recursive callable occupies a compile-time-only slot.
                case IrOperation.Declare:
                    Copy(Variable(variables[(string)plan.Body.Pool[n]]), top); I(w, 0); Set(w, top); break;
                case IrOperation.Assign:
                    Copy(Variable(variables[(string)plan.Body.Pool[n]]), top); I(w, 0); Set(w, Slot(depth - 2)); break;
                case IrOperation.CompoundAssign:
                    var assignment = (Assignment)plan.Body.Pool[n]; Binary(assignment.Operation, Variable(variables[assignment.Name]), top);
                    I(w, 0); Set(w, Slot(depth - 2)); break;
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    v = Variable(variables[(string)plan.Body.Pool[n]]);
                    if (op.Code == IrOperation.IncrementPostfix) Copy(Slot(depth), v);
                    Get(w, v); w.Write((byte)0xac); L(w, 1); w.Write((byte)0x7c); CheckedWide(v);
                    if (op.Code == IrOperation.IncrementPrefix) Copy(Slot(depth), v);
                    break;
                case IrOperation.Duplicate: Copy(Slot(depth), top); break;
                case IrOperation.Pop: case IrOperation.JumpIfBreak: break;
                case IrOperation.Negate: L(w, 0); Get(w, top); w.Write(new byte[] { 0xac, 0x7d }); CheckedWide(top); break;
                case IrOperation.Not: Get(w, top); w.Write((byte)0x45); Set(w, top); break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                    Binary(op.Code, Slot(depth - 2), top); break;
                case IrOperation.Jump: w.Write((byte)0x0c); U(w, n - i - 1); break;
                case IrOperation.JumpIfFalse: Get(w, top); w.Write(new byte[] { 0x45, 0x0d }); U(w, n - i - 1); break;
                case IrOperation.Call:
                    Get(w, 0); Get(w, 1); I(w, 1); w.Write((byte)0x6a);
                    for (int p = 0; p < n; p++) Get(w, Slot(depth - n + p));
                    w.Write((byte)0x10); U(w, implementation); Set(w, result); Set(w, status);
                    Get(w, status); I(w, 1); w.Write(new byte[] { 0x47, 0x04, 0x40 }); Get(w, status); I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
                    Copy(Slot(depth - n - 1), result); break;
                case IrOperation.Return: I(w, 1); Get(w, top); w.Write((byte)0x0f); break;
                default: throw new InvalidOperationException("Unsupported integer instruction.");
            }
        }
        if (plan.Body.Code.Length != 0) w.Write((byte)0x0b);
        if (plan.Depths[^1] > 0) { I(w, 1); Get(w, Slot(plan.Depths[^1] - 1)); }
        else { I(w, 0); I(w, 0); } // Unreachable end after explicit returns.
        w.Write((byte)0x0b);
    }

    private static void U(BinaryWriter w, int n) => WasmCompiler.WriteUnsigned(w.BaseStream, checked((uint)n));
    private static void Signed(BinaryWriter w, long n) { bool more; do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more); }
    private static void I(BinaryWriter w, int n) { w.Write((byte)0x41); Signed(w, n); }
    private static void L(BinaryWriter w, long n) { w.Write((byte)0x42); Signed(w, n); }
    private static void Get(BinaryWriter w, int local) { w.Write((byte)0x20); U(w, local); }
    private static void Set(BinaryWriter w, int local) { w.Write((byte)0x21); U(w, local); }
    private static void Memory(BinaryWriter w, byte op, int align, int offset) { w.Write(op); U(w, align); U(w, offset); }
}
