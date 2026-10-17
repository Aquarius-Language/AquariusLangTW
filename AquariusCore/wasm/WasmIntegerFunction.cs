using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;

namespace AquariusLang.Wasm;

/// <summary>A statically typed specialization of a closed scalar function for integer arguments.
/// Every operand and variable has one proven scalar type at each instruction (integer, float, double,
/// boolean, null or void) and every return has the same type, so values use untagged Wasm locals:
/// integers and booleans as i32, floats and doubles as f64. Integer arithmetic reproduces the language's
/// rules exactly with i64 intermediates; float results are rounded like the generic runtime.</summary>
internal sealed class WasmIntegerFunction
{
    private const int VoidType = 0, IntType = 1, FloatType = 2, DoubleType = 3, BoolType = 4, CallableType = 8, NullType = 11;
    private readonly WasmNumericFunction plan;
    private readonly Dictionary<string, int> variables;
    private Dictionary<int, (int[] Stack, int[] Variables)> states = new();
    /// <summary>The proven type of every return value.</summary>
    internal int ResultType { get; private set; }

    /// <summary>Deoptimization target: the promoted generic body of the same function. Instead of failing
    /// at its depth or fuel bound, the typed code then materializes its native activations as generic
    /// frames (variables in their frame slots, operands below the pending call) and lets the scheduler
    /// continue, so no work is repeated and fuel is consumed exactly as by the generic body.</summary>
    internal sealed record Deopt(int[] Slots, int Locals, int[] Starts, int Weight, int FrameHelper);

    private WasmIntegerFunction(WasmNumericFunction plan)
    {
        this.plan = plan;
        variables = plan.Variables.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
    }

    private static bool Numeric(int type) => type is IntType or FloatType or DoubleType;
    private static bool Wide(int type) => type is FloatType or DoubleType;

    internal static WasmIntegerFunction? Analyze(WasmNumericFunction plan)
    {
        // Try the result types in order; recursion is typed by induction on the assumed result.
        foreach (int result in new[] { IntType, DoubleType, FloatType, BoolType })
            if (Analyze(plan, result) is { } typed) return typed;
        return null;
    }

