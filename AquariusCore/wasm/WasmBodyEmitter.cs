using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;
using static AquariusLang.Wasm.RuntimeLayout;

namespace AquariusLang.Wasm;

/// <summary>
/// Emits one resumable function body. The operand stack has a static depth at every instruction, so
/// operands are addressed at constant frame offsets and kept in Wasm locals inside a basic block.
/// Memory is made consistent before every runtime helper (which may reach a host or collect), at every
/// block boundary and before returning to the scheduler, so suspension, collection and host projection
/// observe exactly the frame state of the generic representation. Promoted lexical scopes
/// (see <see cref="WasmScopePlan"/>) are private frame slots below the operand stack.
/// Operands and register slots whose tag is statically INTEGER use an i32 representation; the language's
/// integer rules (out-of-range results become the minimum integer, truncating division) are reproduced
/// exactly with i64 intermediates, and the f64 frame payload is written only when memory must be exact.
/// </summary>
internal sealed class WasmBodyEmitter
{
    private const int Unknown = WasmScopePlan.Unknown;
    private const double IntMin = -2147483648d, IntLimit = 2147483648d;

    private enum Loc : byte { Mem, Reg, Both }

    private readonly BinaryWriter w;
    private readonly LoweredProgram body;
    private readonly IReadOnlyDictionary<string, int> helpers;
    private readonly int pool;
    private readonly WasmScopePlan plan;
    private readonly int locals, capacity;
    // Wasm locals. Parameter 0 is the frame.
    private const int F = 0, PC = 1, A = 2, B = 3, C = 4, E = 5, G = 6;
    private readonly int tagBase, refBase, intBase, addressBase, wide, x, y, numberBase;
    private readonly Dictionary<int, int> addressLocals = new();
    private readonly Dictionary<int, int> slotRegisters = new();
    // Integer shadows of dynamic bindings (see Shadow): constant -> (valid flag, i32 value, binding value address) locals.
    private readonly Dictionary<int, (int Valid, int Value, int Address)> shadows = new();
    // Read shadows: dynamic bindings this body only reads, cached as a whole value (valid, tag, ref locals; f64 number local).
    private readonly Dictionary<int, (int Valid, int Tag, int Ref, int Number)> readShadows = new();
    private readonly int i32Count, f64Count;

    private Loc[] loc;
    private int nest, block, count, pc, fusedTruth = -1;
    private int[] starts = System.Array.Empty<int>();
    private bool[] deadTop = System.Array.Empty<bool>();

    private WasmBodyEmitter(BinaryWriter w, LoweredProgram body, IReadOnlyDictionary<string, int> helpers, int pool, WasmScopePlan plan, int capacity, bool shadowBindings)
    {
        this.w = w; this.body = body; this.helpers = helpers; this.pool = pool; this.plan = plan;
        locals = plan.Locals; this.capacity = capacity; loc = new Loc[capacity];
        int addresses = 0;
        for (int i = 0; i < body.Code.Length; i++)
            if (plan.Depth[i] >= 0 && plan.Accesses[i] is { } access && !access.Slots.Any(slot => plan.SlotDecl[i]![slot] == WasmScopePlan.Yes)
                && body.Code[i].Code is IrOperation.Load or IrOperation.Assign or IrOperation.CompoundAssign or IrOperation.IncrementPrefix or IrOperation.IncrementPostfix
                && !addressLocals.ContainsKey(body.Code[i].Operand) && addressLocals.Count < 64)
                addressLocals.Add(body.Code[i].Operand, addresses++);
        tagBase = 7; refBase = tagBase + capacity; intBase = refBase + capacity; addressBase = intBase + capacity;
        int next = addressBase + addresses;
        var registers = plan.RegisterSlots.OrderBy(p => p.Key).ToArray();
        foreach (var (slot, tag) in registers) if (tag == IntTag) slotRegisters.Add(slot, next++);
        if (shadowBindings) foreach (int constant in ShadowCandidates()) { shadows.Add(constant, (next, next + 1, next + 2)); next += 3; }
        var readOnly = shadowBindings ? ReadShadowCandidates() : System.Array.Empty<int>();
        foreach (int constant in readOnly) { readShadows.Add(constant, (next, next + 1, next + 2, 0)); next += 3; }
        i32Count = next - 1;
        wide = next++;
        x = next++; y = next++; numberBase = next; next += capacity;
        foreach (int constant in readOnly) readShadows[constant] = readShadows[constant] with { Number = next++ };
        foreach (var (slot, tag) in registers) if (tag != IntTag) slotRegisters.Add(slot, next++);
        f64Count = next - x;
    }


    /// <param name="shadowBindings">Keep integer accumulators of dynamic bindings in registers (off for the unoptimized reference).</param>
    internal static void Emit(BinaryWriter w, LoweredProgram body, IReadOnlyDictionary<string, int> helpers, int pool, WasmScopePlan plan, int capacity, bool shadowBindings = true)
        => new WasmBodyEmitter(w, body, helpers, pool, plan, capacity, shadowBindings).Emit();

    private int T(int p) => tagBase + p;
    private int R(int p) => refBase + p;
    private int I(int p) => intBase + p;
    private int N(int p) => numberBase + p;
    private int Address(int constant) => addressBase + addressLocals[constant];
    private int StackOffset(int p) => FrameValues + ValueSize * (locals + p);
    private static int SlotOffset(int slot) => FrameValues + ValueSize * slot;
    private bool IntRegister(int slot) => plan.RegisterSlots.TryGetValue(slot, out int tag) && tag == IntTag;

    // ---- encoding ------------------------------------------------------------------------------
    private void Op(params byte[] bytes) => w.Write(bytes);
    private void U(int n) => WasmEncoding.U(w, n);
    private void I32(int n) => WasmEncoding.I(w, n);
    private void F64(double v) => WasmEncoding.F(w, v);
    private void Get(int l) => WasmEncoding.Get(w, l);
    private void Set(int l) => WasmEncoding.Set(w, l);
    private void Tee(int l) => WasmEncoding.Tee(w, l);
    private void Mem(byte op, int align, int offset) => WasmEncoding.Memory(w, op, align, offset);
    private void LoadI32(int o) => Mem(0x28, 2, o);
    private void LoadF64(int o) => Mem(0x2b, 3, o);
    private void LoadI64(int o) => Mem(0x29, 3, o);
    private void StoreI32(int o) => Mem(0x36, 2, o);
    private void StoreF64(int o) => Mem(0x39, 3, o);
    private void StoreI64(int o) => Mem(0x37, 3, o);
    private void Call(string name) { w.Write((byte)0x10); U(helpers["rt_" + name]); }
    private void Drop() => w.Write((byte)0x1a);
    private void Return() => w.Write((byte)0x0f);
    private void If(byte type = 0x40) { w.Write((byte)0x04); w.Write(type); nest++; }
    private void Else() => w.Write((byte)0x05);
    private void End() { w.Write((byte)0x0b); nest--; }
    private void ToInt() => Op(0xfc, 0x02); // i32.trunc_sat_f64_s: exact for in-range integral payloads, never traps
    private void ToDouble() => Op(0xb7);   // f64.convert_i32_s

    private static bool HasRef(int tag) => tag is Unknown or (>= 5 and <= 10) or 13 or 14;
    private static bool IsNumeric(int tag) => tag is IntTag or FloatTag or DoubleTag;

    // ---- state -----------------------------------------------------------------------------------
    private int[] Tags(int at) => plan.StackTags[at] ?? throw new InvalidOperationException("Unreachable instruction.");
    private int TagAfter(int p) => plan.OutTags[pc] is { } t && p < t.Length ? t[p] : Unknown;

