using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Wasm;

/// <summary>A closed, effect-free scalar function represented by resolved variable
/// registers. Calls may only recurse through one guarded captured binding. Failed
/// guards can restart the generic body because no externally visible effect ran.</summary>
internal sealed class WasmNumericFunction
{
    internal readonly LoweredProgram Body;
    internal readonly string[] Parameters;
    internal readonly string[] Variables;
    internal readonly int[] Depths;
    internal readonly int Capacity;
    internal readonly string? RecursiveName;
    private readonly Dictionary<string, int> variables;

    private WasmNumericFunction(LoweredProgram body, string[] parameters, string[] names,
        int[] depths, int capacity, string? recursiveName)
    {
        Body = body; Parameters = parameters; Variables = names; Depths = depths;
        Capacity = capacity; RecursiveName = recursiveName;
        variables = names.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
    }

    internal static WasmNumericFunction? Analyze(LoweredProgram body, string[] parameters)
    {
        // Bound compiler effort, direct-call stack use and uninterruptible straight-line work.
        if (body.Code.Length > 256 || parameters.Length > 8) return null;
        var names = parameters.Distinct(StringComparer.Ordinal).ToList();
        foreach (var op in body.Code)
            if (op.Code == IrOperation.Declare && !names.Contains((string)body.Pool[op.Operand], StringComparer.Ordinal))
                names.Add((string)body.Pool[op.Operand]);
        string? recursive = null;
        foreach (var op in body.Code)
        {
            switch (op.Code)
            {
                case IrOperation.Constant:
                    if (body.Pool[op.Operand] is not (IntegerObj or FloatObj or DoubleObj or BooleanObj or NullObj)) return null;
                    break;
                case IrOperation.Load:
                    string name = (string)body.Pool[op.Operand];
                    if (!names.Contains(name, StringComparer.Ordinal)) {
                        if (recursive != null && recursive != name) return null;
                        recursive = name;
                    }
                    break;
                case IrOperation.Assign: case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    if (!names.Contains((string)body.Pool[op.Operand], StringComparer.Ordinal)) return null;
                    break;
                case IrOperation.CompoundAssign:
                    var assignment = (Assignment)body.Pool[op.Operand];
                    if (assignment.Operation == IrOperation.Modulo || !names.Contains(assignment.Name, StringComparer.Ordinal)) return null;
                    break;
                case IrOperation.Jump: case IrOperation.JumpIfFalse: case IrOperation.JumpIfBreak:
                case IrOperation.Pop: case IrOperation.Duplicate: case IrOperation.Declare:
                case IrOperation.Void: case IrOperation.Null: case IrOperation.Return: case IrOperation.Call:
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                case IrOperation.Negate: case IrOperation.Not:
                    break;
                default: return null; // No heap objects, loops, capabilities, captured writes or suspension.
            }
        }
        if (recursive != null && !body.Code.Any(op => op.Code == IrOperation.Call)) return null;
        var depths = Enumerable.Repeat(-1, body.Code.Length + 1).ToArray();
        var states = new Dictionary<int, bool[]>(); // true denotes the guarded recursive callable.
        var work = new Queue<(int Pc, bool[] Stack)>(); work.Enqueue((0, Array.Empty<bool>()));
        int capacity = 1;
        while (work.TryDequeue(out var state))
        {
            int pc = state.Pc;
            if (states.TryGetValue(pc, out var previous)) { if (!previous.SequenceEqual(state.Stack)) return null; continue; }
            states.Add(pc, state.Stack); depths[pc] = state.Stack.Length; capacity = Math.Max(capacity, state.Stack.Length);
            if (pc == body.Code.Length) { if (state.Stack.LastOrDefault()) return null; continue; }
            var stack = state.Stack.ToList(); var op = body.Code[pc];
            bool Take(int count) { if (stack.Count < count) return false; stack.RemoveRange(stack.Count - count, count); return true; }
            switch (op.Code)
            {
                case IrOperation.Constant: case IrOperation.Void: case IrOperation.Null:
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: stack.Add(false); break;
                case IrOperation.Load: stack.Add(!names.Contains((string)body.Pool[op.Operand], StringComparer.Ordinal)); break;
                case IrOperation.Duplicate: if (stack.Count == 0) return null; stack.Add(stack[^1]); break;
                case IrOperation.Pop: if (!Take(1)) return null; break;
                case IrOperation.Declare: case IrOperation.Negate: case IrOperation.Not:
                    if (stack.Count == 0 || stack[^1]) return null; break;
                case IrOperation.Assign: case IrOperation.CompoundAssign:
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                    if (stack.Count < 2 || stack[^1] || stack[^2] || !Take(2)) return null; stack.Add(false); break;
                case IrOperation.Call:
                    int count = op.Operand;
                    if (count != parameters.Length || stack.Count <= count || !stack[stack.Count - count - 1] ||
                        stack.Skip(stack.Count - count).Any(x => x) || !Take(count + 1)) return null;
                    stack.Add(false); break;
                case IrOperation.JumpIfFalse: if (!Take(1)) return null; break;
                case IrOperation.JumpIfBreak: break; // A closed scalar body cannot produce a BreakObj.
                case IrOperation.Return: if (stack.Count == 0 || stack[^1]) return null; continue;
            }
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.JumpIfBreak) {
                if (op.Operand <= pc || op.Operand > body.Code.Length) return null;
                if (op.Code != IrOperation.JumpIfBreak) work.Enqueue((op.Operand, stack.ToArray()));
            }
            if (op.Code != IrOperation.Jump) work.Enqueue((pc + 1, stack.ToArray()));
        }
        return new(body, parameters, names.ToArray(), depths, capacity, recursive);
    }

    internal void WriteType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 2 + Parameters.Length * 2);
        w.Write(new byte[] { 0x7f, 0x7f });
        foreach (var _ in Parameters) w.Write(new byte[] { 0x7f, 0x7c });
        U(w, 3); w.Write(new byte[] { 0x7f, 0x7f, 0x7c }); // status, tag, payload
    }

    internal void EmitAdapter(BinaryWriter w, int implementation, int? integerImplementation = null)
    {
        U(w, 3); U(w, 1); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7c);
        // (args, output, execution) -> status; only scalar arguments enter typed code.
        for (int p = 0; p < Parameters.Length; p++) {
            Get(w, 0); Memory(w, 0x28, 2, p * 16); Set(w, 4);
            Get(w, 4); I(w, 4); w.Write((byte)0x4d); Get(w, 4); I(w, 11); w.Write(new byte[] { 0x46, 0x72, 0x45, 0x04, 0x40 });
            I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
        }
        if (integerImplementation.HasValue) {
            I(w, 1); Set(w, 4);
            for (int p = 0; p < Parameters.Length; p++) {
                Get(w, 4); Get(w, 0); Memory(w, 0x28, 2, p * 16); I(w, 1); w.Write(new byte[] { 0x46, 0x71 }); Set(w, 4);
            }
            Get(w, 4); w.Write(new byte[] { 0x04, 0x40 });
            // Also validate the payload so a malformed host value cannot trap or
            // be silently truncated by a representation-changing fast path.
            for (int p = 0; p < Parameters.Length; p++) {
                Get(w, 0); Memory(w, 0x2b, 3, p * 16 + 8); Set(w, 5);
                Get(w, 5); F(w, -2147483648d); w.Write((byte)0x66);
                Get(w, 5); F(w, 2147483648d); w.Write(new byte[] { 0x63, 0x71, 0x45, 0x04, 0x40 });
                I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
                Get(w, 5); Get(w, 5); w.Write(new byte[] { 0xaa, 0xb7, 0x62, 0x04, 0x40 });
                I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
            }
            Get(w, 2); I(w, 0);
            for (int p = 0; p < Parameters.Length; p++) { Get(w, 0); Memory(w, 0x2b, 3, p * 16 + 8); w.Write((byte)0xaa); }
            w.Write((byte)0x10); U(w, integerImplementation.Value); Set(w, 4); Set(w, 3);
            Get(w, 4); w.Write((byte)0xb7); Set(w, 5); I(w, 1); Set(w, 4);
            w.Write((byte)0x05);
        }
        // Repeated speculative tag/f64 recursion was slower than resumable
        // execution. Recursive acceleration requires the proven i32 variant.
        if (RecursiveName != null) { I(w, 0); w.Write((byte)0x0f); }
        Get(w, 2); I(w, 0);
        for (int p = 0; p < Parameters.Length; p++) {
            Get(w, 0); Memory(w, 0x28, 2, p * 16); Get(w, 0); Memory(w, 0x2b, 3, p * 16 + 8);
        }
        w.Write((byte)0x10); U(w, implementation); Set(w, 5); Set(w, 4); Set(w, 3);
        if (integerImplementation.HasValue) w.Write((byte)0x0b);
        Get(w, 3); I(w, 1); w.Write(new byte[] { 0x46, 0x04, 0x40 });
        Get(w, 1); Get(w, 4); Memory(w, 0x36, 2); Get(w, 1); I(w, 0); Memory(w, 0x36, 2, 4);
        Get(w, 1); Get(w, 5); Memory(w, 0x39, 3, 8); w.Write((byte)0x0b);
        Get(w, 3); w.Write((byte)0x0b);
    }

    internal void EmitImplementation(BinaryWriter w, int implementation)
    {
        int first = 2 + Parameters.Length * 2;
        int VarTag(int v) => first + v * 2;
        int VarNumber(int v) => VarTag(v) + 1;
        int SlotTag(int v) => first + Variables.Length * 2 + v * 2;
        int SlotNumber(int v) => SlotTag(v) + 1;
        int status = SlotTag(Capacity), resultTag = status + 1, resultNumber = status + 2;
        U(w, (Variables.Length + Capacity) * 2 + 2);
        for (int n = 0; n < Variables.Length + Capacity; n++) { U(w, 1); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7c); }
        U(w, 2); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7c);
        void Fail(int code) { I(w, code); I(w, 0); F(w, 0); w.Write((byte)0x0f); }
        void GuardNumeric(int tag) { Get(w, tag); I(w, 1); w.Write((byte)0x4f); Get(w, tag); I(w, 3); w.Write(new byte[] { 0x4d, 0x71, 0x45, 0x04, 0x40 }); Fail(0); w.Write((byte)0x0b); }
        void GuardBool(int tag) { Get(w, tag); I(w, 4); w.Write(new byte[] { 0x47, 0x04, 0x40 }); Fail(0); w.Write((byte)0x0b); }
        void Pair(int toTag, int toNumber, int fromTag, int fromNumber) { Get(w, fromTag); Set(w, toTag); Get(w, fromNumber); Set(w, toNumber); }
        void Empty(int slot, int tag = 0) { I(w, tag); Set(w, SlotTag(slot)); F(w, 0); Set(w, SlotNumber(slot)); }
        void Normalize(int tag, int number) {
            Get(w, tag); I(w, 1); w.Write(new byte[] { 0x46, 0x04, 0x40 });
            Get(w, number); F(w, -2147483648d); w.Write((byte)0x66); Get(w, number); F(w, 2147483648d); w.Write(new byte[] { 0x63, 0x71, 0x04, 0x7c });
            Get(w, number); w.Write(new byte[] { 0xaa, 0xb7, 0x05 }); F(w, -2147483648d); w.Write((byte)0x0b); Set(w, number);
            w.Write((byte)0x05); Get(w, tag); I(w, 2); w.Write(new byte[] { 0x46, 0x04, 0x40 });
            Get(w, number); w.Write(new byte[] { 0xb6, 0xbb }); Set(w, number); w.Write(new byte[] { 0x0b, 0x0b });
        }
        void Binary(IrOperation op, int leftTag, int leftNumber, int rightTag, int rightNumber, bool compound = false) {
            if (op is IrOperation.Equal or IrOperation.NotEqual) {
                // Scalar equality also permits distinct types, null and void.
                Get(w, leftTag); Get(w, rightTag); w.Write((byte)0x46);
                Get(w, leftTag); I(w, 1); w.Write((byte)0x4f); Get(w, leftTag); I(w, 3); w.Write(new byte[] { 0x4d, 0x71 });
                Get(w, rightTag); I(w, 1); w.Write((byte)0x4f); Get(w, rightTag); I(w, 3); w.Write(new byte[] { 0x4d, 0x71, 0x71, 0x72 });
                Get(w, leftNumber); Get(w, rightNumber); w.Write(new byte[] { 0x61, 0x71 });
                if (op == IrOperation.NotEqual) w.Write((byte)0x45);
                w.Write((byte)0xb7); Set(w, leftNumber); I(w, 4); Set(w, leftTag); return;
            }
            if (op is IrOperation.And or IrOperation.Or) {
                GuardBool(leftTag); GuardBool(rightTag);
                Get(w, leftNumber); F(w, 0); w.Write((byte)0x62); Get(w, rightNumber); F(w, 0); w.Write((byte)0x62);
                w.Write(op == IrOperation.And ? (byte)0x71 : (byte)0x72); w.Write((byte)0xb7); Set(w, leftNumber); return;
            }
            GuardNumeric(leftTag); GuardNumeric(rightTag);
            bool comparison = op is IrOperation.Less or IrOperation.Greater or IrOperation.LessEqual or IrOperation.GreaterEqual;
            if (!comparison && !compound) {
                Get(w, leftTag); Get(w, rightTag); w.Write(new byte[] { 0x4b, 0x04, 0x7f }); Get(w, leftTag);
                w.Write((byte)0x05); Get(w, rightTag); w.Write((byte)0x0b); Set(w, leftTag);
            }
            Get(w, leftNumber); Get(w, rightNumber);
            w.Write(op switch {
                IrOperation.Add => (byte)0xa0, IrOperation.Subtract => (byte)0xa1,
                IrOperation.Multiply => (byte)0xa2, IrOperation.Divide => (byte)0xa3,
                IrOperation.Less => (byte)0x63, IrOperation.Greater => (byte)0x64,
                IrOperation.LessEqual => (byte)0x65, IrOperation.GreaterEqual => (byte)0x66,
                _ => throw new InvalidOperationException("Invalid scalar operation.") });
            if (comparison) w.Write((byte)0xb7); Set(w, leftNumber);
            if (comparison) { I(w, 4); Set(w, leftTag); } else Normalize(leftTag, leftNumber);
        }
        void Fuel() {
            Get(w, 0); Memory(w, 0x28, 2, 24); w.Write(new byte[] { 0x45, 0x04, 0x40 }); Fail(2); w.Write((byte)0x0b);
            Get(w, 0); Get(w, 0); Memory(w, 0x28, 2, 24); I(w, 1); w.Write((byte)0x6b); Memory(w, 0x36, 2, 24);
        }
        int depthLimit = Math.Min(64, Math.Max(2, 1024 / (Variables.Length + Capacity + Parameters.Length * 2 + 8)));
        Get(w, 1); I(w, depthLimit); w.Write(new byte[] { 0x4f, 0x04, 0x40 }); Fail(3); w.Write((byte)0x0b);
        for (int v = 0; v < Variables.Length; v++) { I(w, -1); Set(w, VarTag(v)); }
        for (int p = 0; p < Parameters.Length; p++) Pair(VarTag(variables[Parameters[p]]), VarNumber(variables[Parameters[p]]), 2 + p * 2, 3 + p * 2);
        // Forward-only control flow uses real structured branches, with no frame PC.
        for (int i = Body.Code.Length; i > 0; i--) w.Write(new byte[] { 0x02, 0x40 });
        var leaders = new HashSet<int> { 0 };
        for (int i = 0; i < Body.Code.Length; i++) {
            var op = Body.Code[i];
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse) { leaders.Add(op.Operand); leaders.Add(i + 1); }
            if (op.Code is IrOperation.Call or IrOperation.Return) leaders.Add(i + 1);
        }
        for (int i = 0; i < Body.Code.Length; i++)
        {
            if (i > 0) w.Write((byte)0x0b);
            if (Depths[i] < 0) continue;
            if (leaders.Contains(i)) Fuel();
            var op = Body.Code[i]; int depth = Depths[i], n = op.Operand;
            int t = SlotTag(Math.Max(0, depth - 1)), x = SlotNumber(Math.Max(0, depth - 1));
            switch (op.Code)
            {
                case IrOperation.Constant:
                    (int Tag, double Number) value = Body.Pool[n] switch {
                        IntegerObj v => (1, v.Value), FloatObj v => (2, v.Value), DoubleObj v => (3, v.Value),
                        BooleanObj v => (4, v.Value ? 1 : 0), NullObj => (11, 0), _ => throw new InvalidOperationException() };
                    I(w, value.Tag); Set(w, SlotTag(depth)); F(w, value.Number); Set(w, SlotNumber(depth)); break;
                case IrOperation.Void: Empty(depth); break;
                case IrOperation.Null: Empty(depth, 11); break;
                case IrOperation.Load:
                    if (!variables.TryGetValue((string)Body.Pool[n], out int variable)) Empty(depth, 8);
                    else {
                        Get(w, VarTag(variable)); I(w, -1); w.Write(new byte[] { 0x46, 0x04, 0x40 }); Fail(0); w.Write((byte)0x0b);
                        Pair(SlotTag(depth), SlotNumber(depth), VarTag(variable), VarNumber(variable));
                    }
                    break;
                case IrOperation.Declare:
                    variable = variables[(string)Body.Pool[n]]; Pair(VarTag(variable), VarNumber(variable), t, x); Empty(depth - 1); break;
                case IrOperation.Assign:
                    variable = variables[(string)Body.Pool[n]]; Pair(VarTag(variable), VarNumber(variable), t, x); Empty(depth - 2); break;
                case IrOperation.CompoundAssign:
                    var assignment = (Assignment)Body.Pool[n]; variable = variables[assignment.Name];
                    Binary(assignment.Operation, VarTag(variable), VarNumber(variable), t, x, true); Empty(depth - 2); break;
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix:
                    variable = variables[(string)Body.Pool[n]]; GuardNumeric(VarTag(variable));
                    if (op.Code == IrOperation.IncrementPostfix) Pair(SlotTag(depth), SlotNumber(depth), VarTag(variable), VarNumber(variable));
                    Get(w, VarNumber(variable)); F(w, 1); w.Write((byte)0xa0); Set(w, VarNumber(variable)); Normalize(VarTag(variable), VarNumber(variable));
                    if (op.Code == IrOperation.IncrementPrefix) Pair(SlotTag(depth), SlotNumber(depth), VarTag(variable), VarNumber(variable));
                    break;
                case IrOperation.Duplicate: Pair(SlotTag(depth), SlotNumber(depth), t, x); break;
                case IrOperation.Pop: case IrOperation.JumpIfBreak: break;
                case IrOperation.Negate: GuardNumeric(t); Get(w, x); w.Write((byte)0x9a); Set(w, x); Normalize(t, x); break;
                case IrOperation.Not: GuardBool(t); Get(w, x); F(w, 0); w.Write(new byte[] { 0x61, 0xb7 }); Set(w, x); break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                    Binary(op.Code, SlotTag(depth - 2), SlotNumber(depth - 2), t, x); break;
                case IrOperation.Jump: w.Write((byte)0x0c); U(w, n - i - 1); break;
                case IrOperation.JumpIfFalse:
                    GuardBool(t); Get(w, x); F(w, 0); w.Write(new byte[] { 0x61, 0x0d }); U(w, n - i - 1); break;
                case IrOperation.Call:
                    Get(w, 0); Get(w, 1); I(w, 1); w.Write((byte)0x6a);
                    for (int p = 0; p < n; p++) { Get(w, SlotTag(depth - n + p)); Get(w, SlotNumber(depth - n + p)); }
                    w.Write((byte)0x10); U(w, implementation); Set(w, resultNumber); Set(w, resultTag); Set(w, status);
                    Get(w, status); I(w, 1); w.Write(new byte[] { 0x47, 0x04, 0x40 }); Get(w, status); I(w, 0); F(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
                    Pair(SlotTag(depth - n - 1), SlotNumber(depth - n - 1), resultTag, resultNumber); break;
                case IrOperation.Return: I(w, 1); Get(w, t); Get(w, x); w.Write((byte)0x0f); break;
                default: throw new InvalidOperationException("Unsupported scalar instruction.");
            }
        }
        if (Body.Code.Length != 0) w.Write((byte)0x0b);
        int endDepth = Depths[^1];
        if (endDepth > 0) { I(w, 1); Get(w, SlotTag(endDepth - 1)); Get(w, SlotNumber(endDepth - 1)); }
        else { I(w, 1); I(w, 0); F(w, 0); }
        w.Write((byte)0x0b);
    }

    private static void U(BinaryWriter w, int n) => WasmCompiler.WriteUnsigned(w.BaseStream, checked((uint)n));
    private static void I(BinaryWriter w, int n) { w.Write((byte)0x41); bool more; do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more); }
    private static void F(BinaryWriter w, double n) { w.Write((byte)0x44); w.Write(n); }
    private static void Get(BinaryWriter w, int local) { w.Write((byte)0x20); U(w, local); }
    private static void Set(BinaryWriter w, int local) { w.Write((byte)0x21); U(w, local); }
    private static void Memory(BinaryWriter w, byte op, int align, int offset = 0) { w.Write(op); U(w, align); U(w, offset); }
}