    private static WasmIntegerFunction? Analyze(WasmNumericFunction plan, int resultType)
    {
        var candidate = new WasmIntegerFunction(plan) { ResultType = resultType };
        int[] initial = Enumerable.Repeat(-1, plan.Variables.Length).ToArray();
        foreach (string name in plan.Parameters) initial[candidate.variables[name]] = IntType;
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
                if (stack.Count == 0 || stack[^1] != resultType) return null;
                returns = true; continue;
            }
            var op = plan.Body.Code[state.Pc]; int n = op.Operand;
            int Top(int k = 1) => stack[^k];
            int Variable(string name) => candidate.variables[name];
            switch (op.Code) {
                case IrOperation.Constant:
                    stack.Add(plan.Body.Pool[n] switch {
                        IntegerObj => IntType, FloatObj => FloatType, DoubleObj => DoubleType, BooleanObj => BoolType, NullObj => NullType, _ => -1 });
                    if (stack[^1] < 0) return null;
                    break;
                case IrOperation.Void: stack.Add(VoidType); break;
                case IrOperation.Null: stack.Add(NullType); break;
                case IrOperation.Load:
                    if (!candidate.variables.TryGetValue((string)plan.Body.Pool[n], out int variable)) stack.Add(CallableType);
                    else { if (locals[variable] < 0) return null; stack.Add(locals[variable]); }
                    break;
                case IrOperation.Declare:
                    if (Top() == CallableType) return null;
                    locals[Variable((string)plan.Body.Pool[n])] = Top(); stack[^1] = VoidType; break;
                case IrOperation.Assign: {
                    int v = Variable((string)plan.Body.Pool[n]);
                    if (locals[v] < 0 || Top() == CallableType) return null;
                    locals[v] = Top(); stack.RemoveRange(stack.Count - 2, 2); stack.Add(VoidType); break;
                }
                case IrOperation.CompoundAssign: {
                    var assignment = (Assignment)plan.Body.Pool[n]; int v = Variable(assignment.Name);
                    if (locals[v] < 0 || !Numeric(locals[v]) || !Numeric(Top())) return null;
                    if (assignment.Operation is not (IrOperation.Add or IrOperation.Subtract or IrOperation.Multiply or IrOperation.Divide)) return null;
                    stack.RemoveRange(stack.Count - 2, 2); stack.Add(VoidType); break;
                }
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: {
                    int v = Variable((string)plan.Body.Pool[n]);
                    if (locals[v] < 0 || !Numeric(locals[v])) return null;
                    stack.Add(locals[v]); break;
                }
                case IrOperation.Duplicate: stack.Add(Top()); break;
                case IrOperation.Pop: stack.RemoveAt(stack.Count - 1); break;
                case IrOperation.Negate: if (!Numeric(Top())) return null; break;
                case IrOperation.Not: if (Top() == CallableType) return null; stack[^1] = BoolType; break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: {
                    if (!Numeric(Top()) || !Numeric(Top(2))) return null;
                    int type = Math.Max(Top(), Top(2)); stack.RemoveAt(stack.Count - 1); stack[^1] = type; break;
                }
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                    if (!Numeric(Top()) || !Numeric(Top(2))) return null;
                    stack.RemoveAt(stack.Count - 1); stack[^1] = BoolType; break;
                case IrOperation.Equal: case IrOperation.NotEqual:
                    if (Top() == CallableType || Top(2) == CallableType) return null;
                    stack.RemoveAt(stack.Count - 1); stack[^1] = BoolType; break;
                case IrOperation.And: case IrOperation.Or:
                    if (Top() != BoolType || Top(2) != BoolType) return null;
                    stack.RemoveAt(stack.Count - 1); stack[^1] = BoolType; break;
                case IrOperation.JumpIfFalse:
                    if (Top() == CallableType) return null; stack.RemoveAt(stack.Count - 1); break;
                case IrOperation.Jump: case IrOperation.JumpIfBreak: break;
                case IrOperation.Call:
                    if (stack.Count < n + 1 || stack[stack.Count - n - 1] != CallableType || stack.Skip(stack.Count - n).Any(t => t != IntType)) return null;
                    stack.RemoveRange(stack.Count - n - 1, n + 1); stack.Add(resultType); break;
                case IrOperation.Return:
                    if (Top() != resultType) return null; returns = true; continue;
                default: return null;
            }
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse)
                work.Enqueue((n, stack.ToArray(), locals));
            if (op.Code != IrOperation.Jump) work.Enqueue((state.Pc + 1, stack.ToArray(), locals));
        }
        // Recursive returns are sound by induction: every terminating base return has the
        // result type and every allowed operation preserves its proven type.
        candidate.states = states;
        return returns ? candidate : null;
    }

    internal void WriteType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 2 + plan.Parameters.Length);
        for (int i = 0; i < 2 + plan.Parameters.Length; i++) w.Write((byte)0x7f);
        U(w, 3); w.Write(new byte[] { 0x7f, 0x7f, 0x7c }); // status, i32 result, f64 result
    }

    internal void EmitImplementation(BinaryWriter w, int implementation, Deopt? deopt = null)
    {
        // Locals: parameters (execution, depth, arguments); then per variable and operand an i32 and an f64.
        int first = 2 + plan.Parameters.Length;
        int nv = plan.Variables.Length, ns = plan.Capacity;
        int VarI(int v) => first + v;
        int SlotI(int s) => first + nv + s;
        int status = first + nv + ns, resultI = status + 1, frame = status + 2;
        int wide = status + 3;
        int VarF(int v) => wide + 1 + v;
        int SlotF(int s) => wide + 1 + nv + s;
        int resultF = wide + 1 + nv + ns, scratch = resultF + 1;
        U(w, 3); U(w, nv + ns + 3); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7e); U(w, nv + ns + 2); w.Write((byte)0x7c);
        void Fail(int code) { I(w, code); I(w, 0); F(w, 0); w.Write((byte)0x0f); }
        void Fuel() {
            if (deopt != null) return; // Fuel is consumed once at entry, like the generic body's checkpoint.
            Get(w, 0); Memory(w, 0x28, 2, 24); w.Write(new byte[] { 0x45, 0x04, 0x40 }); Fail(2); w.Write((byte)0x0b);
            Get(w, 0); Get(w, 0); Memory(w, 0x28, 2, 24); I(w, 1); w.Write((byte)0x6b); Memory(w, 0x36, 2, 24);
        }
        // Pushes a typed value as f64 (numeric conversion for mixed arithmetic).
        void AsDouble(int type, int i, int f) { if (Wide(type)) Get(w, f); else { Get(w, i); w.Write((byte)0xb7); } }
        void InRange(int f) { Get(w, f); F(w, -2147483648d); w.Write((byte)0x66); Get(w, f); F(w, 2147483648d); w.Write(new byte[] { 0x63, 0x71 }); }
        void CheckedWide(int to) {
            Set(w, wide);
            Get(w, wide); w.Write((byte)0xa7); I(w, int.MinValue); Get(w, wide); Get(w, wide); w.Write(new byte[] { 0xa7, 0xac, 0x51, 0x1b }); Set(w, to);
        }
        // Stores a typed value as a generic Value at frame+offset.
        void StoreValue(int frameLocal, int offset, int type, int i, int f) {
            Get(w, frameLocal); I(w, type); Memory(w, 0x36, 2, offset);
            Get(w, frameLocal); if (type == CallableType) { Get(w, 0); Memory(w, 0x28, 2, 40); } else I(w, 0); Memory(w, 0x36, 2, offset + 4);
            Get(w, frameLocal);
            if (type == IntType) { Get(w, i); w.Write((byte)0xb7); } else if (type == BoolType) { Get(w, i); w.Write((byte)0xb8); }
            else if (Wide(type)) Get(w, f); else F(w, 0);
            Memory(w, 0x39, 3, offset + 8);
        }
        void NewFrame(int pc, int depth) {
            Get(w, 0); I(w, pc); I(w, depth); w.Write((byte)0x10); U(w, deopt!.FrameHelper); Set(w, frame);
        }
        void Copy(int type, int toI, int toF, int fromI, int fromF) {
            if (Wide(type)) { Get(w, fromF); Set(w, toF); } else if (type is IntType or BoolType) { Get(w, fromI); Set(w, toI); }
        }
        if (deopt != null) {
            int depthLimit = Math.Min(512, Math.Max(16, 16384 / (2 * (nv + ns) + plan.Parameters.Length + 8)));
            // Deoptimize at the depth bound, on an exhausted budget (the generic checkpoint then yields)
            // or when a collection is pending; otherwise consume the entry weight like the checkpoint.
            Get(w, 1); I(w, depthLimit); w.Write((byte)0x4f);
            Get(w, 0); Memory(w, 0x28, 2, 24); w.Write(new byte[] { 0x45, 0x72 });
            Get(w, 0); Memory(w, 0x28, 2, 36); w.Write(new byte[] { 0x72, 0x04, 0x40 });
            // Entry deoptimization: the generic body starts at its first block and runs its own checkpoint.
            NewFrame(0, 0);
            for (int p = 0; p < plan.Parameters.Length; p++)
                StoreValue(frame, 56 + 16 * deopt.Slots[variables[plan.Parameters[p]]], IntType, 2 + p, 0);
            I(w, 4); Get(w, frame); F(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
            Get(w, 0);
            Get(w, 0); Memory(w, 0x28, 2, 24); I(w, deopt.Weight); w.Write((byte)0x6b); I(w, 0);
            Get(w, 0); Memory(w, 0x28, 2, 24); I(w, deopt.Weight); w.Write(new byte[] { 0x4b, 0x1b });
            Memory(w, 0x36, 2, 24);
        } else {
            int depthLimit = Math.Min(64, Math.Max(2, 1024 / (2 * (nv + ns) + plan.Parameters.Length + 8)));
            Get(w, 1); I(w, depthLimit); w.Write(new byte[] { 0x4f, 0x04, 0x40 }); Fail(3); w.Write((byte)0x0b);
        }
        for (int p = 0; p < plan.Parameters.Length; p++) { Get(w, 2 + p); Set(w, VarI(variables[plan.Parameters[p]])); }
        for (int i = plan.Body.Code.Length; i > 0; i--) w.Write(new byte[] { 0x02, 0x40 });
        var leaders = new HashSet<int> { 0 };
        for (int i = 0; i < plan.Body.Code.Length; i++) {
            var op = plan.Body.Code[i];
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse) { leaders.Add(op.Operand); leaders.Add(i + 1); }
            if (op.Code is IrOperation.Call or IrOperation.Return) leaders.Add(i + 1);
        }
        for (int i = 0; i < plan.Body.Code.Length; i++) {
            if (i > 0) w.Write((byte)0x0b);
            if (!states.TryGetValue(i, out var state)) continue;
            if (leaders.Contains(i)) Fuel();
            var op = plan.Body.Code[i]; int depth = state.Stack.Length, n = op.Operand;
            var stack = state.Stack; var types = state.Variables;
            int TopType(int k = 1) => stack[depth - k];
            int top = Math.Max(0, depth - 1), below = Math.Max(0, depth - 2);
            switch (op.Code) {
                case IrOperation.Constant:
                    switch (plan.Body.Pool[n]) {
                        case IntegerObj v: I(w, v.Value); Set(w, SlotI(depth)); break;
                        case BooleanObj v: I(w, v.Value ? 1 : 0); Set(w, SlotI(depth)); break;
                        case FloatObj v: F(w, v.Value); Set(w, SlotF(depth)); break;
                        case DoubleObj v: F(w, v.Value); Set(w, SlotF(depth)); break;
                    }
                    break;
                case IrOperation.Void: case IrOperation.Null: break;
                case IrOperation.Load:
                    if (variables.TryGetValue((string)plan.Body.Pool[n], out int lv)) Copy(types[lv], SlotI(depth), SlotF(depth), VarI(lv), VarF(lv));
                    break; // A recursive callable occupies a compile-time-only slot.
                case IrOperation.Declare: {
                    int v = variables[(string)plan.Body.Pool[n]]; Copy(TopType(), VarI(v), VarF(v), SlotI(top), SlotF(top)); break;
                }
                case IrOperation.Assign: {
                    int v = variables[(string)plan.Body.Pool[n]]; Copy(TopType(), VarI(v), VarF(v), SlotI(top), SlotF(top)); break;
                }
                case IrOperation.CompoundAssign: {
                    var assignment = (Assignment)plan.Body.Pool[n]; int v = variables[assignment.Name];
                    int left = types[v], right = TopType();
                    // Compound assignment preserves the left operand's type.
                    if (left == IntType && right == IntType) EmitInt(assignment.Operation, VarI(v), SlotI(top), VarI(v));
                    else {
                        AsDouble(left, VarI(v), VarF(v)); AsDouble(right, SlotI(top), SlotF(top));
                        w.Write(Arithmetic(assignment.Operation)); Set(w, scratch);
                        Narrow(left, scratch, VarI(v), VarF(v));
                    }
                    break;
                }
                case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: {
                    int v = variables[(string)plan.Body.Pool[n]], type = types[v];
                    if (op.Code == IrOperation.IncrementPostfix) Copy(type, SlotI(depth), SlotF(depth), VarI(v), VarF(v));
                    if (type == IntType) { Get(w, VarI(v)); w.Write((byte)0xac); L(w, 1); w.Write((byte)0x7c); CheckedWide(VarI(v)); }
                    else { Get(w, VarF(v)); F(w, 1); w.Write((byte)0xa0); Set(w, scratch); Narrow(type, scratch, VarI(v), VarF(v)); }
                    if (op.Code == IrOperation.IncrementPrefix) Copy(type, SlotI(depth), SlotF(depth), VarI(v), VarF(v));
                    break;
                }
                case IrOperation.Duplicate: Copy(TopType(), SlotI(depth), SlotF(depth), SlotI(top), SlotF(top)); break;
                case IrOperation.Pop: case IrOperation.JumpIfBreak: break;
                case IrOperation.Negate:
                    if (TopType() == IntType) { L(w, 0); Get(w, SlotI(top)); w.Write(new byte[] { 0xac, 0x7d }); CheckedWide(SlotI(top)); }
                    else { Get(w, SlotF(top)); w.Write((byte)0x9a); Set(w, SlotF(top)); }
                    break;
                case IrOperation.Not:
                    if (TopType() == BoolType) { Get(w, SlotI(top)); w.Write((byte)0x45); }
                    else I(w, TopType() == NullType ? 1 : 0);
                    Set(w, SlotI(top)); break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: {
                    int left = TopType(2), right = TopType(), type = Math.Max(left, right);
                    if (type == IntType) EmitInt(op.Code, SlotI(below), SlotI(top), SlotI(below));
                    else {
                        AsDouble(left, SlotI(below), SlotF(below)); AsDouble(right, SlotI(top), SlotF(top));
                        w.Write(Arithmetic(op.Code)); Set(w, SlotF(below));
                        if (type == FloatType) { Get(w, SlotF(below)); w.Write(new byte[] { 0xb6, 0xbb }); Set(w, SlotF(below)); }
                    }
                    break;
                }
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual: {
                    int left = TopType(2), right = TopType();
                    if (left == IntType && right == IntType) { Get(w, SlotI(below)); Get(w, SlotI(top)); w.Write(IntComparison(op.Code)); }
                    else { AsDouble(left, SlotI(below), SlotF(below)); AsDouble(right, SlotI(top), SlotF(top)); w.Write(FloatComparison(op.Code)); }
                    Set(w, SlotI(below)); break;
                }
                case IrOperation.Equal: case IrOperation.NotEqual: {
                    int left = TopType(2), right = TopType();
                    bool equal = op.Code == IrOperation.Equal;
                    if (Numeric(left) && Numeric(right)) {
                        if (left == IntType && right == IntType) { Get(w, SlotI(below)); Get(w, SlotI(top)); w.Write(equal ? (byte)0x46 : (byte)0x47); }
                        else { AsDouble(left, SlotI(below), SlotF(below)); AsDouble(right, SlotI(top), SlotF(top)); w.Write(equal ? (byte)0x61 : (byte)0x62); }
                    } else if (left == BoolType && right == BoolType) { Get(w, SlotI(below)); Get(w, SlotI(top)); w.Write(equal ? (byte)0x46 : (byte)0x47); }
                    else I(w, (left == right) == equal ? 1 : 0); // Null and void compare by tag; distinct tags are unequal.
                    Set(w, SlotI(below)); break;
                }
                case IrOperation.And: case IrOperation.Or:
                    Get(w, SlotI(below)); Get(w, SlotI(top)); w.Write(op.Code == IrOperation.And ? (byte)0x71 : (byte)0x72); Set(w, SlotI(below)); break;
                case IrOperation.Jump: w.Write((byte)0x0c); U(w, n - i - 1); break;
                case IrOperation.JumpIfFalse:
                    if (TopType() == BoolType) { Get(w, SlotI(top)); w.Write(new byte[] { 0x45, 0x0d }); U(w, n - i - 1); }
                    else if (TopType() == NullType) { w.Write((byte)0x0c); U(w, n - i - 1); }
                    break; // Numbers and void are true.
                case IrOperation.Call: {
                    Get(w, 0); Get(w, 1); I(w, 1); w.Write((byte)0x6a);
                    for (int p = 0; p < n; p++) Get(w, SlotI(depth - n + p));
                    w.Write((byte)0x10); U(w, implementation); Set(w, resultF); Set(w, resultI); Set(w, status);
                    if (deopt != null) {
                        // The callee deoptimized: materialize this activation as its parent frame, resuming
                        // after the call with the operands below the callee and every declared variable.
                        Get(w, status); I(w, 4); w.Write(new byte[] { 0x46, 0x04, 0x40 });
                        int under = depth - n - 1, block = Array.BinarySearch(deopt.Starts, i + 1);
                        if (block < 0) throw new InvalidOperationException("Call continuation is not a generic block.");
                        NewFrame(block, under);
                        for (int vi = 0; vi < nv; vi++)
                            if (types[vi] >= 0) StoreValue(frame, 56 + 16 * deopt.Slots[vi], types[vi], VarI(vi), VarF(vi));
                        for (int k = 0; k < under; k++) StoreValue(frame, 56 + 16 * (deopt.Locals + k), stack[k], SlotI(k), SlotF(k));
                        Get(w, resultI); Get(w, frame); Memory(w, 0x36, 2, 0);
                        I(w, 4); Get(w, frame); F(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
                    }
                    Get(w, status); I(w, 1); w.Write(new byte[] { 0x47, 0x04, 0x40 }); Get(w, status); I(w, 0); F(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
                    Copy(ResultType, SlotI(depth - n - 1), SlotF(depth - n - 1), resultI, resultF);
                    break;
                }
                case IrOperation.Return: Result(TopType(), SlotI(top), SlotF(top)); break;
                default: throw new InvalidOperationException("Unsupported typed scalar instruction.");
            }
        }
        if (plan.Body.Code.Length != 0) w.Write((byte)0x0b);
        if (states.TryGetValue(plan.Body.Code.Length, out var end) && end.Stack.Length > 0)
            Result(end.Stack[^1], SlotI(end.Stack.Length - 1), SlotF(end.Stack.Length - 1));
        else { I(w, 0); I(w, 0); F(w, 0); } // Unreachable end after explicit returns.
        w.Write((byte)0x0b);

        void Result(int type, int i, int f) {
            I(w, 1);
            if (Wide(type)) { I(w, 0); Get(w, f); } else { Get(w, i); F(w, 0); }
            w.Write((byte)0x0f);
        }
        // number(value, type) of an f64 local into a variable of that type.
        void Narrow(int type, int f, int toI, int toF) {
            if (type == IntType) { Get(w, f); w.Write(new byte[] { 0xfc, 0x02 }); I(w, int.MinValue); InRange(f); w.Write((byte)0x1b); Set(w, toI); }
            else if (type == FloatType) { Get(w, f); w.Write(new byte[] { 0xb6, 0xbb }); Set(w, toF); }
            else { Get(w, f); Set(w, toF); }
        }
        void EmitInt(IrOperation op, int left, int right, int to) {
            if (op == IrOperation.Divide) {
                Get(w, right); w.Write((byte)0x45);
                Get(w, left); I(w, int.MinValue); w.Write((byte)0x46);
                Get(w, right); I(w, -1); w.Write(new byte[] { 0x46, 0x71, 0x72, 0x04, 0x7f });
                I(w, int.MinValue); w.Write((byte)0x05);
                Get(w, left); Get(w, right); w.Write(new byte[] { 0x6d, 0x0b }); Set(w, to); return;
            }
            Get(w, left); w.Write((byte)0xac); Get(w, right); w.Write((byte)0xac);
            w.Write(op == IrOperation.Add ? (byte)0x7c : op == IrOperation.Subtract ? (byte)0x7d : (byte)0x7e);
            CheckedWide(to);
        }
    }

    internal static byte Arithmetic(IrOperation op) => op switch {
        IrOperation.Add => 0xa0, IrOperation.Subtract => 0xa1, IrOperation.Multiply => 0xa2, IrOperation.Divide => 0xa3,
        _ => throw new InvalidOperationException("Invalid arithmetic operation.") };
    private static byte IntComparison(IrOperation op) => op switch {
        IrOperation.Less => 0x48, IrOperation.Greater => 0x4a, IrOperation.LessEqual => 0x4c, IrOperation.GreaterEqual => 0x4e,
        _ => throw new InvalidOperationException("Invalid comparison.") };
    private static byte FloatComparison(IrOperation op) => op switch {
        IrOperation.Less => 0x63, IrOperation.Greater => 0x64, IrOperation.LessEqual => 0x65, IrOperation.GreaterEqual => 0x66,
        _ => throw new InvalidOperationException("Invalid comparison.") };

    private static void U(BinaryWriter w, int n) => WasmCompiler.WriteUnsigned(w.BaseStream, checked((uint)n));
    private static void Signed(BinaryWriter w, long n) { bool more; do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more); }
    private static void I(BinaryWriter w, int n) { w.Write((byte)0x41); Signed(w, n); }
    private static void L(BinaryWriter w, long n) { w.Write((byte)0x42); Signed(w, n); }
    private static void F(BinaryWriter w, double v) { w.Write((byte)0x44); w.Write(v); }
    private static void Get(BinaryWriter w, int local) { w.Write((byte)0x20); U(w, local); }
    private static void Set(BinaryWriter w, int local) { w.Write((byte)0x21); U(w, local); }
    private static void Memory(BinaryWriter w, byte op, int align, int offset) { w.Write(op); U(w, align); U(w, offset); }
}