    private Loc[] Save() => (Loc[])loc.Clone();
    private void Restore(Loc[] s) => loc = (Loc[])s.Clone();
    private void Merge(Loc[] other)
    {
        for (int p = 0; p < loc.Length; p++) {
            Loc a = loc[p], b = other[p];
            if (a == b) continue;
            if (a == Loc.Mem || b == Loc.Mem) {
                if (a == Loc.Reg || b == Loc.Reg) throw new InvalidOperationException("Inconsistent operand location.");
                loc[p] = Loc.Mem;
            } else loc[p] = Loc.Reg;
        }
    }

    /// <summary>Emits a two-armed conditional (condition already on the Wasm stack) and merges the
    /// compile-time operand state. An arm that leaves the function does not contribute to the merge.
    /// Operands from <paramref name="deadFrom"/> upward are consumed by the operation in both arms.</summary>
    private void Branch(Func<bool> then, Func<bool>? otherwise, int deadFrom = int.MaxValue, byte type = 0x40)
    {
        var start = Save(); If(type);
        bool thenExits = then(); Forget(deadFrom); var afterThen = Save();
        Restore(start);
        bool elseExits = false;
        if (otherwise != null) { Else(); elseExits = otherwise(); }
        Forget(deadFrom);
        End();
        if (thenExits && elseExits) { Restore(afterThen); return; }
        if (thenExits) return;
        if (elseExits) { Restore(afterThen); return; }
        Merge(afterThen);
    }

    private void LoadValue(int p, int baseLocal, int offset, int tag)
    {
        if (tag == Unknown) { Get(baseLocal); LoadI32(offset); Set(T(p)); }
        if (HasRef(tag)) { Get(baseLocal); LoadI32(offset + ValueRef); Set(R(p)); }
        Get(baseLocal); LoadF64(offset + ValueNumber);
        if (tag == IntTag) { ToInt(); Set(I(p)); } else Set(N(p));
    }
    /// <summary>Pushes the f64 payload of operand p.</summary>
    private void Number(int p, int tag) { if (tag == IntTag) { Get(I(p)); ToDouble(); } else Get(N(p)); }
    private void StoreValue(int p, int baseLocal, int offset, int tag)
    {
        Get(baseLocal); if (tag == Unknown) Get(T(p)); else I32(tag); StoreI32(offset);
        Get(baseLocal); if (HasRef(tag)) Get(R(p)); else I32(0); StoreI32(offset + ValueRef);
        Get(baseLocal); Number(p, tag); StoreF64(offset + ValueNumber);
    }
    private void CopyMemory(int fromBase, int fromOffset, int toBase, int toOffset)
    {
        for (int o = 0; o <= 8; o += 8) { Get(toBase); Get(fromBase); LoadI64(fromOffset + o); StoreI64(toOffset + o); }
    }
    private void Ensure(int p, int tag)
    {
        if (loc[p] != Loc.Mem) return;
        LoadValue(p, F, StackOffset(p), tag); loc[p] = Loc.Both;
    }
    private void Spill(int p, int tag)
    {
        if (loc[p] != Loc.Reg) return;
        StoreValue(p, F, StackOffset(p), tag); loc[p] = Loc.Both;
    }
    private void SpillAll(int depth, int[]? tags = null)
    {
        tags ??= Tags(pc);
        for (int p = 0; p < depth; p++) Spill(p, p < tags.Length ? tags[p] : Unknown);
    }
    private void Forget(int from) { for (int p = Math.Max(0, from); p < loc.Length; p++) loc[p] = Loc.Mem; }

    private void StoreSp(int depth) { Get(F); I32(locals + depth); StoreI32(FrameSp); }
    private void Checked()
    {
        Get(F); LoadI32(FrameExecution); LoadI32(ExecutionStatus); I32(StatusFailed); Op(0x46);
        If(); ShadowWriteBack(); I32(-1); Return(); End();
    }
    private void Resume(int continuation) { I32(continuation); Return(); }

    /// <summary>Runtime helper protocol: every live operand is in memory and the frame sp is exact.</summary>
    private void Helper(string name, int operand, int depth, int consumes, bool check = true, int? third = null)
    {
        ShadowWriteBack(); SpillAll(depth); StoreSp(depth);
        Get(F); I32(operand); if (third is int l) Get(l); Call(name); Drop();
        Forget(depth - consumes); InvalidateAddresses();
        if (check) Checked();
    }

    private int BlockOf(int position) => System.Array.BinarySearch(starts, position);
    private void GoBlock(int target)
    {
        int b = BlockOf(target);
        if (b < 0) throw new InvalidOperationException("Invalid branch target.");
        if (b > block) { w.Write((byte)0x0c); U(b - block - 1 + nest); }
        else { I32(b); Set(PC); w.Write((byte)0x0c); U(count - block + nest); }
    }

    // ---- numbers -------------------------------------------------------------------------------
    private void InRange(int number)
    {
        Get(number); F64(IntMin); Op(0x66); Get(number); F64(IntLimit); Op(0x63, 0x71);
    }
    /// <summary>number(n, tag) for a known tag on an f64 local.</summary>
    private void NormalizeKnown(int number, int tag)
    {
        if (tag == IntTag) { Get(number); ToInt(); ToDouble(); F64(IntMin); InRange(number); Op(0x1b); Set(number); }
        else if (tag == FloatTag) { Get(number); Op(0xb6, 0xbb); Set(number); }
    }
    /// <summary>Pushes number(n, INTEGER) of an f64 local as i32.</summary>
    private void IntegerOf(int number) { Get(number); ToInt(); I32(int.MinValue); InRange(number); Op(0x1b); }
    private void NormalizeDynamic(int tag, int number)
    {
        Get(tag); I32(IntTag); Op(0x46); If();
        Get(number); ToInt(); ToDouble(); F64(IntMin); InRange(number); Op(0x1b); Set(number);
        Else(); Get(tag); I32(FloatTag); Op(0x46); If(); Get(number); Op(0xb6, 0xbb); Set(number); End();
        End();
    }
    private void NumericTagTest(int tagLocal) { Get(tagLocal); I32(IntTag); Op(0x4f); Get(tagLocal); I32(DoubleTag); Op(0x4d, 0x71); }
    private static byte Arithmetic(IrOperation op) => op switch {
        IrOperation.Add => 0xa0, IrOperation.Subtract => 0xa1, IrOperation.Multiply => 0xa2, IrOperation.Divide => 0xa3,
        _ => throw new InvalidOperationException("Invalid arithmetic operation.") };
    private static byte Comparison(IrOperation op) => op switch {
        IrOperation.Less => 0x63, IrOperation.Greater => 0x64, IrOperation.LessEqual => 0x65, IrOperation.GreaterEqual => 0x66,
        IrOperation.Equal => 0x61, IrOperation.NotEqual => 0x62, _ => throw new InvalidOperationException("Invalid comparison.") };
    private static byte IntComparison(IrOperation op) => op switch {
        IrOperation.Less => 0x48, IrOperation.Greater => 0x4a, IrOperation.LessEqual => 0x4c, IrOperation.GreaterEqual => 0x4e,
        IrOperation.Equal => 0x46, IrOperation.NotEqual => 0x47, _ => throw new InvalidOperationException("Invalid comparison.") };
    private static string HelperName(IrOperation op) => char.ToLowerInvariant(op.ToString()[0]) + op.ToString()[1..];

    /// <summary>Integer add, subtract and multiply of two i32 values on the Wasm stack: the exact i64
    /// result, or the minimum integer when it is out of range.</summary>
    private void IntArithmetic(IrOperation op, Action left, Action right)
    {
        left(); Op(0xac); right(); Op(0xac);
        Op(op switch { IrOperation.Add => (byte)0x7c, IrOperation.Subtract => (byte)0x7d, IrOperation.Multiply => (byte)0x7e,
            _ => throw new InvalidOperationException("Invalid integer operation.") });
        Set(wide);
        Get(wide); Op(0xa7); I32(int.MinValue); Get(wide); Get(wide); Op(0xa7, 0xac, 0x51); Op(0x1b);
    }
    /// <summary>Truncating integer division; a zero divisor or minimum integer divided by -1 is out of range.</summary>
    private void IntDivide(int left, int right)
    {
        Get(right); Op(0x45); Get(left); I32(int.MinValue); Op(0x46); Get(right); I32(-1); Op(0x46, 0x71, 0x72);
        If(0x7f); I32(int.MinValue); Else(); Get(left); Get(right); Op(0x6d); End();
    }
    /// <summary>Integer remainder for a non-zero divisor (zero is the helper's run-time error).</summary>
    private void IntRemainder(int left, int right)
    {
        Get(right); I32(-1); Op(0x46); If(0x7f); I32(0); Else(); Get(left); Get(right); Op(0x6f); End();
    }
    /// <summary>Float remainder: x - trunc(x / y) * y on f64 locals.</summary>
    private void FloatRemainder(int left, int right) { Get(left); Get(left); Get(right); Op(0xa3); Op(0x9d); Get(right); Op(0xa2); Op(0xa1); }

    // ---- named bindings ------------------------------------------------------------------------
    /// <summary>Address of the guarded dynamic lexical candidate or 0 (no failure, no helper).</summary>
    private void CandidatePointer(int constant, WasmBindingSlots.Candidate candidate)
    {
        Get(F); LoadI32(FrameScope); Set(C);
        for (int depth = 0; depth < candidate.Depth; depth++) {
            Get(C); LoadI32(ScopeCaptured); Op(0x45); If(0x7f); Get(C); LoadI32(ScopeOuter); Set(C);
        }
        Get(C); LoadI32(ScopeCount); I32(candidate.Slot); Op(0x4b); If(0x7f);
        Get(C); LoadI32(ScopeBindings); I32(checked(candidate.Slot * BindingSize)); Op(0x6a); Set(B);
        Get(B); LoadI32(BindingName); I32(0); LoadI32(checked(pool + constant * ValueSize + ValueRef)); Op(0x46);
        If(0x7f); Get(B); I32(BindingValue); Op(0x6a); Else(); I32(0); End();
        Else(); I32(0); End();
        for (int depth = 0; depth < candidate.Depth; depth++) { Else(); I32(0); End(); }
    }
    /// <summary>Leaves the value address of a dynamic binding in local A, failing like rt_binding.</summary>
    private void DynamicAddress(int constant, WasmBindingSlots.Candidate? candidate)
    {
        LookupAddress(constant, candidate);
        Get(A); Op(0x45); If(); Get(F); I32(constant); Call("binding"); Set(A); Checked(); End();
    }

    // Positive binding addresses survive control-flow edges, but never a helper or a call: a
    // declaration can grow a binding buffer, and a callback can introduce a shadowing binding.
    // Wasm locals start at zero on every entry/resumption; GC does not move live scope buffers.
    private void InvalidateAddresses()
    {
        foreach (int constant in addressLocals.Keys) { I32(0); Set(Address(constant)); }
    }

    // ---- integer shadows of dynamic bindings ------------------------------------------------------
    // A dynamic binding this body updates with ++/compound assignment (an accumulator in a module or closure scope) is
    // kept in an i32 local while it holds an integer. Nothing else can observe the binding between barriers: helpers,
    // calls, returns, failures and yields write the shadow back (only the number: the tag stays INTEGER) and drop it,
    // and every access to the name in this body goes through the shadow. Locals start at zero on every entry, so a
    // resumption simply re-acquires.
    private IEnumerable<int> ShadowCandidates()
    {
        var updated = new HashSet<int>(); var excluded = new HashSet<int>();
        for (int i = 0; i < body.Code.Length; i++) {
            if (plan.Depth[i] < 0) continue;
            var op = body.Code[i];
            if (op.Code is not (IrOperation.Load or IrOperation.Assign or IrOperation.CompoundAssign or IrOperation.IncrementPrefix or IrOperation.IncrementPostfix)) continue;
            int key = NameConstant(op.Operand); var access = plan.Accesses[i];
            bool dynamic = access != null && access.Slots.All(slot => plan.SlotDecl[i]![slot] == WasmScopePlan.No);
            if (!dynamic) excluded.Add(key);
            else if (op.Code is IrOperation.IncrementPrefix or IrOperation.IncrementPostfix ||
                     op.Code == IrOperation.CompoundAssign && ((Assignment)body.Pool[op.Operand]).Operation != IrOperation.Modulo)
                updated.Add(key);
        }
        return updated.Where(c => !excluded.Contains(c)).OrderBy(c => c).Take(8).ToArray();
    }
    // Dynamic bindings this body loads at least twice and never assigns: between barriers they cannot change, so the
    // whole value is cached in locals and loads read the locals.
    private IEnumerable<int> ReadShadowCandidates()
    {
        var loads = new Dictionary<int, int>(); var excluded = new HashSet<int>(shadows.Keys);
        for (int i = 0; i < body.Code.Length; i++) {
            if (plan.Depth[i] < 0) continue;
            var op = body.Code[i];
            if (op.Code is not (IrOperation.Load or IrOperation.Assign or IrOperation.CompoundAssign or IrOperation.IncrementPrefix or IrOperation.IncrementPostfix)) continue;
            int key = NameConstant(op.Operand); var access = plan.Accesses[i];
            bool dynamic = access != null && access.Slots.All(slot => plan.SlotDecl[i]![slot] == WasmScopePlan.No);
            if (op.Code != IrOperation.Load || !dynamic) excluded.Add(key); else loads[key] = loads.GetValueOrDefault(key) + 1;
        }
        return loads.Where(p => p.Value >= 2 && !excluded.Contains(p.Key)).Select(p => p.Key).OrderBy(c => c).Take(8).ToArray();
    }
    // Loads, assignments and increments name a binding by a string constant; compound assignments by an Assignment
    // constant. Shadows are keyed by the string constant (or by a key no plain access shares).
    private int NameConstant(int constant)
    {
        if (body.Pool[constant] is not Assignment assignment) return constant;
        for (int c = 0; c < body.Pool.Length; c++) if (body.Pool[c] is string name && name == assignment.Name) return c;
        return -1 - constant;
    }
    private bool Shadowed(int constant, out (int Valid, int Value, int Address) shadow) => shadows.TryGetValue(NameConstant(constant), out shadow);
    /// <summary>Makes the shadow valid when the binding currently holds an integer (looking it up if necessary).</summary>
    private void ShadowAcquire(int constant, (int Valid, int Value, int Address) shadow, WasmBindingSlots.Candidate? candidate)
    {
        Get(shadow.Valid); Op(0x45); If();
        LookupAddress(constant, candidate);
        Get(A); If(); Get(A); LoadI32(ValueTag); I32(IntTag); Op(0x46); If();
        Get(A); Set(shadow.Address); Get(A); LoadF64(ValueNumber); ToInt(); Set(shadow.Value); I32(1); Set(shadow.Valid);
        End(); End();
        End();
    }
    /// <summary>Writes every valid shadow back to its binding and drops it; read shadows are dropped.</summary>
    private void ShadowWriteBack()
    {
        foreach (var shadow in shadows.Values) {
            Get(shadow.Valid); If(); Get(shadow.Address); Get(shadow.Value); ToDouble(); StoreF64(ValueNumber); I32(0); Set(shadow.Valid); End();
        }
        foreach (var shadow in readShadows.Values) { I32(0); Set(shadow.Valid); }
    }

    private void LookupAddress(int constant, WasmBindingSlots.Candidate? candidate)
    {
        bool cache = addressLocals.ContainsKey(constant);
        if (cache) { Get(Address(constant)); Tee(A); Op(0x45); If(); }
        if (candidate != null) {
            CandidatePointer(constant, candidate); Set(A);
            Get(A); Op(0x45); If(); Get(F); I32(constant); Call("lookup"); Set(A); End();
        } else { Get(F); I32(constant); Call("lookup"); Set(A); }
        if (cache) { Get(A); Set(Address(constant)); End(); }
    }
    /// <summary>Leaves the value address of the resolved target of an assignment-like access in local A.</summary>
    private void TargetAddress(WasmScopePlan.Access access, int constant)
    {
        var decl = plan.SlotDecl[pc]!;
        var slots = access.Slots.Where(s => decl[s] != WasmScopePlan.No).ToArray();
        void Chain(int index) {
            if (index == slots.Length) { DynamicAddress(constant, access.Dynamic); return; }
            int slot = slots[index];
            if (decl[slot] == WasmScopePlan.Yes) { Get(F); I32(SlotOffset(slot)); Op(0x6a); Set(A); return; }
            Get(F); LoadI32(SlotOffset(slot)); I32(-1); Op(0x47);
            Branch(() => { Get(F); I32(SlotOffset(slot)); Op(0x6a); Set(A); return false; }, () => { Chain(index + 1); return false; });
        }
        Chain(0);
    }
    /// <summary>The single definitely-declared promoted slot that an access resolves to, if any.</summary>
    private int DefiniteSlot(WasmScopePlan.Access access)
    {
        var decl = plan.SlotDecl[pc]!;
        foreach (int slot in access.Slots) {
            if (decl[slot] == WasmScopePlan.Yes) return slot;
            if (decl[slot] == WasmScopePlan.Maybe) return -1;
        }
        return -1;
    }

    private void SetRegister(int slot, int register, int p, int tag)
    {
        bool integer = IntRegister(slot);
        if (loc[p] == Loc.Mem) { Get(F); LoadF64(StackOffset(p) + ValueNumber); if (integer) ToInt(); }
        else if (integer) Get(I(p));
        else Number(p, tag);
        Set(register);
    }
    private void StoreEntryAt(int p, int baseLocal, int offset, int tag)
    {
        if (loc[p] == Loc.Mem) CopyMemory(F, StackOffset(p), baseLocal, offset);
        else StoreValue(p, baseLocal, offset, tag);
    }
    private void PushKnown(int p, int tag, double number = 0)
    {
        if (tag == Unknown) throw new InvalidOperationException("Known operand requires a tag.");
        if (tag == IntTag) { I32((int)number); Set(I(p)); } else { F64(number); Set(N(p)); }
        loc[p] = Loc.Reg;
    }

    private void Load(int constant, WasmScopePlan.Access access, int d)
    {
        var decl = plan.SlotDecl[pc]!; var slotTags = plan.SlotTags[pc]!;
        var slots = access.Slots.Where(s => decl[s] != WasmScopePlan.No).ToArray();
        int resultTag = TagAfter(d);
        void Chain(int index) {
            if (index == slots.Length) { DynamicLoad(constant, access, d, resultTag); return; }
            int slot = slots[index];
            if (decl[slot] == WasmScopePlan.Yes) {
                if (slotRegisters.TryGetValue(slot, out int register)) {
                    if (resultTag != plan.RegisterSlots[slot]) throw new InvalidOperationException("Register slot tag mismatch.");
                    Get(register); Set(resultTag == IntTag ? I(d) : N(d)); loc[d] = Loc.Reg; return;
                }
                LoadValue(d, F, SlotOffset(slot), resultTag == Unknown ? Unknown : slotTags[slot]); loc[d] = Loc.Reg; return;
            }
            if (resultTag != Unknown) throw new InvalidOperationException("Uncertain loads have unknown tags.");
            Get(F); LoadI32(SlotOffset(slot)); I32(-1); Op(0x47);
            Branch(() => { LoadValue(d, F, SlotOffset(slot), Unknown); loc[d] = Loc.Reg; return false; }, () => { Chain(index + 1); return false; });
        }
        Chain(0);
    }
    private void DynamicLoad(int constant, WasmScopePlan.Access access, int d, int resultTag)
    {
        if (resultTag != Unknown) throw new InvalidOperationException("Dynamic loads have unknown tags.");
        bool Memory() {
            LookupAddress(constant, access.Dynamic); Get(A);
            Branch(() => { LoadValue(d, A, 0, Unknown); loc[d] = Loc.Reg; return false; },
                () => { Helper("load", constant, d, 0); LoadValue(d, F, StackOffset(d), Unknown); loc[d] = Loc.Both; return false; });
            return false;
        }
        if (readShadows.TryGetValue(constant, out var cached)) {
            Get(cached.Valid); Op(0x45); If();
            LookupAddress(constant, access.Dynamic);
            Get(A); If(); Get(A); LoadI32(ValueTag); Set(cached.Tag); Get(A); LoadI32(ValueRef); Set(cached.Ref); Get(A); LoadF64(ValueNumber); Set(cached.Number); I32(1); Set(cached.Valid); End();
            End();
            Get(cached.Valid);
            Branch(() => { Get(cached.Tag); Set(T(d)); Get(cached.Ref); Set(R(d)); Get(cached.Number); Set(N(d)); loc[d] = Loc.Reg; return false; }, Memory);
            return;
        }
        if (!Shadowed(constant, out var shadow)) { Memory(); return; }
        ShadowAcquire(constant, shadow, access.Dynamic); Get(shadow.Valid);
        Branch(() => { I32(IntTag); Set(T(d)); I32(0); Set(R(d)); Get(shadow.Value); ToDouble(); Set(N(d)); loc[d] = Loc.Reg; return false; }, Memory);
    }

    // ---- emission --------------------------------------------------------------------------------
    /// <summary>Basic block starts of the emitted body; block indices are the resumable continuations.</summary>
    internal static int[] BlockStarts(LoweredProgram body, WasmScopePlan plan)
    {
        var code = body.Code; int n = code.Length;
        var leaders = new SortedSet<int> { 0, n };
        foreach (int position in WasmFuel.Checkpoints(body).Keys) leaders.Add(position);
        for (int i = 0; i < n; i++) {
            if (plan.Depth[i] < 0) continue;
            var op = code[i];
            switch (op.Code) {
                case IrOperation.Jump: case IrOperation.Break: case IrOperation.JumpIfFalse: case IrOperation.LoopCondition:
                    leaders.Add(op.Operand); leaders.Add(i + 1); break;
                case IrOperation.JumpIfBreak: {
                    int top = plan.StackTags[i]![^1];
                    if (top == Unknown || top == BreakTag) { leaders.Add(op.Operand); leaders.Add(i + 1); }
                    break;
                }
                case IrOperation.Call: case IrOperation.ResolveMemberFunction: case IrOperation.Return: case IrOperation.Error:
                    leaders.Add(i + 1); break;
            }
        }
        return leaders.ToArray();
    }

    private void Emit()
    {
        var code = body.Code; int n = code.Length;
        var fuel = WasmFuel.Checkpoints(body);
        starts = BlockStarts(body, plan); count = starts.Length;
        var checkpointBlocks = fuel.ToDictionary(p => BlockOf(p.Key), p => p.Value);
        deadTop = new bool[count];
        for (int b = 0; b < count; b++) {
            int i = starts[b];
            while (i < n && code[i].Code == IrOperation.JumpIfBreak && plan.Depth[i] >= 0 && plan.StackTags[i]![^1] is not (Unknown or BreakTag)) i++;
            deadTop[b] = i < n && plan.Depth[i] >= 0 && code[i].Code == IrOperation.Pop && (BlockOf(i) < 0 || i == starts[b]);
        }

        U(3); U(i32Count); w.Write((byte)0x7f); U(1); w.Write((byte)0x7e); U(f64Count); w.Write((byte)0x7c);
        Get(F); LoadI32(FramePc); Set(PC);
        // Register slots are loaded on every entry (call or resumption) and stored before any resumable exit.
        foreach (var (slot, register) in slotRegisters) { Get(F); LoadF64(SlotOffset(slot) + ValueNumber); if (IntRegister(slot)) ToInt(); Set(register); }
        Op(0x03, 0x40); Op(0x02, 0x40);
        for (int b = count - 1; b >= 0; b--) Op(0x02, 0x40);
        Get(PC); w.Write((byte)0x0e); U(count); for (int b = 0; b < count; b++) U(b); U(count);
        for (block = 0; block < count; block++) {
            w.Write((byte)0x0b); nest = 0;
            int start = starts[block];
            int depth = plan.Depth[start];
            if (depth < 0) { w.Write((byte)0x00); continue; }
            System.Array.Fill(loc, Loc.Mem); fusedTruth = -1;
            if (start == n) { pc = n; StoreSp(depth); ShadowWriteBack(); Resume(-1); continue; }
            if (checkpointBlocks.TryGetValue(block, out int weight)) Checkpoint(depth, weight);
            bool terminated = false;
            for (pc = start; pc < starts[block + 1]; pc++) {
                terminated = Instruction(code[pc]);
                if (terminated) break;
            }
            if (!terminated) {
                // Fall through into the next block with all live operands in memory.
                int last = starts[block + 1] - 1, d = plan.Depth[last + 1];
                if (d >= 0) { pc = last; SpillEdge(d, plan.OutTags[last], last + 1); }
            }
        }
        Op(0x0b, 0x00, 0x0b, 0x00, 0x0b);
    }

    private void Checkpoint(int depth, int weight)
    {
        // Inline fuel (see WasmFuel): only an exhausted budget or a pending collection calls the runtime.
        Get(F); LoadI32(FrameExecution); Set(E);
        Get(E); LoadI32(ExecutionBudget); Tee(G); I32(weight); Op(0x4b);
        Get(E); LoadI32(ExecutionPoll); Op(0x45); Op(0x71);
        If(); Get(E); Get(G); I32(weight); Op(0x6b); StoreI32(ExecutionBudget);
        Else(); StoreSp(depth); WriteBack(); ShadowWriteBack(); Get(F); I32(weight); Call("checkpoint"); If(); Resume(block); End();
        End();
    }

    private void WriteBack() { foreach (int slot in slotRegisters.Keys) WriteBack(slot); }
    private void WriteBack(int slot)
    {
        if (!slotRegisters.TryGetValue(slot, out int r)) return;
        Get(F); Get(r); if (IntRegister(slot)) ToDouble(); StoreF64(SlotOffset(slot) + ValueNumber);
    }
    private void Reload(int slot)
    {
        if (!slotRegisters.TryGetValue(slot, out int r)) return;
        Get(F); LoadF64(SlotOffset(slot) + ValueNumber); if (IntRegister(slot)) ToInt(); Set(r);
    }

    /// <summary>Spills operands for an edge into the block at target; a block value that the target only pops is dead.</summary>
    private void SpillEdge(int depth, int[]? tags, params int[] targets)
    {
        tags ??= Tags(pc);
        bool dead = depth > 0 && targets.All(t => BlockOf(t) is int b && b >= 0 && deadTop[b] && plan.Depth[t] == depth);
        for (int p = 0; p < depth; p++) if (!(dead && p == depth - 1)) Spill(p, p < tags.Length ? tags[p] : Unknown);
    }

    /// <summary>Returns true if the instruction ends the block.</summary>
    private bool Instruction(Instruction op)
    {
        int d = plan.Depth[pc], n = op.Operand;
        var tags = Tags(pc);
        switch (op.Code) {
            case IrOperation.Constant: {
                int tag = TagAfter(d);
                switch (body.Pool[n]) {
                    case IntegerObj v: PushKnown(d, IntTag, v.Value); break;
                    case FloatObj v: PushKnown(d, FloatTag, v.Value); break;
                    case DoubleObj v: PushKnown(d, DoubleTag, v.Value); break;
                    case BooleanObj v: PushKnown(d, BoolTag, v.Value ? 1 : 0); break;
                    case NullObj: PushKnown(d, NullTag); break;
                    default:
                        I32(0); Set(A); LoadValue(d, A, checked(pool + n * ValueSize), tag); loc[d] = Loc.Reg; break;
                }
                return false;
            }
            case IrOperation.Void: PushKnown(d, VoidTag); return false;
            case IrOperation.Null: PushKnown(d, NullTag); return false;
            case IrOperation.Pop: loc[d - 1] = Loc.Mem; return false;
            case IrOperation.Duplicate: {
                int tag = tags[d - 1]; Ensure(d - 1, tag);
                if (tag == Unknown) { Get(T(d - 1)); Set(T(d)); }
                if (HasRef(tag)) { Get(R(d - 1)); Set(R(d)); }
                if (tag == IntTag) { Get(I(d - 1)); Set(I(d)); } else { Get(N(d - 1)); Set(N(d)); }
                loc[d] = Loc.Reg; return false;
            }
            case IrOperation.Load: Load(n, plan.Accesses[pc]!, d); return false;
            case IrOperation.Declare: {
                int slot = plan.DeclareSlot[pc];
                if (slot < 0) { Helper("declare", n, d, 1); return false; }
                if (slotRegisters.TryGetValue(slot, out int register)) {
                    SetRegister(slot, register, d - 1, tags[d - 1]); Get(F); I32(plan.RegisterSlots[slot]); StoreI32(SlotOffset(slot));
                } else StoreEntryAt(d - 1, F, SlotOffset(slot), tags[d - 1]);
                PushKnown(d - 1, VoidTag); return false;
            }
            case IrOperation.Assign: {
                var access = plan.Accesses[pc]!; int value = d - 1, tag = tags[value];
                int slot = DefiniteSlot(access);
                if (slot >= 0 && slotRegisters.TryGetValue(slot, out int register)) SetRegister(slot, register, value, tag);
                else if (slot >= 0) StoreEntryAt(value, F, SlotOffset(slot), tag);
                else { if (Shadowed(n, out var shadow)) { I32(0); Set(shadow.Valid); } TargetAddress(access, n); StoreEntryAt(value, A, 0, tag); }
                PushKnown(d - 2, VoidTag); loc[d - 1] = Loc.Mem; return false;
            }
            case IrOperation.CompoundAssign: CompoundAssign(n, d); return false;
            case IrOperation.IncrementPrefix: case IrOperation.IncrementPostfix: Increment(n, d, op.Code == IrOperation.IncrementPrefix); return false;
            case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: case IrOperation.Modulo:
            case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
            case IrOperation.Equal: case IrOperation.NotEqual: case IrOperation.And: case IrOperation.Or:
                Binary(op.Code, d); return false;
            case IrOperation.Negate: Negate(d); return false;
            case IrOperation.Not: {
                int p = d - 1, tag = tags[p]; Ensure(p, tag);
                if (tag == BoolTag) { Get(N(p)); F64(0); Op(0x61); }
                else if (tag == Unknown) { Get(N(p)); F64(0); Op(0x61); Get(T(p)); I32(NullTag); Op(0x46); Get(T(p)); I32(BoolTag); Op(0x46); Op(0x1b); }
                else I32(tag == NullTag ? 1 : 0);
                Op(0xb8); Set(N(p)); loc[p] = Loc.Reg; return false;
            }
            case IrOperation.Index: Index(d); return false;
            case IrOperation.CheckArrayWrite: CheckArrayWrite(d); return false;
            case IrOperation.WriteIndex: WriteIndex(d); return false;
            case IrOperation.Closure: Helper("closure", n, d, 0); return false;
            case IrOperation.Array: Helper("array", n, d, n); return false;
            case IrOperation.Hash: Helper("hash", n, d, n * 2); return false;
            case IrOperation.CheckHashKey: Helper("checkHashKey", n, d, 0); return false;
            case IrOperation.Member: case IrOperation.MemberFunction: Helper("member", n, d, 1); return false;
            case IrOperation.EnterLoop: {
                int child = plan.ScopeAt[pc + 1];
                if (!plan.Scopes[child].Promoted) { Helper("enterLoop", n, d, 0); return false; }
                ClearSlots(child, -1); return false;
            }
            case IrOperation.NextIteration: {
                int s = plan.ScopeAt[pc]; var scope = plan.Scopes[s];
                if (!scope.Promoted) { Helper("nextIteration", n, d, 0); return false; }
                ClearSlots(s, scope.LoopName is string name ? scope.Slots[name] : -1); return false;
            }
            case IrOperation.LeaveLoop: {
                int s = plan.ScopeAt[pc];
                if (!plan.Scopes[s].Promoted) { Helper("leaveLoop", n, d, 0); return false; }
                ClearSlots(s, -1); Forget(d); PushKnown(d, VoidTag); return false;
            }
            case IrOperation.Break: {
                int b = plan.LoopBase[pc]; Forget(b); SpillEdge(b, null, n); GoBlock(n); return true;
            }
            case IrOperation.Jump: SpillEdge(d, null, n); GoBlock(n); return true;
            case IrOperation.JumpIfFalse: case IrOperation.LoopCondition: {
                int p = d - 1, tag = tags[p];
                if (fusedTruth == p) {
                    fusedTruth = -1; loc[p] = Loc.Mem; SpillEdge(p, null, n, pc + 1);
                    Get(A); Op(0x45); If(); GoBlock(n); End();
                    GoBlock(pc + 1); return true;
                }
                Ensure(p, tag);
                if (op.Code == IrOperation.LoopCondition && tag != BoolTag) {
                    if (tag != Unknown) { Helper("loopCondition", 0, d, 1); Resume(-1); return true; }
                    Get(T(p)); I32(BoolTag); Op(0x47);
                    Branch(() => { Helper("loopCondition", 0, d, 1); Resume(-1); return true; }, null);
                }
                Truth(p, op.Code == IrOperation.LoopCondition ? BoolTag : tag); Set(A);
                loc[p] = Loc.Mem; SpillEdge(p, null, n, pc + 1);
                Get(A); Op(0x45); If(); GoBlock(n); End();
                GoBlock(pc + 1); return true;
            }
            case IrOperation.JumpIfBreak: {
                int p = d - 1, tag = tags[p];
                if (tag != Unknown && tag != BreakTag) return false;
                if (tag == BreakTag) { SpillEdge(d, null, n); GoBlock(n); return true; }
                Ensure(p, tag); Get(T(p)); I32(BreakTag); Op(0x46); Set(A);
                SpillEdge(d, null, n, pc + 1); Get(A); If(); GoBlock(n); End(); GoBlock(pc + 1); return true;
            }
            case IrOperation.Return:
                ShadowWriteBack(); SpillAll(d); StoreSp(d); Get(F); I32(1); StoreI32(FrameReturned); Resume(-1); return true;
            case IrOperation.Error: Helper("error", n, d, 0, false); Resume(-1); return true;
            case IrOperation.Call:
                ShadowWriteBack(); SpillAll(d); StoreSp(d); Get(F); I32(n); Call("call");
                If(); WriteBack(); Resume(BlockOf(pc + 1)); End(); Forget(d - n - 1); InvalidateAddresses();
                GoBlock(pc + 1); return true;
            case IrOperation.ResolveMemberFunction:
                Helper("resolveMemberFunction", n, d, 1, false); WriteBack(); Resume(BlockOf(pc + 1)); return true;
            default: throw new InvalidOperationException($"Unsupported instruction {op.Code}.");
        }
    }

    private void ClearSlots(int scope, int keep)
    {
        foreach (int slot in plan.Scopes[scope].SlotList) if (slot != keep) { Get(F); I32(-1); StoreI32(SlotOffset(slot)); }
    }

    /// <summary>Language truthiness of operand p as an i32 on the Wasm stack.</summary>
    private void Truth(int p, int tag)
    {
        if (tag == BoolTag) { Get(N(p)); F64(0); Op(0x62); return; }
        if (tag == Unknown) {
            Get(N(p)); F64(0); Op(0x62); Get(T(p)); I32(NullTag); Op(0x47); Get(T(p)); I32(BoolTag); Op(0x46); Op(0x1b); return;
        }
        I32(tag == NullTag ? 0 : 1);
    }

    private bool Fusable() => nest == 0 && pc + 1 < body.Code.Length && body.Code[pc + 1].Code is IrOperation.JumpIfFalse or IrOperation.LoopCondition
        && BlockOf(pc + 1) < 0;

    private void Binary(IrOperation op, int d)
    {
        int a = d - 2, b = d - 1;
        var tags = Tags(pc); int ta = tags[a], tb = tags[b], result = TagAfter(a);
        Ensure(a, ta); Ensure(b, tb);
        bool comparison = op is IrOperation.Less or IrOperation.Greater or IrOperation.LessEqual or IrOperation.GreaterEqual or IrOperation.Equal or IrOperation.NotEqual;
        void Slow() {
            Helper(HelperName(op), 0, d, 2);
            LoadValue(a, F, StackOffset(a), result); loc[a] = Loc.Both;
        }
        if (op is IrOperation.And or IrOperation.Or) {
            void Logic() {
                Get(N(a)); F64(0); Op(0x62); Get(N(b)); F64(0); Op(0x62); Op(op == IrOperation.And ? (byte)0x71 : (byte)0x72); Op(0xb8); Set(N(a));
                loc[a] = Loc.Reg; loc[b] = Loc.Mem;
            }
            if (ta == BoolTag && tb == BoolTag) { Logic(); return; }
            if (ta is not (BoolTag or Unknown) || tb is not (BoolTag or Unknown)) { Slow(); return; }
            BoolTest(a, ta); BoolTest(b, tb); Op(0x71);
            Branch(() => { Logic(); return false; }, () => { Slow(); return false; }, b);
            return;
        }
        if (IsNumeric(ta) && IsNumeric(tb)) {
            int tag = Math.Max(ta, tb);
            if (op == IrOperation.Modulo && tag == IntTag) {
                Get(I(b)); Op(0x45, 0x45);
                Branch(() => { IntRemainder(I(a), I(b)); Set(I(a)); loc[a] = Loc.Reg; return false; }, () => { Slow(); return false; }, b);
                return;
            }
            KnownNumeric(op, a, b, ta, tb, comparison); return;
        }
        if (op == IrOperation.Modulo || ta != Unknown && !IsNumeric(ta) || tb != Unknown && !IsNumeric(tb)) { Slow(); loc[b] = Loc.Mem; return; }
        if (ta == Unknown) NumericTagTest(T(a)); else I32(1);
        if (tb == Unknown) { NumericTagTest(T(b)); Op(0x71); }
        Branch(() => {
            if (comparison) {
                Number(a, ta); Number(b, tb); Op(Comparison(op)); Op(0xb8); Set(N(a)); loc[a] = Loc.Reg; return false;
            }
            // Dynamic result tag: the wider numeric operand type.
            if (ta == Unknown && tb == Unknown) { Get(T(a)); Get(T(b)); Get(T(a)); Get(T(b)); Op(0x4b); Op(0x1b); Set(G); }
            else if (ta == Unknown) { Get(T(a)); I32(tb); Get(T(a)); I32(tb); Op(0x4b); Op(0x1b); Set(G); }
            else { I32(ta); Get(T(b)); I32(ta); Get(T(b)); Op(0x4b); Op(0x1b); Set(G); }
            Number(a, ta); Number(b, tb); Op(Arithmetic(op)); Set(N(a)); Get(G); Set(T(a)); NormalizeDynamic(T(a), N(a));
            loc[a] = Loc.Reg; return false;
        }, () => { Slow(); return false; }, b);
    }
    private void BoolTest(int p, int tag) { if (tag == BoolTag) I32(1); else { Get(T(p)); I32(BoolTag); Op(0x46); } }
    private void KnownNumeric(IrOperation op, int a, int b, int ta, int tb, bool comparison)
    {
        bool integers = ta == IntTag && tb == IntTag; int tag = Math.Max(ta, tb);
        if (comparison) {
            if (integers) { Get(I(a)); Get(I(b)); Op(IntComparison(op)); }
            else { Number(a, ta); Number(b, tb); Op(Comparison(op)); }
            // A comparison consumed by the next conditional branch stays an i32 truth value in local A.
            if (Fusable()) { Set(A); fusedTruth = a; }
            else { Op(0xb8); Set(N(a)); }
            loc[a] = Loc.Reg; loc[b] = Loc.Mem; return;
        }
        if (integers) {
            if (op == IrOperation.Divide) IntDivide(I(a), I(b));
            else IntArithmetic(op, () => Get(I(a)), () => Get(I(b)));
            Set(I(a));
        } else {
            Number(a, ta); Set(x); Number(b, tb); Set(y);
            if (op == IrOperation.Modulo) FloatRemainder(x, y); else { Get(x); Get(y); Op(Arithmetic(op)); }
            Set(N(a)); NormalizeKnown(N(a), tag);
        }
        loc[a] = Loc.Reg; loc[b] = Loc.Mem;
    }

    private void Negate(int d)
    {
        int p = d - 1, tag = Tags(pc)[p]; Ensure(p, tag);
        if (tag == IntTag) {
            Get(I(p)); I32(int.MinValue); Op(0x46); If(0x7f); I32(int.MinValue); Else(); I32(0); Get(I(p)); Op(0x6b); End(); Set(I(p));
            loc[p] = Loc.Reg; return;
        }
        if (IsNumeric(tag)) { Get(N(p)); Op(0x9a); Set(N(p)); NormalizeKnown(N(p), tag); loc[p] = Loc.Reg; return; }
        void Slow() { Helper("negate", 0, d, 1); LoadValue(p, F, StackOffset(p), Unknown); loc[p] = Loc.Both; }
        if (tag != Unknown) { Slow(); return; }
        NumericTagTest(T(p));
        Branch(() => { Get(N(p)); Op(0x9a); Set(N(p)); NormalizeDynamic(T(p), N(p)); loc[p] = Loc.Reg; return false; }, () => { Slow(); return false; });
    }

    /// <summary>Re-reads the target after the right operand like rt_compoundAssign and preserves the
    /// left operand's numeric type.</summary>
    private void CompoundAssign(int constant, int d)
    {
        var assignment = (Assignment)body.Pool[constant]; var access = plan.Accesses[pc]!;
        int rhs = d - 1, rt = Tags(pc)[rhs]; var operation = assignment.Operation;
        int slot = DefiniteSlot(access); int st = slot >= 0 ? plan.SlotTags[pc]![slot] : Unknown;
        Ensure(rhs, rt);
        if (slot >= 0 && IsNumeric(st) && IsNumeric(rt) && !(operation == IrOperation.Modulo && st == IntTag)) {
            bool cached = slotRegisters.TryGetValue(slot, out int register);
            if (cached && st == IntTag && rt == IntTag) {
                if (operation == IrOperation.Divide) IntDivide(register, I(rhs));
                else IntArithmetic(operation, () => Get(register), () => Get(I(rhs)));
                Set(register);
            } else {
                if (cached) { Get(register); if (st == IntTag) ToDouble(); } else { Get(F); LoadF64(SlotOffset(slot) + ValueNumber); }
                Set(x); Number(rhs, rt); Set(y);
                if (operation == IrOperation.Modulo) FloatRemainder(x, y); else { Get(x); Get(y); Op(Arithmetic(operation)); }
                Set(x);
                if (cached && st == IntTag) { IntegerOf(x); Set(register); }
                else { NormalizeKnown(x, st); if (cached) { Get(x); Set(register); } else { Get(F); Get(x); StoreF64(SlotOffset(slot) + ValueNumber); } }
            }
            PushKnown(d - 2, VoidTag); loc[rhs] = Loc.Mem; return;
        }
        if (slot < 0 && operation != IrOperation.Modulo && (rt == Unknown || IsNumeric(rt)) && Shadowed(constant, out var shadow)) {
            ShadowAcquire(constant, shadow, access.Dynamic);
            Get(shadow.Valid); if (rt == Unknown) { NumericTagTest(T(rhs)); Op(0x71); }
            Branch(() => {
                if (rt == IntTag && operation != IrOperation.Divide) { IntArithmetic(operation, () => Get(shadow.Value), () => Get(I(rhs))); Set(shadow.Value); }
                else { Get(shadow.Value); ToDouble(); Number(rhs, rt); Op(Arithmetic(operation)); Set(x); IntegerOf(x); Set(shadow.Value); }
                return false;
            }, () => { CompoundMemory(constant, d, access, rhs, rt, operation, slot); return false; }, d - 2);
            PushKnown(d - 2, VoidTag); loc[rhs] = Loc.Mem; return;
        }
        CompoundMemory(constant, d, access, rhs, rt, operation, slot);
        PushKnown(d - 2, VoidTag);
    }
    private void CompoundMemory(int constant, int d, WasmScopePlan.Access access, int rhs, int rt, IrOperation operation, int slot)
    {
        if (slot >= 0) { Get(F); I32(SlotOffset(slot)); Op(0x6a); Set(A); WriteBack(slot); } else TargetAddress(access, constant);
        void Slow() { Helper("compoundAt", constant, d, 2, true, A); }
        if (operation == IrOperation.Modulo || rt != Unknown && !IsNumeric(rt)) { Slow(); if (slot >= 0) Reload(slot); return; }
        Get(A); LoadI32(ValueTag); Set(B);
        NumericTagTest(B); if (rt == Unknown) { NumericTagTest(T(rhs)); Op(0x71); }
        Branch(() => {
            Get(A); LoadF64(ValueNumber); Number(rhs, rt); Op(Arithmetic(operation)); Set(x); NormalizeDynamic(B, x);
            Get(A); Get(x); StoreF64(ValueNumber); return false;
        }, () => { Slow(); return false; }, d - 2);
        if (slot >= 0) Reload(slot);
    }

    private void Increment(int constant, int d, bool prefix)
    {
        var access = plan.Accesses[pc]!; int slot = DefiniteSlot(access);
        int st = slot >= 0 ? plan.SlotTags[pc]![slot] : Unknown;
        if (slot >= 0 && IsNumeric(st)) {
            bool cached = slotRegisters.TryGetValue(slot, out int register);
            if (cached && st == IntTag) {
                // Integer increment: the maximum integer wraps to the minimum like number(x + 1, INTEGER).
                Get(register); Set(I(d));
                Get(register); I32(int.MaxValue); Op(0x46); If(0x7f); I32(int.MinValue); Else(); Get(register); I32(1); Op(0x6a); End(); Set(register);
                if (prefix) { Get(register); Set(I(d)); }
                loc[d] = Loc.Reg; return;
            }
            if (cached) Get(register); else { Get(F); LoadF64(SlotOffset(slot) + ValueNumber); } Set(x);
            Get(x); F64(1); Op(0xa0); Set(y); NormalizeKnown(y, st);
            if (cached) { Get(y); Set(register); } else { Get(F); Get(y); StoreF64(SlotOffset(slot) + ValueNumber); }
            Get(prefix ? y : x);
            if (st == IntTag) { ToInt(); Set(I(d)); } else Set(N(d));
            loc[d] = Loc.Reg; return;
        }
        bool Memory() {
            if (slot >= 0) { Get(F); I32(SlotOffset(slot)); Op(0x6a); Set(A); WriteBack(slot); } else TargetAddress(access, constant);
            Get(A); LoadI32(ValueTag); Set(T(d)); NumericTagTest(T(d));
            Branch(() => {
                Get(A); LoadF64(ValueNumber); Set(x); Get(x); F64(1); Op(0xa0); Set(y); NormalizeDynamic(T(d), y);
                Get(A); Get(y); StoreF64(ValueNumber); Get(prefix ? y : x); Set(N(d)); loc[d] = Loc.Reg; return false;
            }, () => { Helper("incrementAt", prefix ? 1 : 0, d, 0, true, A); LoadValue(d, F, StackOffset(d), Unknown); loc[d] = Loc.Both; return false; });
            if (slot >= 0) Reload(slot);
            return false;
        }
        if (slot >= 0 || !Shadowed(constant, out var shadow)) { Memory(); return; }
        ShadowAcquire(constant, shadow, access.Dynamic); Get(shadow.Valid);
        Branch(() => {
            // Integer increment: the maximum integer wraps to the minimum like number(x + 1, INTEGER).
            Get(shadow.Value); ToDouble(); Set(N(d));
            Get(shadow.Value); I32(int.MaxValue); Op(0x46); If(0x7f); I32(int.MinValue); Else(); Get(shadow.Value); I32(1); Op(0x6a); End(); Set(shadow.Value);
            if (prefix) { Get(shadow.Value); ToDouble(); Set(N(d)); }
            I32(IntTag); Set(T(d)); I32(0); Set(R(d)); loc[d] = Loc.Reg; return false;
        }, Memory);
    }

    /// <summary>Pushes the i32 index of operand p (INTEGER-tagged at run time).</summary>
    private void IndexOf(int p, int tag) { if (tag == IntTag) Get(I(p)); else { Get(N(p)); ToInt(); } }
    /// <summary>Pushes whether index operand i is within the array operand a.</summary>
    private void InBounds(int a, int i, int ti) { IndexOf(i, ti); Get(R(a)); LoadI32(ArrayCount); Op(0x49); }

    private void Index(int d)
    {
        int a = d - 2, i = d - 1; var tags = Tags(pc); int ta = tags[a], ti = tags[i];
        Ensure(a, ta); Ensure(i, ti);
        void Slow() { Helper("index", 0, d, 2); LoadValue(a, F, StackOffset(a), Unknown); loc[a] = Loc.Both; }
        if (ta is not (ArrayTag or Unknown) || ti is not (IntTag or Unknown)) { Slow(); loc[i] = Loc.Mem; return; }
        if (ta == Unknown) { Get(T(a)); I32(ArrayTag); Op(0x46); } else I32(1);
        if (ti == Unknown) { Get(T(i)); I32(IntTag); Op(0x46); Op(0x71); }
        Branch(() => {
            // Out-of-range array reads produce null like rt_index; an unsigned compare also rejects negatives.
            InBounds(a, i, ti);
            Branch(() => {
                Get(R(a)); LoadI32(ArrayValues); IndexOf(i, ti); I32(4); Op(0x74); Op(0x6a); Set(B);
                LoadValue(a, B, 0, Unknown); loc[a] = Loc.Reg; return false;
            }, () => { I32(NullTag); Set(T(a)); I32(0); Set(R(a)); F64(0); Set(N(a)); loc[a] = Loc.Reg; return false; });
            return false;
        }, () => { Slow(); return false; }, i);
    }

    private void ArrayWriteTest(int a, int ta, int i, int ti)
    {
        if (ta == Unknown) { Get(T(a)); I32(ArrayTag); Op(0x46); } else I32(1);
        if (ti == Unknown) { Get(T(i)); I32(IntTag); Op(0x46); Op(0x71); }
        If(0x7f); InBounds(a, i, ti); Else(); I32(0); End();
    }
    private void CheckArrayWrite(int d)
    {
        int a = d - 2, i = d - 1; var tags = Tags(pc); int ta = tags[a], ti = tags[i];
        Ensure(a, ta); Ensure(i, ti);
        if (ta is not (ArrayTag or Unknown) || ti is not (IntTag or Unknown)) { Helper("checkArrayWrite", 0, d, 0); return; }
        ArrayWriteTest(a, ta, i, ti); Op(0x45);
        Branch(() => { Helper("checkArrayWrite", 0, d, 0); return false; }, null);
    }
    private void WriteIndex(int d)
    {
        int a = d - 3, i = d - 2, v = d - 1; var tags = Tags(pc); int ta = tags[a], ti = tags[i], tv = tags[v];
        Ensure(a, ta); Ensure(i, ti); Ensure(v, tv);
        void Slow() { Helper("writeIndex", 0, d, 3); LoadValue(a, F, StackOffset(a), tv); loc[a] = Loc.Both; }
        if (ta is not (ArrayTag or Unknown) || ti is not (IntTag or Unknown)) { Slow(); return; }
        // Owned, unexposed storage needs no copy-on-write or host dirty tracking.
        ArrayWriteTest(a, ta, i, ti);
        If(0x7f); Get(R(a)); LoadI32(ArrayStorage); LoadI32(ArrayStorageReferences); I32(1); Op(0x46); Get(R(a)); LoadI32(ArrayExposed); Op(0x45); Op(0x71); Else(); I32(0); End();
        Branch(() => {
            Get(R(a)); LoadI32(ArrayValues); IndexOf(i, ti); I32(4); Op(0x74); Op(0x6a); Set(B);
            StoreValue(v, B, 0, tv);
            if (tv == Unknown) { Get(T(v)); Set(T(a)); }
            if (HasRef(tv)) { Get(R(v)); Set(R(a)); }
            if (tv == IntTag) { Get(I(v)); Set(I(a)); } else { Get(N(v)); Set(N(a)); }
            loc[a] = Loc.Reg; return false;
        }, () => { Slow(); return false; }, i);
    }
}
