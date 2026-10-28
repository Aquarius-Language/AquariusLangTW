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
/// raised directly with the generic message. The execution budget travels through native calls as a parameter and a
/// second result; a returned budget of int.MinValue means the callee raised an error (the module's signal global then
/// holds 5) or deoptimized (the signal holds the outermost generic frame). The entry adapter checks every outer binding (tag, closure identity,
/// unshadowed core conversions) and the argument types before any effect, and otherwise leaves the call to the generic
/// body (status 0).
/// </summary>
internal sealed class WasmNativeFunction
{
    private const int Unknown = -1, VoidType = 0, IntType = 1, FloatType = 2, DoubleType = 3, BoolType = 4, NullType = 11;
    // Struct type codes (32 | id << 6, id < 2^23) stay below the compile-time-only operand codes.
    private const int CallableBase = 1 << 30, BuiltinBase = CallableBase + (1 << 20), MixedType = 999;
    /// <summary>The marker of an outer binding holding a struct type (constructed natively): its Callee is the type's code.</summary>
    private const int StructTypeMarker = -2;
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
    /// <summary>What a call can change that its caller holds in locals, here or in any callee: outer bindings (an
    /// assignment to one) and arrays' values pointers (an element write, which may copy shared storage).</summary>
    internal bool WritesOuter { get; private set; } = true;
    internal bool WritesArrays { get; private set; } = true;
    /// <summary>Whether the body (or a callee) allocates: concatenates strings, builds array literals or constructs structs.
    /// Its loop heads then also poll for a pending collection (deoptimizing to the generic checkpoint, which collects).</summary>
    internal bool Allocates { get; private set; }
    /// <summary>Whether the body (or a callee) appends to arrays (陣列.追加 / Array.Append): their counts change too, so every
    /// array local's companions are read again after it (or after a call to it), not only the values pointers.</summary>
    internal bool ResizesArrays { get; private set; }

    /// <summary>Appends to a string slot (`s = s + x`): the Add's position and its slot, and the loads that belong to such an
    /// append (its left operand and the assignment's placeholder). An append keeps the text it builds marked as owned by the
    /// slot in the pointer's low bit (texts are 16-byte aligned), so the next append can extend it in place
    /// (rt_nativeAppend); every other load of the slot, and every frame the slot is written to, drops the mark.</summary>
    private Dictionary<int, int>? appendAt;
    private HashSet<int>? appendReads;
    private void FindAppends()
    {
        if (appendAt != null) return;
        appendAt = new(); appendReads = new();
        var code = body.Code;
        int Pusher(int at, int depth) { for (int k = at - 1; k >= 0; k--) if (plan.Depth[k] == depth) return k; return -1; }
        int Slot(int i) => i >= 0 && plan.Depth[i] >= 0 && plan.Accesses[i] is { } a ? SlotAt(i, a) : -1;
        for (int i = 1; i < code.Length; i++) {
            if (code[i].Code != IrOperation.Assign || plan.Depth[i] < 0 || Slot(i) is not (>= 0 and var slot)) continue;
            int add = i - 1; if (code[add].Code == IrOperation.CheckType) add--;
            if (add < 1 || code[add].Code != IrOperation.Add || plan.Depth[add] < 3) continue;
            int depth = plan.Depth[add], left = Pusher(add, depth - 2), dummy = left - 1;
            if (left < 1 || code[left].Code != IrOperation.Load || Slot(left) != slot || code[dummy].Code != IrOperation.Load || Slot(dummy) != slot
                || plan.Depth[dummy] != depth - 3) continue;
            appendAt[add] = slot; appendReads.Add(left); appendReads.Add(dummy);
        }
    }
    internal bool Mutates => WritesOuter || WritesArrays;
    /// <summary>A small leaf (no calls, loops or outer bindings) that changes nothing its caller holds: callers emit
    /// its body in place of the call. Its only checkpoint is its entry, which the caller tests before choosing the
    /// inlined body; a failing test takes the ordinary call, which deoptimizes exactly as before.</summary>
    internal bool Inlinable { get; private set; }
    /// <summary>The native recursion depth at which an entry deoptimizes (bounded by the frames' total size).</summary>
    private int DepthLimit => Math.Clamp(262144 / (32 + 8 * (plan.Locals + capacity + Pointers.Length)), 64, 8192);
    /// <summary>Why the analysis rejected the body (for AQUARIUS_WASM_TIER_DIAGNOSTICS).</summary>
    internal string? Rejection { get; private set; }

    private readonly int[] slotTypes;
    private readonly Dictionary<int, int[]> states = new();
    private readonly List<string> callNames = new();
    private int capacity = 1;

    /// <summary>A native region: one loop of an untyped body, [Start, Stop] = its EnterLoop and LeaveLoop, entered and
    /// resumed at its loop heads from the generic frame and leaving to the generic block at Stop (or a loop head).</summary>
    internal bool IsRegion { get; }
    internal int Start { get; }
    internal int Stop { get; }
    /// <summary>The types a region assumes for untyped bindings instead of their guesses (a JIT stencil's variant).</summary>
    private readonly IReadOnlyDictionary<string, int>? assumed;
    /// <summary>The native-tier signature of a callee literal (typed or a clone), during selection.</summary>
    private Func<AquariusLang.ast.FunctionLiteral, NativeSignature?>? signatureOf;
    /// <summary>The address of this body's constant pool (string constants are read from it).</summary>
    internal int Pool { get; set; }
    /// <summary>The result types a clone tries, in order.</summary>
    private static readonly int[] CloneResults = [IntType, DoubleType, BoolType, FloatType, StringType, AnyArrayType, VoidType];
    private int Assumed(string name, NativeSignature.OuterName outer) => assumed != null && assumed.TryGetValue(name, out int type) ? type : outer.Guess;

    private WasmNativeFunction(int bodyIndex, NativeSignature signature, LoweredProgram body, WasmScopePlan plan, int start = 0, int end = -1,
        IReadOnlyDictionary<string, int>? assumed = null)
    {
        Body = bodyIndex; Signature = signature; this.body = body; this.plan = plan; this.assumed = assumed;
        IsRegion = end >= 0; Start = start; Stop = IsRegion ? end : body.Code.Length;
        slotTypes = Enumerable.Repeat(Unknown, plan.Locals).ToArray();
        for (int p = 0; p < signature.Parameters.Length; p++)
            slotTypes[p] = signature.Clone && IsArray(signature.Parameters[p]) ? signature.Parameters[p] | LooseFlag : signature.Parameters[p];
        // A region also uses the generic frame's slots declared before it, with the types the generic dataflow proves.
        if (IsRegion && plan.SlotTags[start] is { } proven)
            for (int s = 0; s < proven.Length; s++)
                if (plan.SlotDecl[start]![s] == WasmScopePlan.Yes && proven[s] is IntType or FloatType or DoubleType or BoolType) slotTypes[s] = proven[s];
        // A typed array declared before the loop (in a function body, where it is a slot) has its annotated type; the
        // region's entry checks the array's element lock like any other slot type.
        if (IsRegion)
            foreach (var (name, outer) in signature.Outer) {
                if (!IsRef(outer.Type) || !outer.Sibling) continue;
                var declared = plan.Scopes.Where(scope => scope.Slots.ContainsKey(name)).Select(scope => scope.Slots[name])
                    .Where(slot => plan.SlotDecl[start]![slot] == WasmScopePlan.Yes).ToArray();
                if (declared.Length == 1 && slotTypes[declared[0]] == Unknown) slotTypes[declared[0]] = outer.Type;
            }
        // Untyped slots declared before the loop (parameters and locals of the function) are speculated to hold their
        // guessed scalar types (TypeChecker): the entry checks every slot's tag, and a wrong guess leaves the loop generic.
        if (IsRegion)
            foreach (var (name, outer) in signature.Outer) {
                int guess = Assumed(name, outer);
                if (IsArray(guess)) guess |= LooseFlag;
                if (outer.Type != 0 || !(guess is IntType or FloatType or DoubleType or BoolType || IsLoose(guess))) continue;
                var declared = plan.Scopes.Where(scope => scope.Slots.ContainsKey(name)).Select(scope => scope.Slots[name])
                    .Where(slot => plan.SlotDecl[start]![slot] == WasmScopePlan.Yes).ToArray();
                if (declared.Length == 1 && slotTypes[declared[0]] == Unknown) slotTypes[declared[0]] = guess;
            }
    }

    /// <summary>The outermost loops of untyped bodies that the native tier accepts as regions.</summary>
    internal static List<WasmNativeFunction> SelectRegions(IReadOnlyList<LoweredProgram> bodies, IReadOnlyList<WasmScopePlan> plans,
        IReadOnlySet<int> natives, Action<int, string>? rejected = null)
    {
        var regions = new List<WasmNativeFunction>();
        for (int f = 0; f < bodies.Count; f++) {
            // Functions the native tier accepts run their loops natively already.
            if (natives.Contains(f) || !plans[f].Scopes.Skip(1).All(scope => scope.Promoted)) continue;
            int covered = -1;
            foreach (var (enter, head, leave, region) in bodies[f].Loops.OrderBy(l => l.Enter)) {
                if (enter < covered || region == null || plans[f].Depth[enter] != 0) continue;
                var candidate = new WasmNativeFunction(f, region, bodies[f], plans[f], enter, leave);
                if (candidate.Analyze(_ => -1)) { candidate.Pointers = candidate.OwnPointers.Keys.ToArray(); regions.Add(candidate); covered = leave; }
                else rejected?.Invoke(f, $"loop at {enter}: " + (candidate.Rejection ?? "its control flow does not nest"));
            }
        }
        return regions;
    }

    /// <summary>A JIT stencil: one loop of an untyped body compiled natively under assumed types for its untyped bindings.
    /// The desktop runtime JIT installs the first variant of a hot loop whose entry check accepts the values it sees.</summary>
    internal sealed record Stencil(int Loop, WasmNativeFunction Region, IReadOnlyDictionary<string, int> Types);

    /// <summary>Stencils for the loops of untyped bodies that the regions do not cover: every assignment of integer or
    /// double to the loop's untyped scalar bindings (up to three) other than the guessed one a region already runs.
    /// Loops are numbered in order; a loop's variants come most likely first (its guesses, then integers).</summary>
    internal static List<Stencil> SelectStencils(IReadOnlyList<LoweredProgram> bodies, IReadOnlyList<WasmScopePlan> plans,
        IReadOnlySet<int> natives, IReadOnlyList<WasmNativeFunction> regions)
    {
        var stencils = new List<Stencil>();
        int loop = 0;
        for (int f = 0; f < bodies.Count; f++) {
            if (natives.Contains(f) || !plans[f].Scopes.Skip(1).All(scope => scope.Promoted)) continue;
            var covered = regions.Where(r => r.Body == f).Select(r => (r.Start, r.Stop)).ToList();
            foreach (var (enter, _, leave, region) in bodies[f].Loops.OrderBy(l => l.Enter)) {
                if (region == null || plans[f].Depth[enter] != 0 || covered.Any(c => c.Start <= enter && enter <= c.Stop && !(c.Start == enter))) continue;
                // Variants assign scalar types; untyped arrays keep their guess (loose arrays).
                var names = region.Outer.Where(o => o.Value.Type == 0 && o.Value.Function == null && o.Value.Struct == null && !IsArray(o.Value.Guess))
                    .Select(o => o.Key).ToArray();
                if (names.Length is 0 or > 3) continue;
                var accepted = regions.FirstOrDefault(r => r.Body == f && r.Start == enter);
                var guessed = names.ToDictionary(n => n, n => region.Outer[n].Guess, StringComparer.Ordinal);
                var variants = new List<Dictionary<string, int>>();
                for (int bits = 0; bits < 1 << names.Length; bits++)
                    variants.Add(names.Select((n, i) => (n, (bits >> i & 1) == 0 ? IntType : DoubleType)).ToDictionary(p => p.n, p => p.Item2, StringComparer.Ordinal));
                // The guessed assignment first, unless a region already runs it.
                variants = variants.OrderBy(v => v.All(p => guessed[p.Key] == p.Value) ? 0 : 1).ToList();
                var found = new List<Stencil>();
                foreach (var types in variants) {
                    if (accepted != null && types.All(p => guessed[p.Key] == p.Value)) continue;
                    var candidate = new WasmNativeFunction(f, region, bodies[f], plans[f], enter, leave, types);
                    if (!candidate.Analyze(_ => -1)) continue;
                    candidate.Pointers = candidate.OwnPointers.Keys.ToArray();
                    found.Add(new Stencil(loop, candidate, types));
                }
                if (found.Count == 0) continue;
                stencils.AddRange(found); loop++;
            }
        }
        return stencils;
    }

    private static bool Numeric(int type) => type is IntType or FloatType or DoubleType;
    private static bool Wide(int type) => type is FloatType or DoubleType;
    /// <summary>Types a native local holds: numbers, Booleans, typed arrays and arrays of any values (an Array* in an i32).</summary>
    private static bool Value(int type) => type is IntType or FloatType or DoubleType or BoolType or StringType || IsRef(type) || IsStructType(type);
    /// <summary>A string (字串 / string): a Text* in an i32, never null. Its length and code units are read in place;
    /// equality, concatenation and one-unit results call the runtime (rt_nativeTextEqual, rt_nativeConcat,
    /// rt_nativeUnitText). A read past its end deoptimizes at that read, where the generic code gives null.</summary>
    private const int StringType = 5;
    /// <summary>Types held as a pointer in an i32: arrays, structs and strings.</summary>
    private static bool IsPointer(int type) => IsRef(type) || IsStructType(type) || type == StringType;
    private static bool IsArray(int type) => (type & 16) != 0 && (type & 15) is >= IntType and <= BoolType;
    /// <summary>An array of any values (陣列 / array): code 16, element type 0.</summary>
    private const int AnyArrayType = 16;
    /// <summary>One element read from an array of any values: a whole generic value, its tag in the operand's count
    /// companion, its reference in the i32 local and its number in the f64 local. It lives only on the operand stack
    /// (an argument of an 陣列 parameter, checked at the call, or an element of an array literal).</summary>
    private const int AnyValueType = 48;
    /// <summary>An untyped array a region speculates holds only elements of one type (a TypeChecker array guess): that
    /// typed array's type with this flag. Its entry check is rt_arrayKind (the array's element kind), and a read outside
    /// it leaves the region at that read, where the generic code gives null (only typed arrays make it an error).</summary>
    private const int LooseFlag = 64;
    private static bool IsLoose(int type) => IsArray(type) && (type & LooseFlag) != 0;
    /// <summary>Types held as an Array* (with count and values companions).</summary>
    private static bool IsRef(int type) => IsArray(type) || type == AnyArrayType;
    /// <summary>A struct type (結構 / struct): a Struct* in an i32, 0 for null (struct types are nullable).</summary>
    private static bool IsStructType(int type) => type >= 32 && type < CallableBase && (type & 63) == 32;
    /// <summary>The native type of a value read from a field annotated with code: an untyped field gives a whole value.</summary>
    private static int FieldValueType(int code) => code == 0 ? AnyValueType : code == AnyArrayType || IsArray(code) || IsStructType(code)
        || code is IntType or FloatType or DoubleType or BoolType ? code : Unknown;
    /// <summary>Whether a native value of type value may be stored in a field annotated with code (statically).</summary>
    private static bool FieldAccepts(int code, int value) => code == 0 ? Value(value) || value == AnyValueType
        : IsStructType(code) ? value == code : code == AnyArrayType ? IsRef(value) : Widens(value, code);
    private const int LengthBuiltin = 1;
    /// <summary>陣列.追加 / Array.Append: the runtime's builtin id, so a deoptimized operand holds that very builtin. The
    /// portable 陣列 / Array module itself is a compile-time-only operand (ArrayModule) that only its member call consumes.</summary>
    private const int AppendBuiltin = 10, ArrayModule = 1000;
    private const int ArrayLimit = 33554431; // ARRAY_LIMIT in runtime/guest/arrays.h
    private static bool Widens(int from, int to) => from == to || from is IntType or FloatType && to is FloatType or DoubleType && from < to
        || to == AnyArrayType && IsArray(from);
    private static int BuiltinOf(string name) => name switch {
        "int" or "整數" => ConvertFirst, "float" or "浮點數" => ConvertFirst + 1, "double" or "雙精度" => ConvertFirst + 2,
        "len" or "長度" => LengthBuiltin, "Array" or "陣列" => ArrayModule, _ => -1 };

    // ---- selection ----------------------------------------------------------------------------------------------

    /// <summary>The fully typed bodies the native tier accepts. A function calls another directly only when both are
    /// declared in the same scope, so their outer names resolve to the same bindings.</summary>
    internal static List<WasmNativeFunction> Select(IReadOnlyList<LoweredProgram> bodies, IReadOnlyList<FunctionCode?> codes, IReadOnlyList<WasmScopePlan> plans,
        Action<int, string>? rejected = null)
    {
        var bodyOf = new Dictionary<AquariusLang.ast.FunctionLiteral, int>(ReferenceEqualityComparer.Instance);
        for (int f = 0; f < codes.Count; f++) if (codes[f] is { } code) bodyOf[code.Literal] = f;
        // Clones (untyped functions whose parameters all have guesses) try each result type in turn until one is proven.
        var candidates = new HashSet<int>(Enumerable.Range(0, bodies.Count).Where(f => codes[f]?.Native is { } s
            && (!s.Clone || s.Parameters.All(p => p != 0)) && plans[f].PromotedFunction));
        var choice = candidates.Where(f => codes[f]!.Native!.Clone).ToDictionary(f => f, f => 0);
        foreach (var (f, _) in choice) codes[f]!.Native!.Result = CloneResults[0];
        NativeSignature? SignatureOf(AquariusLang.ast.FunctionLiteral literal) => bodyOf.TryGetValue(literal, out int g) ? codes[g]!.Native : null;
        Dictionary<int, WasmNativeFunction> accepted;
        while (true) {
            accepted = new();
            var retry = new HashSet<int>();
            foreach (int f in candidates) {
                var native = new WasmNativeFunction(f, codes[f]!.Native!, bodies[f], plans[f]) { signatureOf = SignatureOf };
                if (native.Analyze(literal => bodyOf.TryGetValue(literal, out int g) && candidates.Contains(g)
                        && codes[g]!.Native!.DefiningScope == codes[f]!.Native!.DefiningScope ? g : -1))
                    accepted.Add(f, native);
                else {
                    rejected?.Invoke(f, native.Rejection ?? "its control flow does not nest");
                    if (choice.TryGetValue(f, out int k) && k + 1 < CloneResults.Length) { choice[f] = k + 1; codes[f]!.Native!.Result = CloneResults[k + 1]; retry.Add(f); }
                }
            }
            if (accepted.Count == candidates.Count) break;
            candidates = accepted.Keys.Concat(retry).ToHashSet();
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
        foreach (var native in accepted.Values) {
            native.WritesOuter = native.Written.Count > 0;
            native.WritesArrays = native.ResizesArrays || native.body.Code.Any(op => op.Code is IrOperation.WriteIndex or IrOperation.CompoundIndex or IrOperation.IncrementIndex);
        }
        for (changed = true; changed;) {
            changed = false;
            foreach (var native in accepted.Values)
                foreach (var callee in native.Callees.Select(c => accepted[c])) {
                    if (callee.WritesOuter && !native.WritesOuter) native.WritesOuter = changed = true;
                    if (callee.WritesArrays && !native.WritesArrays) native.WritesArrays = changed = true;
                    if (callee.ResizesArrays && !native.ResizesArrays) native.ResizesArrays = changed = true;
                    if (callee.Allocates && !native.Allocates) native.Allocates = changed = true;
                }
        }
        foreach (var native in accepted.Values)
            native.Inlinable = !native.Mutates && native.Pointers.Length == 0 && native.Callees.Count == 0
                && !native.constructs!.Any(c => c.Loop) && native.body.Code.Length <= 96;
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
            // A region resolves its outer bindings by name from the frame's scope chain (guarded by tag), so any depth works;
            // a native function receives pointers from its caller, so only bindings of its defining scope do.
            if (!outer.Sibling && !IsRegion) return Unknown;
            if (outer.Function != null && !outer.Assigned) {
                int callee = nativeBody(outer.Function);
                if (callee < 0) return Unknown;
                if (callee == Body) ClosureGuards[name] = callee; else OwnPointers[name] = (ClosureTag, callee);
                Callees.Add(callee);
                int index = callNames.IndexOf(name); if (index < 0) { index = callNames.Count; callNames.Add(name); }
                return CallableBase + index;
            }
            if (outer.Struct != null && !outer.Assigned) {
                OwnPointers[name] = (StructTypeMarker, TypeChecker.StructFlag | outer.Struct.Id << 6);
                int index = callNames.IndexOf(name); if (index < 0) { index = callNames.Count; callNames.Add(name); }
                return CallableBase + index;
            }
            // A region speculates that an untyped binding holds its guessed type (TypeChecker): its entry checks the tag and
            // leaves the loop to the generic code when the guess is wrong, so speculation never changes a result.
            int type = outer.Type == 0 && (IsRegion || Signature.Clone) ? Assumed(name, outer) : outer.Type;
            if (outer.Type == 0 && IsArray(type)) type |= LooseFlag;
            if (!Value(type)) return Unknown;
            OwnPointers[name] = (type, -1);
            return type;
        }
        int builtin = BuiltinOf(name);
        if (builtin < 0) return Unknown;
        Guards[name] = builtin;
        return BuiltinBase + builtin;
    }

    // ---- structs --------------------------------------------------------------------------------------------------

    /// <summary>The declaration of a struct type code.</summary>
    private AquariusLang.ast.StructStatement? Shape(int code) => IsStructType(code) && Signature.Structs.TryGetValue(code, out var shape) ? shape : null;
    /// <summary>A field of a value of struct type code: its index and annotation code (0 when untyped), or null.</summary>
    private (int Index, int Code)? StructField(int code, string name) {
        if (Shape(code) is not { } shape) return null;
        int index = Array.FindIndex(shape.Fields, f => f.Value == name);
        return index < 0 ? null : (index, shape.Fields[index].Type?.Code ?? 0);
    }
    /// <summary>Whether a compile-time callee is a struct type binding, and its code.</summary>
    private bool StructCallee(int callee, out int code) {
        code = 0;
        if (callee < CallableBase || callee >= BuiltinBase) return false;
        if (!OwnPointers.TryGetValue(callNames[callee - CallableBase], out var own) || own.Tag != StructTypeMarker) return false;
        code = own.Callee; return true;
    }
    /// <summary>The fields each argument of a construction sets (by name, or positionally), or null when the native code
    /// cannot build it: an unknown or missing field, or an argument the field does not statically accept.</summary>
    private int[]? ConstructionFields(int code, int[] arguments, string[]? names) {
        if (Shape(code) is not { } shape || names == null && arguments.Length > shape.Fields.Length) return null;
        var fields = new int[arguments.Length];
        for (int i = 0; i < arguments.Length; i++) {
            int index = names == null ? i : Array.FindIndex(shape.Fields, f => f.Value == names[i]);
            if (index < 0 || !FieldAccepts(shape.Fields[index].Type?.Code ?? 0, arguments[i])) return null;
            fields[i] = index;
        }
        // Fields left out are null: only untyped and struct-typed fields may be.
        for (int f = 0; f < shape.Fields.Length; f++)
            if (Array.IndexOf(fields, f) < 0 && shape.Fields[f].Type is { } type && !IsStructType(type.Code)) return null;
        return fields;
    }
    private int? Construction(int callee, int[] arguments, string[]? names) =>
        StructCallee(callee, out int code) && ConstructionFields(code, arguments, names) != null ? code : null;

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
        ResizesArrays = false;
        var work = new Queue<(int Pc, int[] Stack)>(); work.Enqueue((Start, Array.Empty<int>()));
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
            if (IsRegion && pc == Stop) { if (state.Stack.Length != 0) return Reject(pc); returns = true; continue; }
            if (IsRegion && (pc < Start || pc > Stop)) return Reject(pc);
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
                        IntegerObj => IntType, FloatObj => FloatType, DoubleObj => DoubleType, BooleanObj => BoolType, NullObj => NullType, StringObj => StringType, _ => Unknown };
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
                    // The array module is only the receiver of a member call (it never reaches a deoptimization site).
                    if (type == BuiltinBase + ArrayModule && (pc + 1 >= n || code[pc + 1].Code != IrOperation.MemberFunction)) return Reject(pc);
                    stack.Add(type); break;
                }
                case IrOperation.MemberFunction:
                    if (Top() != BuiltinBase + ArrayModule || NameOf(operand) is not ("追加" or "Append")) return Reject(pc);
                    stack[^1] = BuiltinBase + AppendBuiltin; break;
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
                case IrOperation.Add when Top() == StringType && Top(2) == StringType: Allocates = true; Pop(2); stack.Add(StringType); break;
                case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide: case IrOperation.Modulo:
                    if (!Numeric(Top()) || !Numeric(Top(2))) return Reject(pc);
                    { int type = Math.Max(Top(), Top(2)); Pop(2); stack.Add(type); }
                    break;
                case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                    if (!Numeric(Top()) || !Numeric(Top(2))) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.Equal: case IrOperation.NotEqual:
                    // Structs compare by identity, like the generic runtime.
                    if (!(Numeric(Top()) && Numeric(Top(2)) || Top() == BoolType && Top(2) == BoolType || IsStructType(Top()) && IsStructType(Top(2))
                        || Top() == StringType && Top(2) == StringType)) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.And: case IrOperation.Or:
                    if (Top() != BoolType || Top(2) != BoolType) return Reject(pc);
                    Pop(2); stack.Add(BoolType); break;
                case IrOperation.Negate: if (!Numeric(Top())) return Reject(pc); break;
                case IrOperation.Not: if (Top() != BoolType) return Reject(pc); break;
                case IrOperation.CheckType: {
                    int want = operand & 31;
                    if (want == AnyArrayType ? !IsRef(Top()) : IsArray(want) ? Top() != want : !Widens(Top(), want)) return Reject(pc);
                    stack[^1] = want; break;
                }
                case IrOperation.Member: {
                    if (StructField(Top(), NameOf(operand)) is not { } field) return Reject(pc);
                    int type = FieldValueType(field.Code);
                    if (type == Unknown) return Reject(pc);
                    stack[^1] = type; break;
                }
                case IrOperation.SetMember: {
                    if (stack.Count < 2 || StructField(Top(2), NameOf(operand)) is not { } field || !FieldAccepts(field.Code, Top())) return Reject(pc);
                    // The stored value is the result, widened to a numeric field's type.
                    int value = Numeric(field.Code) ? field.Code : Top(); Pop(2); stack.Add(value); break;
                }
                case IrOperation.CompoundMember: {
                    if (stack.Count < 2 || StructField(Top(2), ((Assignment)body.Pool[operand]).Name) is not { } field || !Numeric(field.Code) || !Numeric(Top())) return Reject(pc);
                    Pop(2); stack.Add(VoidType); break;
                }
                case IrOperation.CheckStruct:
                    if (Top() != (TypeChecker.StructFlag | int.Parse(((string)body.Pool[operand]).Split('\u0001')[0]) << 6)) return Reject(pc);
                    break;
                case IrOperation.CallNamed: {
                    var names = ((string)body.Pool[operand]).Split('\u0001');
                    if (stack.Count < names.Length + 1 || stack.Contains(MixedType)) return Reject(pc);
                    int callee = stack[stack.Count - names.Length - 1];
                    if (Construction(callee, stack.Skip(stack.Count - names.Length).ToArray(), names) is not int made) return Reject(pc);
                    Allocates = true;
                    Pop(names.Length + 1); stack.Add(made); break;
                }
                case IrOperation.Index:
                    if (Top(2) == AnyArrayType && Top() == IntType) { Pop(2); stack.Add(AnyValueType); break; }
                    // A string's code unit is a one-unit string; past the end it deoptimizes, so the read must start a generic block.
                    if (Top(2) == StringType && Top() == IntType) {
                        if (Array.BinarySearch(WasmBodyEmitter.BlockStarts(body, plan), pc) < 0) return Reject(pc);
                        Pop(2); stack.Add(StringType); break;
                    }
                    if (!IsArray(Top(2)) || Top() != IntType) return Reject(pc);
                    if (IsLoose(Top(2)) && Array.BinarySearch(WasmBodyEmitter.BlockStarts(body, plan), pc) < 0) return Reject(pc);
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
                case IrOperation.CompoundIndex:
                    // A number element keeps its type; anything else (strings, Booleans, other arrays) stays generic.
                    if (stack.Count < 3 || !IsArray(Top(3)) || Top(2) != IntType || !Numeric(Top(3) & 15) || !Numeric(Top())) return Reject(pc);
                    Pop(3); stack.Add(VoidType); break;
                case IrOperation.IncrementIndex:
                    if (!IsArray(Top(2)) || Top() != IntType || !Numeric(Top(2) & 15)) return Reject(pc);
                    { int element = Top(2) & 15; Pop(2); stack.Add(element); }
                    break;
                case IrOperation.IncrementMember: {
                    if (StructField(Top(), NameOf(operand >> 1)) is not { } field || !Numeric(field.Code)) return Reject(pc);
                    stack[^1] = field.Code; break;
                }
                case IrOperation.Jump:
                    work.Enqueue((operand, stack.ToArray())); continue;
                case IrOperation.JumpIfFalse: case IrOperation.LoopCondition:
                    if (Top() != BoolType) return Reject(pc);
                    Pop(1); work.Enqueue((operand, stack.ToArray())); break;
                case IrOperation.Array:
                    // An array literal of native values is built natively (an array of any values, like the generic one).
                    if (stack.Count < operand || stack.Skip(stack.Count - operand).Any(t => !Value(t) && t != AnyValueType)) return Reject(pc);
                    Allocates = true; Pop(operand); stack.Add(AnyArrayType); break;
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
                        if (count != 1 || !IsRef(arguments[0]) && arguments[0] != StringType) return Reject(pc);
                        result = IntType;
                    } else if (callee == BuiltinBase + AppendBuiltin) {
                        // An element the typed array takes (widened like the generic element check) or, for a loose array, of
                        // exactly its speculated type (anything else would make its elements mixed); the result is the array.
                        if (count != 2 || !IsArray(arguments[0])) return Reject(pc);
                        int element = arguments[0] & 15;
                        if (arguments[1] != element && (IsLoose(arguments[0]) || !Numeric(arguments[1]) || !Widens(arguments[1], element))) return Reject(pc);
                        Allocates = true; ResizesArrays = true;
                        result = arguments[0];
                    } else if (callee >= BuiltinBase) {
                        if (count != 1 || !Numeric(arguments[0])) return Reject(pc);
                        result = callee - BuiltinBase - ConvertFirst + IntType;
                    } else if (Construction(callee, arguments, null) is int made) {
                        Allocates = true;
                        result = made;
                    } else if (callee >= CallableBase && !IsRegion && !StructCallee(callee, out _)) {
                        var target = signatureOf?.Invoke(Signature.Outer[callNames[callee - CallableBase]].Function!);
                        if (target == null || target.Parameters.Length != count) return Reject(pc);
                        // An element of an array of any values passes to an 陣列 parameter after a tag check at the call. A
                        // clone takes exactly its parameters' types (any array of that element), as its entry would.
                        for (int i = 0; i < count; i++)
                            if (target.Clone ? !(arguments[i] == target.Parameters[i] || IsArray(arguments[i]) && IsArray(target.Parameters[i]) && (arguments[i] & 15) == (target.Parameters[i] & 15)
                                    || target.Parameters[i] == AnyArrayType && (IsRef(arguments[i]) || arguments[i] == AnyValueType)
                                    || target.Parameters[i] == StringType && arguments[i] == AnyValueType)
                                : !Widens(arguments[i], target.Parameters[i]) && !(arguments[i] == AnyValueType && target.Parameters[i] is AnyArrayType or StringType)) return Reject(pc);
                        result = target.Result;
                    } else return Reject(pc);
                    Pop(count + 1); stack.Add(result); break;
                }
                case IrOperation.Return:
                    if (IsRegion || Top() != Signature.Result) return Reject(pc);
                    returns = true; continue;
                default: return Reject(pc); // Closures, heap objects, members, strings and errors stay generic.
            }
            work.Enqueue((pc + 1, stack.ToArray()));
        }
        // A loop head is a deoptimization site: its operands must all be materializable.
        foreach (var (head, operands) in states)
            if ((operands.Contains(MixedType) || IsRegion && operands.Length > 0)
                && code.Take(n).Select((op, at) => (op, at)).Any(x => x.op.Code == IrOperation.Jump && x.op.Operand == head && head <= x.at)) {
                Rejection = $"loop head {head} has operands a region or deoptimization cannot keep ({string.Join(",", operands)})"; return false;
            }
        // The generic body keeps type-stable slots in registers: a region must agree with their types.
        if (IsRegion)
            foreach (var (slot, tag) in plan.RegisterSlots)
                if (slotTypes[slot] != Unknown && slotTypes[slot] != tag) { Rejection = $"slot {slot} disagrees with its register type"; return false; }
        return returns;
    }

    // ---- emission -----------------------------------------------------------------------------------------------

    private static byte WasmType(int type) => Wide(type) ? (byte)0x7c : (byte)0x7f;

    /// <summary>The payload of an integer or Boolean value (always an exact 32-bit integer) as an i32, without the
    /// branches of a saturating conversion: adding 1.5 * 2^52 leaves the integer in the low mantissa bits.</summary>
    internal static void ExactInteger(BinaryWriter w)
    {
        F(w, 6755399441055744.0); w.Write((byte)0xa0); // f64.add
        w.Write((byte)0xbd); w.Write((byte)0xa7);    // i64.reinterpret_f64, i32.wrap_i64
    }

    internal void WriteType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 3 + Pointers.Length + Signature.Parameters.Length);
        for (int i = 0; i < 3 + Pointers.Length; i++) w.Write((byte)0x7f);
        foreach (int type in Signature.Parameters) w.Write(WasmType(type));
        U(w, 2); w.Write(WasmType(Signature.Result)); w.Write((byte)0x7f);
    }

    /// <summary>The resume implementation: (depth, budget, closure, pointers..., site, frame) -> (result, budget).</summary>
    internal void WriteResumeType(BinaryWriter w)
    {
        w.Write((byte)0x60); U(w, 5 + Pointers.Length);
        for (int i = 0; i < 5 + Pointers.Length; i++) w.Write((byte)0x7f);
        U(w, 2); w.Write(WasmType(Signature.Result)); w.Write((byte)0x7f);
    }

    /// <summary>Where a deoptimized activation can resume natively: site 1 is the entry, site 2 + i the i-th of the loop
    /// heads and the continuations after direct calls (where a callee's deoptimization left this activation).</summary>
    private int[] LoopHeads => constructs!.Where(c => c.Loop).Select(c => c.Start).OrderBy(pc => pc).ToArray();
    private int[] ResumeTargets => LoopHeads.Concat(states.Where(p => p.Key < body.Code.Length && body.Code[p.Key].Code == IrOperation.Call
        && p.Value[p.Value.Length - body.Code[p.Key].Operand - 1] is >= CallableBase and < BuiltinBase).Select(p => p.Key + 1)).Distinct().OrderBy(pc => pc).ToArray();
    private int SiteOf(int pc) => pc == 0 ? 1 : 2 + Array.IndexOf(ResumeTargets, pc);
    /// <summary>A region's loop heads and the site its function resumes at for each.</summary>
    internal (int Head, int Site)[] HeadSites => LoopHeads.Select(h => (h, SiteOf(h))).ToArray();

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
        } else {
            // A pending collection runs in the generic body's entry checkpoint; native code does not poll for it.
            Get(w, 2); Memory(w, I32Load, 2, ExecutionPoll); w.Write(new byte[] { 0x04, 0x40 }); I(w, 0); w.Write(new byte[] { 0x0f, 0x0b });
            Get(w, 2); Memory(w, I32Load, 2, ExecutionNumericClosure); Set(w, closure);
        }
        Get(w, closure); Memory(w, I32Load, 2, ClosureScope); Set(w, scope);
        for (int k = 0; k < Pointers.Length; k++) {
            string name = Pointers[k];
            Get(w, scope); I(w, text(name)); w.Write(Call); U(w, runtime["aqua_scope_get"]); Set(w, Pointer(k));
            Unless(() => Get(w, Pointer(k)));
            var expected = natives.Values.First(n => n.OwnPointers.ContainsKey(name)).OwnPointers[name];
            if (expected.Tag == StructTypeMarker || IsStructType(expected.Tag)) {
                // A struct type binding (or a struct-typed binding) must hold that type (or null).
                int pointer = Pointer(k);
                Unless(() => StructCheck(w, () => { Get(w, pointer); Memory(w, I32Load, 2, ValueTag); }, () => { Get(w, pointer); Memory(w, I32Load, 2, ValueRef); },
                    expected.Tag == StructTypeMarker ? expected.Callee : expected.Tag, expected.Tag == StructTypeMarker));
                continue;
            }
            Unless(() => { Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueTag); I(w, IsRef(expected.Tag) ? ArrayTag : expected.Tag); w.Write((byte)0x46); });
            // A loose array (an untyped binding a clone speculates on) must hold only elements of its type.
            if (IsLoose(expected.Tag))
                Unless(() => { Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueRef); I(w, expected.Tag & 15); w.Write(Call); U(w, runtime["rt_arrayKind"]); });
            // A typed array binding must hold an array locked to that element type.
            else if (IsArray(expected.Tag))
                Unless(() => { Get(w, Pointer(k)); Memory(w, I32Load, 2, ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, expected.Tag & 15); w.Write((byte)0x46); });
            if (expected.Callee >= 0 && expected.Tag == ClosureTag) {
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
        // A core global (長度, 整數, ...) lives in the host's builtin scope, never in the closure's chain: no binding of its
        // name there means the builtin; a binding (a user's shadowing name) must hold that very builtin.
        foreach (var (name, id) in Guards) {
            Get(w, scope); I(w, text(name)); w.Write(Call); U(w, runtime["aqua_scope_get"]); Set(w, scratch);
            // The portable array module: any binding of its name shadows it.
            if (id == ArrayModule) { Unless(() => { Get(w, scratch); w.Write((byte)0x45); }); continue; }
            Unless(() => {
                Get(w, scratch); w.Write(new byte[] { 0x45, 0x04, 0x7f }); I(w, 1); w.Write((byte)0x05);
                Get(w, scratch); Memory(w, I32Load, 2, ValueTag); I(w, BuiltinTag); w.Write((byte)0x46);
                Get(w, scratch); Memory(w, I32Load, 2, ValueRef); I(w, id); w.Write(new byte[] { 0x46, 0x71, 0x0b });
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
                    if (type == AnyArrayType) { Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46); }
                    else if (Signature.Clone && IsArray(type)) {
                        // A clone's array argument: any array (typed or untyped) of only that element type.
                        Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46); w.Write(new byte[] { 0x04, 0x7f });
                        Get(w, 0); Memory(w, I32Load, 2, offset + ValueRef); I(w, type & 15); w.Write(Call); U(w, runtime["rt_arrayKind"]);
                        w.Write((byte)0x05); I(w, 0); w.Write((byte)0x0b);
                    }
                    else if (Signature.Clone) { Get(w, scratch); I(w, type); w.Write((byte)0x46); }
                    else if (IsStructType(type)) StructCheck(w, () => Get(w, scratch), () => { Get(w, 0); Memory(w, I32Load, 2, offset + ValueRef); }, type, false);
                    else if (IsArray(type)) {
                        // Only an array an annotation check already locked to this element type.
                        Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46);
                        Get(w, scratch); I(w, ArrayTag); w.Write((byte)0x46); w.Write(new byte[] { 0x04, 0x7f });
                        Get(w, 0); Memory(w, I32Load, 2, offset + ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, type & 15); w.Write((byte)0x46);
                        w.Write((byte)0x05); I(w, 0); w.Write(new byte[] { 0x0b, 0x71 });
                    }
                    else if (type is IntType or BoolType or StringType) { Get(w, scratch); I(w, type); w.Write((byte)0x46); }
                    else { Get(w, scratch); I(w, IntType); w.Write((byte)0x4f); Get(w, scratch); I(w, type); w.Write(new byte[] { 0x4d, 0x71 }); }
                });
            }
        Get(w, 2); w.Write((byte)0x24); U(w, execution);
        void Budget() { Get(w, 2); Memory(w, I32Load, 2, ExecutionBudget); }
        void CallEntry() {
            I(w, 0); Budget(); Get(w, closure);
            for (int k = 0; k < Pointers.Length; k++) Get(w, Pointer(k));
            for (int p = 0; p < Signature.Parameters.Length; p++) {
                int type = Signature.Parameters[p];
                if (IsPointer(type)) { Get(w, 0); Memory(w, I32Load, 2, argumentBase + p * ValueSize + ValueRef); continue; }
                Get(w, 0); Memory(w, F64Load, 3, argumentBase + p * ValueSize + ValueNumber);
                if (type is IntType or BoolType) ExactInteger(w);
                else if (type == FloatType) w.Write(new byte[] { 0xb6, 0xbb });
            }
            w.Write(Call); U(w, implementation);
        }
        void Results() { Set(w, scratch); if (Wide(Signature.Result)) Set(w, resultF); else Set(w, resultI); }
        if (resume.HasValue) {
            Get(w, 0); Memory(w, I32Load, 2, FrameNativeSite); I(w, 1); w.Write(new byte[] { 0x46, 0x04, 0x40 });
            CallEntry(); Results();
            w.Write((byte)0x05);
            I(w, 0); Budget(); Get(w, closure);
            for (int k = 0; k < Pointers.Length; k++) Get(w, Pointer(k));
            Get(w, 0); Memory(w, I32Load, 2, FrameNativeSite); Get(w, 0);
            w.Write(Call); U(w, resume.Value); Results();
            w.Write((byte)0x0b);
        } else { CallEntry(); Results(); }
        // A returned budget: store it (clamped like the generic checkpoint) and the result. int.MinValue: the signal
        // holds 5 after a raised error, otherwise the outermost generic frame of a deoptimization.
        Get(w, scratch); I(w, int.MinValue); w.Write(new byte[] { 0x46, 0x04, 0x40 });
        w.Write((byte)0x23); U(w, signal); Set(w, status); I(w, 0); w.Write((byte)0x24); U(w, signal);
        Get(w, status); I(w, 5); w.Write(new byte[] { 0x46, 0x04, 0x40 }); I(w, 5); w.Write(new byte[] { 0x0f, 0x0b });
        Get(w, 1); Get(w, status); Memory(w, I32Store, 2, ValueRef); I(w, 4); w.Write(new byte[] { 0x0f, 0x0b });
        Get(w, 2); Get(w, scratch); I(w, 0); Get(w, scratch); I(w, 0); w.Write(new byte[] { 0x4a, 0x1b }); Memory(w, I32Store, 2, ExecutionBudget);
        Get(w, 1);
        if (IsStructType(Signature.Result)) { I(w, StructTag); I(w, NullTag); Get(w, resultI); w.Write((byte)0x1b); }
        else I(w, IsRef(Signature.Result) ? ArrayTag : Signature.Result);
        Memory(w, I32Store, 2, ValueTag);
        Get(w, 1); if (IsPointer(Signature.Result)) Get(w, resultI); else I(w, 0); Memory(w, I32Store, 2, ValueRef);
        Get(w, 1);
        if (Signature.Result == IntType) { Get(w, resultI); w.Write((byte)0xb7); }
        else if (Signature.Result == BoolType) { Get(w, resultI); w.Write((byte)0xb8); }
        else if (IsPointer(Signature.Result)) F(w, 0);
        else Get(w, resultF);
        Memory(w, F64Store, 3, ValueNumber);
        I(w, 1); w.Write((byte)0x0b);
    }

    /// <summary>Pushes whether a value (its tag and reference) is a struct of the type with code (or null), or, for a struct
    /// type binding, that struct type.</summary>
    private static void StructCheck(BinaryWriter w, Action tag, Action reference, int code, bool type) {
        tag(); I(w, type ? StructTypeTag : StructTag); w.Write(new byte[] { 0x46, 0x04, 0x7f });
        reference(); if (!type) Memory(w, I32Load, 2, StructShape); Memory(w, I32Load, 2, ShapeId); I(w, code >> 6); w.Write((byte)0x46);
        w.Write((byte)0x05);
        if (type) I(w, 0); else { tag(); I(w, NullTag); w.Write((byte)0x46); }
        w.Write((byte)0x0b);
    }

    internal void EmitImplementation(BinaryWriter w, IReadOnlyDictionary<int, (int Function, WasmNativeFunction Native)> natives,
        IReadOnlyDictionary<string, int> runtime, int signal, int execution, bool resume = false,
        Func<string, int>? text = null)
    {
        bool region = IsRegion;
        if (region) resume = true; // A region always enters at a loop head from the generic frame.
        // The body whose instructions are being emitted: this one, or an inlined callee (see Inlinable).
        WasmNativeFunction cur = this;
        var code = body.Code; int n = code.Length;
        var fuel = WasmFuel.Checkpoints(body);
        var starts = WasmBodyEmitter.BlockStarts(body, plan);
        int BlockOf(int pc) { int b = Array.BinarySearch(starts, pc); if (b < 0) throw new InvalidOperationException("Native deoptimization target is not a generic block."); return b; }

        // ---- locals --------------------------------------------------------------------------------------------
        // Parameters: recursion depth 0, own closure 1, pointers, then the arguments, or (resuming) the site and the
        // deoptimized frame. The execution is a global the adapter sets.
        // A region's parameters are (frame, site, unused); its pointers are locals it resolves itself.
        int pointerBase = 3, argumentBase = 3 + Pointers.Length;
        const int depthParameter = 0, budgetParameter = 1, selfParameter = 2;
        int first = region ? 3 + Pointers.Length : argumentBase + (resume ? 2 : Signature.Parameters.Length);
        int siteParameter = region ? 1 : argumentBase, frameParameter = region ? 0 : argumentBase + 1;
        void Execution() { Op(0x23); U(w, execution); }
        int slots = plan.Locals, cap = capacity;
        // Outer typed bindings live in locals: loaded at entry, written back before every exit, deoptimization and call
        // (a callee reads and writes the bindings themselves), and reloaded after a call.
        var cached = Pointers.Where(p => !OwnPointers.TryGetValue(p, out var e) || e.Callee < 0).ToArray();
        // Every local that can hold a typed array (slots, operands, cached outer bindings) has two companions: the
        // array's count and values pointer. They are copied with the array and loaded only where an array enters a
        // local from memory, so loops index arrays without reloading their headers. Native code never resizes an
        // array; a copy-on-write (rt_nativeWritable) or a callee can replace an array's values, so both refresh them.
        int i32Own = slots * 2 + cap + 8 + cached.Length + 2 * (slots + cap + cached.Length), f64Own = slots + cap + 3 + cached.Length;
        // Each inlined callee gets its own slots, flags, operands and companions after this body's locals.
        var inlined = new List<WasmNativeFunction>();
        foreach (var (at, operands) in states)
            if (at < n && code[at].Code == IrOperation.Call && operands[operands.Length - code[at].Operand - 1] is int callee and >= CallableBase and < BuiltinBase
                && !StructCallee(callee, out _)
                && natives[OwnPointers.TryGetValue(callNames[callee - CallableBase], out var target) ? target.Callee : Body].Native is { Inlinable: true } inline
                && inline != this && !inlined.Contains(inline))
                inlined.Add(inline);
        int InlineI32(WasmNativeFunction f) => f.plan.Locals * 4 + f.capacity * 3;
        int InlineF64(WasmNativeFunction f) => f.plan.Locals + f.capacity;
        int i32Count = i32Own + inlined.Sum(InlineI32) + (region ? Pointers.Length : 0), f64Count = f64Own + inlined.Sum(InlineF64);
        // Where the current body's slots, flags, operands and companions start (swapped while inlining).
        int slotBase = first, flagBase = first + slots, stackBase = first + slots * 2;
        int SlotI(int s) => slotBase + s;
        int Flag(int s) => flagBase + s;
        int StackI(int k) => stackBase + k;
        int status = first + slots * 2 + cap, resultI = status + 1, frame = status + 2, tempI = status + 3, tempJ = status + 4;
        // The execution budget lives in a local between the entry checkpoint and every exit or call: nothing else
        // changes it during native code, and loop-head checkpoints then cost no memory access.
        int budget = status + 5, site = status + 6, resumeAt = status + 7;
        int CacheI(int c) => status + 8 + c;
        int companions = status + 8 + cached.Length;
        int slotCountBase = companions, stackCountBase = companions + 2 * slots;
        int SlotCount(int s) => slotCountBase + 2 * s;
        int StackCount(int k) => stackCountBase + 2 * k;
        int CacheCount(int c) => companions + 2 * (slots + cap + c);
        // The local may go negative where the generic checkpoint saturates at 0: both yield at the next checkpoint, and
        // the stored budget is clamped to the generic value.
        void SaveBudget() { Execution(); Get(w, budget); I(w, 0); Get(w, budget); I(w, 0); Op(0x4a, 0x1b); Memory(w, I32Store, 2, ExecutionBudget); }
        void LoadBudget() { Execution(); Memory(w, I32Load, 2, ExecutionBudget); Set(w, budget); }
        int f64First = first + i32Count - (region ? Pointers.Length : 0);
        int slotFBase = f64First, stackFBase = f64First + slots;
        int SlotF(int s) => slotFBase + s;
        int StackF(int k) => stackFBase + k;
        int resultF = f64First + slots + cap, tempF = resultF + 1, tempG = resultF + 2;
        int CacheF(int c) => resultF + 3 + c;
        U(w, 2); U(w, i32Count); w.Write((byte)0x7f); U(w, f64Count); w.Write((byte)0x7c);
        // A region's results: the generic block to continue at, -1 to run the generic body now (no budget left or a
        // collection pending), -2 when a binding or slot has another type (generic from now on), -3 after an error.
        void RegionExit(int block, int pc) { WriteSlots(pc); SaveOuter(); SaveBudget(); I(w, block); Op(Return); }
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
            if (IsPointer(type)) { Memory(w, I32Load, 2, offset + ValueRef); return; }
            Memory(w, F64Load, 3, offset + ValueNumber); if (!Wide(type)) ExactInteger(w);
        }

        int CachedType(string name) {
            if (OwnPointers.TryGetValue(name, out var own)) return own.Tag;
            foreach (var native in natives.Values) if (native.Native.OwnPointers.TryGetValue(name, out var e)) return e.Tag;
            throw new InvalidOperationException("Unknown outer binding.");
        }
        // An array's companions (count, then values) from the array in local, or copied from another local's.
        void Describe(int local, int count) {
            Get(w, local); Memory(w, I32Load, 2, ArrayCount); Set(w, count);
            Get(w, local); Memory(w, I32Load, 2, ArrayValues); Set(w, count + 1);
        }
        void CopyCompanions(int type, int from, int to) { if (!IsRef(type)) return; Get(w, from); Set(w, to); Get(w, from + 1); Set(w, to + 1); }
        // After a copy-on-write or a call, every array local's values pointer is read again (its count is unchanged).
        // Undeclared slots hold 0, never a stale reference.
        void RefreshValues(int[] stack) {
            void Refresh(int local, int count) { Get(w, local); Memory(w, I32Load, 2, ArrayValues); Set(w, count + 1); }
            for (int s = 0; s < slots; s++) if (IsRef(slotTypes[s])) Refresh(SlotI(s), SlotCount(s));
            for (int k = 0; k < stack.Length; k++) if (IsRef(stack[k])) Refresh(StackI(k), StackCount(k));
            for (int c = 0; c < cached.Length; c++) if (IsRef(CachedType(cached[c]))) Refresh(CacheI(c), CacheCount(c));
        }
        // After an append (here or in a callee) counts change too: every array local's companions are read again.
        void Redescribe(int[] stack) {
            for (int s = 0; s < slots; s++) if (IsRef(slotTypes[s])) Describe(SlotI(s), SlotCount(s));
            for (int k = 0; k < stack.Length; k++) if (IsRef(stack[k])) Describe(StackI(k), StackCount(k));
            for (int c = 0; c < cached.Length; c++) if (IsRef(CachedType(cached[c]))) Describe(CacheI(c), CacheCount(c));
        }
        void ReadOuter(string name, int type) { int c = CacheOf(name); Local(type, CacheI(c), CacheF(c)); }
        void WriteOuterFrom(string name, int type, Action value) { int c = CacheOf(name); value(); SetLocal(type, CacheI(c), CacheF(c)); }
        void LoadOuter() { for (int c = 0; c < cached.Length; c++) { int type = CachedType(cached[c]);
            LoadPayload(() => Get(w, PointerOf(cached[c])), 0, type); SetLocal(type, CacheI(c), CacheF(c));
            if (IsRef(type)) Describe(CacheI(c), CacheCount(c)); } }
        void SaveOuter() {
            foreach (string name in Written) {
                int c = CacheOf(name), type = CachedType(name);
                Get(w, PointerOf(name)); Local(type, CacheI(c), CacheF(c));
                if (IsStructType(type)) {
                    Memory(w, I32Store, 2, ValueRef);
                    Get(w, PointerOf(name)); I(w, StructTag); I(w, NullTag); Local(type, CacheI(c), CacheF(c)); Op(0x1b); Memory(w, I32Store, 2, ValueTag);
                }
                else if (IsRef(type) || type == StringType) Memory(w, I32Store, 2, ValueRef); else { AsPayload(type); Memory(w, F64Store, 3, ValueNumber); }
            }
        }

        void Zero() { if (Wide(Signature.Result)) F(w, 0); else I(w, 0); }
        // The slots a generic frame holds at pc: the plan proves most of them declared or not there, so only uncertain
        // ones test their flag, and values that are dead at pc stay dead (no copy is kept live across a loop for it).
        // A region leaving to the generic body also marks the undeclared ones, since its frame may hold old values.
        void StoreSlots(int pc, bool undeclare) {
            var declared = plan.SlotDecl[pc]!;
            for (int s = 0; s < slots; s++) {
                int type = slotTypes[s];
                if (type == Unknown || declared[s] == WasmScopePlan.No && !undeclare) continue;
                void Declared() => StoreValue(FrameValues + ValueSize * s, type, () => { Local(type, SlotI(s), SlotF(s)); if (type == StringType) { I(w, -2); Op(0x71); } else if (!IsRef(type)) AsPayload(type); });
                void Undeclared() { Get(w, frame); I(w, -1); Memory(w, I32Store, 2, FrameValues + ValueSize * s + ValueTag); }
                if (declared[s] == WasmScopePlan.Yes) { Declared(); continue; }
                if (declared[s] == WasmScopePlan.No) { Undeclared(); continue; }
                Get(w, Flag(s)); Op(If, 0x40); Declared();
                if (undeclare) { Op(Else); Undeclared(); }
                Op(End);
            }
        }
        void WriteSlots(int pc) => StoreSlots(pc, true);
        void Raise(Action value) { value(); Op(0x24); U(w, signal); Zero(); I(w, int.MinValue); Op(Return); }
        void ExitWith(int type, int k) { SaveOuter(); PushStack(type, k); Get(w, budget); Op(Return); }
        // Errors and deoptimizations leave the body through one handler after it, so loops contain no runtime calls
        // (which would make every value live across them spill).
        var sites = new List<(int Kind, int Block, int[] Operands, int Message)>();
        // InlineSite: an inlined callee's entry checkpoint; CheckSite: an argument of an 陣列 parameter that is not an array
        // (the callee's generic body raises the annotation error at its entry).
        // IndexSite: a read outside a loose array in a region (it continues generically at that read).
        const int ErrorSite = 0, CheckpointSite = 1, CallSite = 2, InlineSite = 3, CheckSite = 4, IndexSite = 5;
        void ToHandler(int kind, int block, int[] operands, int message) {
            sites.Add((kind, block, operands, message)); I(w, sites.Count - 1); Set(w, site);
            Op(Br); U(w, Label(c => c.End == int.MaxValue) + 1); // Every site sits in one if.
        }
        void Fail(int message) => ToHandler(ErrorSite, 0, Array.Empty<int>(), message);

        // An element of an array of any values (operand k) as the generic value it is.
        void StoreAny(int offset, int k) {
            Get(w, frame); Get(w, StackCount(k)); Memory(w, I32Store, 2, offset + ValueTag);
            Get(w, frame); Get(w, StackI(k)); Memory(w, I32Store, 2, offset + ValueRef);
            Get(w, frame); Get(w, StackF(k)); Memory(w, F64Store, 3, offset + ValueNumber);
        }
        // Deoptimization: a generic frame resuming at block of pc with the given operands, declared slots written.
        void StoreValue(int offset, int type, Action payload) {
            if (IsStructType(type)) {
                payload(); Set(w, tempI);
                Get(w, frame); I(w, StructTag); I(w, NullTag); Get(w, tempI); Op(0x1b); Memory(w, I32Store, 2, offset + ValueTag);
                Get(w, frame); Get(w, tempI); Memory(w, I32Store, 2, offset + ValueRef);
                Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                return;
            }
            if (IsRef(type) || type == StringType) {
                Get(w, frame); I(w, type == StringType ? StringTag : ArrayTag); Memory(w, I32Store, 2, offset + ValueTag);
                Get(w, frame); payload(); Memory(w, I32Store, 2, offset + ValueRef);
                Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                return;
            }
            Get(w, frame); I(w, type == NullType ? NullTag : type); Memory(w, I32Store, 2, offset + ValueTag);
            Get(w, frame); I(w, 0); Memory(w, I32Store, 2, offset + ValueRef);
            Get(w, frame); payload(); Memory(w, F64Store, 3, offset + ValueNumber);
        }
        void Materialize(int block, int pc, int[] operands) {
            Execution(); Get(w, selfParameter); I(w, block); I(w, operands.Length); Op(Call); U(w, runtime["rt_nativeFrame"]); Set(w, frame);
            StoreSlots(pc, false);
            WriteOperands(operands);
        }
        // The operands of the frame (in local frame) as generic values above its slots.
        void WriteOperands(int[] operands) {
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
                        Get(w, frame); Get(w, selfParameter); Memory(w, I32Store, 2, offset + ValueRef);
                        Get(w, frame); F(w, 0); Memory(w, F64Store, 3, offset + ValueNumber);
                    } else {
                        int pointer = PointerOf(name);
                        for (int o = 0; o <= 8; o += 8) { Get(w, frame); Get(w, pointer); Memory(w, I64Load, 3, o); Memory(w, I64Store, 3, offset + o); }
                    }
                } else if (type == AnyValueType) StoreAny(offset, k);
                else if (Value(type)) StoreValue(offset, type, () => { PushStack(type, k); if (!IsRef(type)) AsPayload(type); });
                else StoreValue(offset, type, () => F(w, 0));
            }
        }

        int depthLimit = DepthLimit;
        // The generic checkpoint: yield (here: deoptimize) when the budget is exhausted, a collection is pending or, at
        // the entry, the native depth bound is reached; otherwise consume the weight with saturation. A pending
        // collection is armed only by allocation, which native code never does, so loops need not poll.
        void Checkpoint(int pc, bool entry) {
            if (!fuel.TryGetValue(pc, out int weight)) return;
            if (entry) {
                Get(w, budgetParameter); Set(w, budget);
                // The adapters check for a pending collection before native code runs; native code only allocates
                // when it leaves (a deoptimization) or copies an array on write, which loops do not poll for either.
                Get(w, depthParameter); I(w, depthLimit); Op(0x4f);
                Get(w, budget); Op(0x45, 0x72);
            } else {
                Get(w, budget); I(w, 0); Op(0x4c);
                // A loop that allocates leaves for a pending collection too: the generic checkpoint collects.
                if (Allocates) { Execution(); Memory(w, I32Load, 2, ExecutionPoll); Op(0x72); }
            }
            Op(If, 0x40); ToHandler(CheckpointSite, BlockOf(pc), states[pc], pc); Op(End);
            Get(w, budget); I(w, weight); Op(0x6b); Set(w, budget);
        }

        // ---- structured control flow -------------------------------------------------------------------------------
        var constructs = this.constructs!;
        // While an inlined callee is emitted: where its result goes (instead of returning).
        Action<int, int>? inlineReturn = null;
        // The outermost block is the handler's entry.
        var open = new List<(int Start, int End, bool Loop)> { (-1, int.MaxValue, false) };
        int Label(Func<(int Start, int End, bool Loop), bool> match) {
            for (int d = open.Count - 1; d >= 0; d--) if (match(open[d])) return open.Count - 1 - d;
            throw new InvalidOperationException("Native branch target is not open.");
        }

        // ---- resuming: skip blocks lead from the top through every enclosing construct to the resumed loop head ------
        var targets = region ? LoopHeads : ResumeTargets;
        var skips = new List<(int Owner, int End, int[] Sites)>(); // Owner: index into constructs, or -1 for the top.
        if (resume)
            foreach (int target in targets) {
                // The constructs that enclose the target, outermost first; a segment skips from inside each to the next,
                // and the last one to the target itself.
                var path = constructs.Select((c, i) => (c, i)).Where(p => p.c.Start < target && target < p.c.End)
                    .OrderBy(p => p.c.Start).ThenByDescending(p => p.c.End).ThenBy(p => p.c.Loop).ToList();
                for (int k = 0; k <= path.Count; k++) {
                    int owner = k == 0 ? -1 : path[k - 1].i, next = k < path.Count ? path[k].c.Start : target;
                    // Nothing to skip between two constructs opened at the same pc unless the outer one is a loop (its checkpoint).
                    if (k > 0 && path[k - 1].c.Start == next && !path[k - 1].c.Loop) continue;
                    int existing = skips.FindIndex(x => x.Owner == owner && x.End == next);
                    if (existing < 0) skips.Add((owner, next, new[] { SiteOf(target) }));
                    else skips[existing] = (owner, next, skips[existing].Sites.Append(SiteOf(target)).ToArray());
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
        if (region) {
            Get(w, frameParameter); Set(w, frame);
            Get(w, frame); Memory(w, I32Load, 2, FrameExecution); Op(0x24); U(w, execution);
            Execution(); Memory(w, I32Load, 2, ExecutionBudget); Op(0x45);
            Execution(); Memory(w, I32Load, 2, ExecutionPoll); Op(0x72, If, 0x40); I(w, -1); Op(Return); Op(End);
            void Unless(Action condition) { condition(); Op(0x45, If, 0x40); I(w, -2); Op(Return); Op(End); }
            void Lookup(string name, int into) {
                Get(w, frame); Memory(w, I32Load, 2, FrameScope); I(w, text!(name)); Op(Call); U(w, runtime["aqua_scope_get"]); Set(w, into);
                Unless(() => Get(w, into));
            }
            for (int k = 0; k < Pointers.Length; k++) {
                int pointer = pointerBase + k, tag = OwnPointers[Pointers[k]].Tag;
                Lookup(Pointers[k], pointer);
                if (tag == StructTypeMarker || IsStructType(tag)) {
                    int structCode = tag == StructTypeMarker ? OwnPointers[Pointers[k]].Callee : tag;
                    Unless(() => StructCheck(w, () => { Get(w, pointer); Memory(w, I32Load, 2, ValueTag); }, () => { Get(w, pointer); Memory(w, I32Load, 2, ValueRef); }, structCode, tag == StructTypeMarker));
                    continue;
                }
                Unless(() => { Get(w, pointer); Memory(w, I32Load, 2, ValueTag); I(w, IsRef(tag) ? ArrayTag : tag); Op(0x46); });
                if (IsLoose(tag)) Unless(() => { Get(w, pointer); Memory(w, I32Load, 2, ValueRef); I(w, tag & 15); Op(Call); U(w, runtime["rt_arrayKind"]); });
                else if (IsArray(tag)) Unless(() => { Get(w, pointer); Memory(w, I32Load, 2, ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, tag & 15); Op(0x46); });
            }
            // A core global: no binding of its name means the builtin; a binding must hold that very builtin.
            foreach (var (name, id) in Guards) {
                Get(w, frame); Memory(w, I32Load, 2, FrameScope); I(w, text!(name)); Op(Call); U(w, runtime["aqua_scope_get"]); Set(w, tempI);
                if (id == ArrayModule) { Unless(() => { Get(w, tempI); Op(0x45); }); continue; }
                Unless(() => { Get(w, tempI); Op(0x45, If, 0x7f); I(w, 1); Op(Else);
                    Get(w, tempI); Memory(w, I32Load, 2, ValueTag); I(w, BuiltinTag); Op(0x46); Get(w, tempI); Memory(w, I32Load, 2, ValueRef); I(w, id); Op(0x46, 0x71); Op(End); });
            }
            // Every slot the region holds is undeclared or has the region's type.
            for (int s = 0; s < slots; s++) {
                int type = slotTypes[s], offset = FrameValues + ValueSize * s;
                if (type == Unknown) continue;
                Get(w, frame); Memory(w, I32Load, 2, offset + ValueTag); Set(w, tempI);
                Get(w, tempI); I(w, -1); Op(0x47); Set(w, Flag(s));
                Unless(() => {
                    Get(w, Flag(s)); Op(0x45);
                    if (IsStructType(type)) StructCheck(w, () => Get(w, tempI), () => { Get(w, frame); Memory(w, I32Load, 2, offset + ValueRef); }, type, false);
                    else { Get(w, tempI); I(w, IsRef(type) ? ArrayTag : type); Op(0x46); }
                    // A loose array's elements are checked only when the tag is an array (rt_arrayKind reads the array).
                    if (IsLoose(type)) { Op(If, 0x7f); Get(w, frame); Memory(w, I32Load, 2, offset + ValueRef); I(w, type & 15); Op(Call); U(w, runtime["rt_arrayKind"]); Op(Else); I(w, 0); Op(End); }
                    else if (IsArray(type)) { Get(w, frame); Memory(w, I32Load, 2, offset + ValueRef); Memory(w, I32Load, 2, ArrayElement); I(w, type & 15); Op(0x46, 0x71); }
                    Op(0x72);
                });
                LoadPayload(() => Get(w, frame), offset, type);
                if (IsRef(type)) { I(w, 0); Get(w, Flag(s)); Op(0x1b); }
                SetLocal(type, SlotI(s), SlotF(s));
                if (IsRef(type)) Describe(SlotI(s), SlotCount(s));
            }
            Get(w, siteParameter); Set(w, resumeAt);
        } else if (resume) {
            // The deoptimized frame: declared slots (an undeclared slot keeps its flag clear), then the operands below
            // the resumed loop head.
            for (int s = 0; s < slots; s++) {
                int type = slotTypes[s];
                if (type == Unknown) { I(w, 0); Set(w, Flag(s)); continue; }
                Get(w, frameParameter); Memory(w, I32Load, 2, FrameValues + ValueSize * s + ValueTag); I(w, -1); Op(0x47); Set(w, Flag(s));
                LoadPayload(() => Get(w, frameParameter), FrameValues + ValueSize * s, type);
                if (IsRef(type)) { I(w, 0); Get(w, Flag(s)); Op(0x1b); }
                SetLocal(type, SlotI(s), SlotF(s));
                if (IsRef(type)) Describe(SlotI(s), SlotCount(s));
            }
            foreach (int target in targets) {
                var operands = states[target];
                if (!operands.Any(t => Value(t) || t == AnyValueType)) continue;
                Get(w, siteParameter); I(w, SiteOf(target)); Op(0x46, If, 0x40);
                for (int k = 0; k < operands.Length; k++) {
                    if (operands[k] == AnyValueType) {
                        int offset = FrameValues + ValueSize * (plan.Locals + k);
                        Get(w, frameParameter); Memory(w, I32Load, 2, offset + ValueTag); Set(w, StackCount(k));
                        Get(w, frameParameter); Memory(w, I32Load, 2, offset + ValueRef); Set(w, StackI(k));
                        Get(w, frameParameter); Memory(w, F64Load, 3, offset + ValueNumber); Set(w, StackF(k));
                        continue;
                    }
                    if (!Value(operands[k])) continue;
                    LoadPayload(() => Get(w, frameParameter), FrameValues + ValueSize * (plan.Locals + k), operands[k]);
                    PopStack(operands[k], k);
                    if (IsRef(operands[k])) Describe(StackI(k), StackCount(k));
                }
                Op(End);
            }
            Get(w, siteParameter); Set(w, resumeAt);
        } else {
            for (int s = 0; s < slots; s++) { I(w, s < Signature.Parameters.Length ? 1 : 0); Set(w, Flag(s)); }
            for (int p = 0; p < Signature.Parameters.Length; p++) {
                Get(w, argumentBase + p); SetLocal(slotTypes[p], SlotI(p), SlotF(p));
                if (IsRef(slotTypes[p])) Describe(SlotI(p), SlotCount(p));
            }
        }
        Op(Block, 0x40);
        if (region) LoadBudget(); else if (resume) { Get(w, budgetParameter); Set(w, budget); } else Checkpoint(0, true);
        LoadOuter();
        OpenSkips(-1);

        void EmitOps(int startPc, int stopPc) {
        for (int pc = startPc; pc <= stopPc; pc++) {
            while (open.Count > 1 && open[^1].End == pc) { open.RemoveAt(open.Count - 1); Op(End); }
            // Arriving at the resumed target ends the skipping.
            if (inlineReturn == null && resume && Array.IndexOf(targets, pc) >= 0) { Get(w, resumeAt); I(w, SiteOf(pc)); Op(0x46, If, 0x40); I(w, 0); Set(w, resumeAt); Op(End); }
            foreach (var c in constructs.Where(c => c.Start == pc).OrderByDescending(c => c.End).ThenBy(c => c.Loop)) {
                if (c.End > open[^1].End) throw new InvalidOperationException("Native control flow is not structured.");
                open.Add(c); Op(c.Loop ? Loop : Block, 0x40);
                if (inlineReturn == null) OpenSkips(constructs.IndexOf(c));
                if (c.Loop) Checkpoint(pc, false);
            }
            if (!cur.states.TryGetValue(pc, out var stack)) continue;
            int depth = stack.Length, top = depth - 1, below = depth - 2;
            if (inlineReturn == null && region && pc == Stop) { RegionExit(BlockOf(Stop), Stop); break; }
            if (pc == n) { Exit(stack[top], top); break; }
            var op = code[pc]; int operand = op.Operand;
            int TopType(int k = 1) => stack[depth - k];
            switch (op.Code) {
                case IrOperation.Constant:
                    switch (cur.body.Pool[operand]) {
                        case IntegerObj v: I(w, v.Value); Set(w, StackI(depth)); break;
                        case BooleanObj v: I(w, v.Value ? 1 : 0); Set(w, StackI(depth)); break;
                        case FloatObj v: F(w, v.Value); Set(w, StackF(depth)); break;
                        case DoubleObj v: F(w, v.Value); Set(w, StackF(depth)); break;
                        // A string constant: its text, from the body's constant pool.
                        case StringObj: I(w, cur.Pool + ValueSize * operand); Memory(w, I32Load, 2, ValueRef); Set(w, StackI(depth)); break;
                    }
                    break;
                case IrOperation.Void: case IrOperation.Null: case IrOperation.Pop: case IrOperation.JumpIfBreak:
                case IrOperation.MemberFunction: // 陣列.追加: a compile-time-only callee
                    break;
                case IrOperation.Load: {
                    int slot = cur.SlotAt(pc, cur.plan.Accesses[pc]!), type = cur.states[pc + 1][depth];
                    if (slot >= 0 && type == StringType) {
                        // A string slot: an append's own loads keep the owned mark; any other load drops it (in the slot too).
                        cur.FindAppends();
                        Get(w, SlotI(slot));
                        if (!cur.appendReads!.Contains(pc)) { I(w, -2); Op(0x71); Tee(w, SlotI(slot)); }
                        Set(w, StackI(depth));
                    }
                    else if (slot >= 0) { Local(type, SlotI(slot), SlotF(slot)); PopStack(type, depth); CopyCompanions(type, SlotCount(slot), StackCount(depth)); }
                    else if (Value(type)) {
                        ReadOuter(cur.NameOf(operand), type); PopStack(type, depth);
                        CopyCompanions(type, CacheCount(CacheOf(cur.NameOf(operand))), StackCount(depth));
                    }
                    break; // A callee occupies a compile-time-only operand.
                }
                case IrOperation.Declare: {
                    int slot = cur.plan.DeclareSlot[pc], type = TopType();
                    PushStack(type, top); SetLocal(type, SlotI(slot), SlotF(slot)); I(w, 1); Set(w, Flag(slot));
                    CopyCompanions(type, StackCount(top), SlotCount(slot)); break;
                }
                case IrOperation.Assign: {
                    int slot = cur.SlotAt(pc, cur.plan.Accesses[pc]!), type = TopType();
                    if (slot >= 0) { PushStack(type, top); SetLocal(type, SlotI(slot), SlotF(slot)); CopyCompanions(type, StackCount(top), SlotCount(slot)); }
                    else {
                        string name = cur.NameOf(operand);
                        WriteOuterFrom(name, type, () => PushStack(type, top));
                        CopyCompanions(type, StackCount(top), CacheCount(CacheOf(name)));
                    }
                    break;
                }
                case IrOperation.CompoundAssign: {
                    var assignment = (Assignment)cur.body.Pool[operand];
                    int slot = cur.SlotAt(pc, cur.plan.Accesses[pc]!), right = TopType();
                    string name = assignment.Name;
                    int left = slot >= 0 ? cur.slotTypes[slot] : OwnPointers[name].Tag;
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
                    int slot = cur.SlotAt(pc, cur.plan.Accesses[pc]!), type = cur.states[pc + 1][depth];
                    string name = cur.NameOf(operand);
                    void LoadIt() { if (slot >= 0) Local(type, SlotI(slot), SlotF(slot)); else ReadOuter(name, type); }
                    void Next() { LoadIt(); if (type == IntType) { I(w, 1); Op(0x6a); } else { F(w, 1); Op(0xa0); if (type == FloatType) RoundFloat(); } }
                    if (op.Code == IrOperation.IncrementPostfix) { LoadIt(); PopStack(type, depth); }
                    if (slot >= 0) { Next(); SetLocal(type, SlotI(slot), SlotF(slot)); } else WriteOuterFrom(name, type, Next);
                    if (op.Code == IrOperation.IncrementPrefix) { LoadIt(); PopStack(type, depth); }
                    break;
                }
                case IrOperation.Add when TopType() == StringType:
                    cur.FindAppends();
                    Get(w, StackI(below)); Get(w, StackI(top));
                    Op(Call); U(w, runtime[cur.appendAt!.ContainsKey(pc) ? "rt_nativeAppend" : "rt_nativeConcat"]); Set(w, StackI(below)); break;
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
                    if (left == StringType) {
                        Get(w, StackI(below)); Get(w, StackI(top)); Op(Call); U(w, runtime["rt_nativeTextEqual"]);
                        if (op.Code == IrOperation.NotEqual) Op(0x45);
                    } else if (left is IntType or BoolType && right is IntType or BoolType || IsStructType(left) && IsStructType(right)) {
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
                case IrOperation.Member: {
                    var field = cur.StructField(TopType(), cur.NameOf(operand))!.Value; int type = FieldValueType(field.Code);
                    NullCheck(top, 4); FieldAddress(StackI(top), field.Index);
                    if (type == AnyValueType) {
                        Get(w, tempI); Memory(w, I32Load, 2, ValueTag); Set(w, StackCount(top));
                        Get(w, tempI); Memory(w, F64Load, 3, ValueNumber); Set(w, StackF(top));
                        Get(w, tempI); Memory(w, I32Load, 2, ValueRef); Set(w, StackI(top));
                    } else if (IsRef(type) || IsStructType(type)) {
                        Get(w, tempI); Memory(w, I32Load, 2, ValueRef); Set(w, StackI(top));
                        if (IsRef(type)) Describe(StackI(top), StackCount(top));
                    } else { Get(w, tempI); Memory(w, F64Load, 3, ValueNumber); if (!Wide(type)) ExactInteger(w); PopStack(type, top); }
                    break;
                }
                case IrOperation.SetMember: {
                    var field = cur.StructField(TopType(2), cur.NameOf(operand))!.Value; int value = TopType();
                    NullCheck(below, 5); FieldAddress(StackI(below), field.Index);
                    StoreField(field.Code, value, top);
                    // The result is the stored value (a numeric field's widened one).
                    if (Numeric(field.Code)) { Get(w, tempI); Memory(w, F64Load, 3, ValueNumber); if (!Wide(field.Code)) ExactInteger(w); PopStack(field.Code, below); }
                    else if (value == AnyValueType) { Get(w, StackCount(top)); Set(w, StackCount(below)); Get(w, StackI(top)); Set(w, StackI(below)); Get(w, StackF(top)); Set(w, StackF(below)); }
                    else { PushStack(value, top); PopStack(value, below); CopyCompanions(value, StackCount(top), StackCount(below)); }
                    break;
                }
                case IrOperation.CompoundMember: {
                    var assignment = (Assignment)cur.body.Pool[operand];
                    var field = cur.StructField(TopType(2), assignment.Name)!.Value; int left = field.Code, right = TopType();
                    NullCheck(below, 5); FieldAddress(StackI(below), field.Index);
                    Get(w, tempI); Set(w, resultI);
                    void LoadField() { Get(w, resultI); Memory(w, F64Load, 3, ValueNumber); if (!Wide(left)) ExactInteger(w); }
                    void StoreField_(Action number) { Get(w, resultI); number(); Memory(w, F64Store, 3, ValueNumber); }
                    var operation = assignment.Operation;
                    if (left == IntType && right == IntType) {
                        IntCheck(operation, () => PushStack(IntType, top));
                        StoreField_(() => { IntOperation(operation, LoadField, () => PushStack(IntType, top)); Op(0xb7); });
                    } else {
                        if (left == IntType && operation == IrOperation.Modulo) { AsDouble(right, top); F(w, 0); Op(0x61, If, 0x40); Fail(1); Op(End); }
                        LoadField(); if (!Wide(left)) Op(0xb7); Set(w, tempF);
                        AsDouble(right, top); Set(w, tempG);
                        FloatOperation(operation, tempF, tempG); Set(w, tempF);
                        StoreField_(() => { Get(w, tempF); if (left == IntType) { Op(0xfc, 0x02); Op(0xb7); } else if (left == FloatType) RoundFloat(); });
                    }
                    break;
                }
                case IrOperation.CheckStruct: break; // The type is proven: the operand already has it.
                case IrOperation.CallNamed: {
                    var names = ((string)cur.body.Pool[operand]).Split('\u0001');
                    Construct(stack, depth - names.Length - 1, names); break;
                }
                case IrOperation.Index when TopType(2) == StringType: {
                    Get(w, StackI(top)); Get(w, StackI(below)); Memory(w, I32Load, 2, TextLength); Op(0x49, 0x45, If, 0x40);
                    ToHandler(IndexSite, BlockOf(pc), stack, pc); Op(End);
                    Get(w, StackI(below)); Get(w, StackI(top)); I(w, 1); Op(0x74, 0x6a); Memory(w, 0x2f, 1, TextData);
                    Op(Call); U(w, runtime["rt_nativeUnitText"]); Set(w, StackI(below)); break;
                }
                case IrOperation.Index when TopType(2) == AnyArrayType: {
                    // An array of any values reads like the generic code: an element, or null outside the array.
                    Get(w, StackI(top)); Get(w, StackCount(below)); Op(0x49, If, 0x40);
                    Get(w, StackCount(below) + 1); Get(w, StackI(top)); I(w, 4); Op(0x74, 0x6a); Set(w, tempI);
                    Get(w, tempI); Memory(w, I32Load, 2, ValueTag); Set(w, StackCount(below));
                    Get(w, tempI); Memory(w, F64Load, 3, ValueNumber); Set(w, StackF(below));
                    Get(w, tempI); Memory(w, I32Load, 2, ValueRef); Set(w, StackI(below));
                    Op(Else); I(w, NullTag); Set(w, StackCount(below)); I(w, 0); Set(w, StackI(below)); F(w, 0); Set(w, StackF(below));
                    Op(End); break;
                }
                case IrOperation.Array: {
                    // rt_nativeArray allocates; every element is written before anything can collect (native code
                    // never runs a collection, and a deoptimization materializes the array's reference first).
                    int at = depth - operand;
                    I(w, operand); Op(Call); U(w, runtime["rt_nativeArray"]); Set(w, tempI);
                    Get(w, tempI); Memory(w, I32Load, 2, ArrayValues); Set(w, tempJ);
                    for (int k = 0; k < operand; k++) {
                        int type = stack[at + k], offset = ValueSize * k;
                        Get(w, tempJ); if (type == AnyValueType) Get(w, StackCount(at + k)); else if (IsStructType(type)) { I(w, StructTag); I(w, NullTag); Get(w, StackI(at + k)); Op(0x1b); } else I(w, IsRef(type) ? ArrayTag : type); Memory(w, I32Store, 2, offset + ValueTag);
                        Get(w, tempJ); if (type == AnyValueType || IsPointer(type)) Get(w, StackI(at + k)); else I(w, 0); Memory(w, I32Store, 2, offset + ValueRef);
                        Get(w, tempJ);
                        if (type == AnyValueType) Get(w, StackF(at + k)); else if (IsPointer(type)) F(w, 0); else { PushStack(type, at + k); AsPayload(type); }
                        Memory(w, F64Store, 3, offset + ValueNumber);
                    }
                    Get(w, tempI); Set(w, StackI(at)); Describe(StackI(at), StackCount(at));
                    break;
                }
                case IrOperation.Index: {
                    // Typed reads are checked: an index outside the array is an error, not null. Outside a loose array the
                    // region leaves at this read (a generic block), so the generic code reads it.
                    Get(w, StackI(top)); Get(w, StackCount(below)); Op(0x49, 0x45, If, 0x40);
                    if (IsLoose(TopType(2))) ToHandler(IndexSite, BlockOf(pc), stack, pc); else Fail(2);
                    Op(End);
                    int element = TopType(2) & 15;
                    Get(w, StackCount(below) + 1); Get(w, StackI(top)); I(w, 4); Op(0x74, 0x6a);
                    Memory(w, F64Load, 3, ValueNumber); if (!Wide(element)) ExactInteger(w);
                    PopStack(element, below); break;
                }
                case IrOperation.CheckArrayWrite:
                    Get(w, StackI(top)); Get(w, StackCount(below)); Op(0x49, 0x45, If, 0x40); Fail(3); Op(End);
                    break;
                case IrOperation.WriteIndex: {
                    int array = depth - 3, index = depth - 2, type = TopType();
                    // Shared (copy-on-write) or host-exposed storage goes through the runtime first.
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayStorage); Memory(w, I32Load, 2, ArrayStorageReferences); I(w, 1); Op(0x47);
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayExposed); Op(0x72, If, 0x40);
                    Get(w, StackI(array)); Op(Call); U(w, runtime["rt_nativeWritable"]); Op(Drop);
                    RefreshValues(stack);
                    Op(End);
                    Get(w, StackCount(array) + 1); Get(w, StackI(index)); I(w, 4); Op(0x74, 0x6a);
                    PushStack(type, top); AsPayload(type); Memory(w, F64Store, 3, ValueNumber);
                    PushStack(type, top); PopStack(type, array); break;
                }
                case IrOperation.CompoundIndex: {
                    // Like a compound field assignment, on the element CheckArrayWrite proved in range.
                    var operation = ((Assignment)cur.body.Pool[operand]).Operation;
                    int array = depth - 3, index = depth - 2, left = TopType(3) & 15, right = TopType();
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayStorage); Memory(w, I32Load, 2, ArrayStorageReferences); I(w, 1); Op(0x47);
                    Get(w, StackI(array)); Memory(w, I32Load, 2, ArrayExposed); Op(0x72, If, 0x40);
                    Get(w, StackI(array)); Op(Call); U(w, runtime["rt_nativeWritable"]); Op(Drop);
                    RefreshValues(stack);
                    Op(End);
                    Get(w, StackCount(array) + 1); Get(w, StackI(index)); I(w, 4); Op(0x74, 0x6a); Set(w, resultI);
                    void LoadElement() { Get(w, resultI); Memory(w, F64Load, 3, ValueNumber); if (!Wide(left)) ExactInteger(w); }
                    void StoreElement(Action number) { Get(w, resultI); number(); Memory(w, F64Store, 3, ValueNumber); }
                    if (left == IntType && right == IntType) {
                        IntCheck(operation, () => PushStack(IntType, top));
                        StoreElement(() => { IntOperation(operation, LoadElement, () => PushStack(IntType, top)); Op(0xb7); });
                    } else {
                        if (left == IntType && operation == IrOperation.Modulo) { AsDouble(right, top); F(w, 0); Op(0x61, If, 0x40); Fail(1); Op(End); }
                        LoadElement(); if (!Wide(left)) Op(0xb7); Set(w, tempF);
                        AsDouble(right, top); Set(w, tempG);
                        FloatOperation(operation, tempF, tempG); Set(w, tempF);
                        StoreElement(() => { Get(w, tempF); if (left == IntType) { Op(0xfc, 0x02); Op(0xb7); } else if (left == FloatType) RoundFloat(); });
                    }
                    break;
                }
                case IrOperation.IncrementIndex: case IrOperation.IncrementMember: {
                    // ++ on an element CheckArrayWrite proved in range, or on a numeric field; the result replaces the receiver.
                    int receiver, type;
                    if (op.Code == IrOperation.IncrementIndex) {
                        receiver = depth - 2; type = TopType(2) & 15;
                        Get(w, StackI(receiver)); Memory(w, I32Load, 2, ArrayStorage); Memory(w, I32Load, 2, ArrayStorageReferences); I(w, 1); Op(0x47);
                        Get(w, StackI(receiver)); Memory(w, I32Load, 2, ArrayExposed); Op(0x72, If, 0x40);
                        Get(w, StackI(receiver)); Op(Call); U(w, runtime["rt_nativeWritable"]); Op(Drop);
                        RefreshValues(stack);
                        Op(End);
                        Get(w, StackCount(receiver) + 1); Get(w, StackI(top)); I(w, 4); Op(0x74, 0x6a); Set(w, resultI);
                    } else {
                        receiver = top; var field = cur.StructField(TopType(), cur.NameOf(operand >> 1))!.Value; type = field.Code;
                        NullCheck(top, 5); FieldAddress(StackI(top), field.Index); Get(w, tempI); Set(w, resultI);
                    }
                    bool prefix = (operand & 1) != 0;
                    void Load() { Get(w, resultI); Memory(w, F64Load, 3, ValueNumber); if (!Wide(type)) ExactInteger(w); }
                    if (!prefix) { Load(); PopStack(type, receiver); }
                    Get(w, resultI); Load();
                    if (type == IntType) { I(w, 1); Op(0x6a, 0xb7); } else { F(w, 1); Op(0xa0); if (type == FloatType) RoundFloat(); }
                    Memory(w, F64Store, 3, ValueNumber);
                    if (prefix) { Load(); PopStack(type, receiver); }
                    break;
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
                case IrOperation.EnterLoop: ClearScope(cur.plan.ScopeAt[pc + 1], -1); break;
                case IrOperation.NextIteration: {
                    int s = cur.plan.ScopeAt[pc]; var scope = cur.plan.Scopes[s];
                    ClearScope(s, scope.LoopName is string loopName ? scope.Slots[loopName] : -1); break;
                }
                case IrOperation.LeaveLoop: ClearScope(cur.plan.ScopeAt[pc], -1); break;
                case IrOperation.Call: {
                    int count = operand, at = depth - count - 1, callee = stack[at];
                    if (callee == BuiltinBase + LengthBuiltin && stack[top] == StringType) { Get(w, StackI(top)); Memory(w, I32Load, 2, TextLength); Set(w, StackI(at)); break; }
                    if (callee == BuiltinBase + LengthBuiltin) { Get(w, StackCount(top)); Set(w, StackI(at)); break; }
                    if (callee == BuiltinBase + AppendBuiltin) {
                        // 陣列.追加(array, element): the generic limit error, then the runtime appends (it may copy or grow
                        // the storage, so every array local is described again); the result is the array.
                        int array = at + 1, item = at + 2, element = stack[array] & 15, from = stack[item];
                        Get(w, StackCount(array)); I(w, ArrayLimit); Op(0x4f, If, 0x40); Fail(6); Op(End);
                        Get(w, StackI(array)); I(w, element);
                        if (from == element) { PushStack(from, item); AsPayload(from); } else { AsDouble(from, item); if (element == FloatType) RoundFloat(); }
                        Op(Call); U(w, runtime["rt_nativeArrayAppend"]); Op(Drop);
                        Redescribe(stack);
                        Get(w, StackI(array)); Set(w, StackI(at)); CopyCompanions(stack[array], StackCount(array), StackCount(at));
                        break;
                    }
                    if (callee >= BuiltinBase) {
                        int result = callee - BuiltinBase - ConvertFirst + IntType, argument = stack[top];
                        if (result == IntType) {
                            if (argument == IntType) Get(w, StackI(top)); else { Get(w, StackF(top)); Op(0xfc, 0x02); }
                            Set(w, StackI(at));
                        } else { AsDouble(argument, top); if (result == FloatType) RoundFloat(); Set(w, StackF(at)); }
                        break;
                    }
                    if (StructCallee(callee, out _)) { Construct(stack, at, null); break; }
                    string name = callNames[callee - CallableBase];
                    var (function, target) = natives[OwnPointers.TryGetValue(name, out var own) ? own.Callee : Body];
                    // An element of an array of any values passed to an 陣列 (or 字串) parameter must be an array (or a string).
                    for (int a = 0; a < count; a++)
                        if (stack[at + 1 + a] == AnyValueType) {
                            Get(w, StackCount(at + 1 + a)); I(w, target.Signature.Parameters[a] == StringType ? StringTag : ArrayTag); Op(0x47, If, 0x40);
                            ToHandler(CheckSite, BlockOf(pc + 1), stack.Take(at).ToArray(), pc); Op(End);
                        }
                    if (inlined.Contains(target) && !stack.Skip(at + 1).Contains(AnyValueType)) {
                        // The callee's entry checkpoint: where it would deoptimize, the handler builds both generic
                        // frames instead (so the loop around an inlined call holds no call that clobbers registers).
                        Get(w, depthParameter); I(w, 1); Op(0x6a); I(w, target.DepthLimit); Op(0x4f);
                        Get(w, budget); Op(0x45, 0x72, If, 0x40); ToHandler(InlineSite, BlockOf(pc + 1), stack.Take(at).ToArray(), pc); Op(End);
                        Inline(target, stack, at, count);
                        break;
                    }
                    // A call whose result this body returns at once is a tail call (return_call): the callee takes over this
                    // activation, so it keeps the depth. When it deoptimizes, its generic frame returns straight to this
                    // body's caller, which is what this body's frame would have done after the call.
                    bool tail = inlineReturn == null && pc + 1 < n && code[pc + 1].Code == IrOperation.Return && cur == this
                        && target.Signature.Result == Signature.Result;
                    Get(w, depthParameter); if (!tail) { I(w, 1); Op(0x6a); } Get(w, budget);
                    // The callee's closure: this activation's own for a recursive call, else loaded from its binding.
                    if (ClosureGuards.ContainsKey(name) && !OwnPointers.ContainsKey(name)) Get(w, selfParameter); else { Get(w, PointerOf(name)); Memory(w, I32Load, 2, ValueRef); }
                    foreach (string pointer in target.Pointers) Get(w, PointerOf(pointer));
                    for (int a = 0; a < count; a++) {
                        int from = stack[at + 1 + a], to = target.Signature.Parameters[a];
                        if (from == AnyValueType || IsRef(from)) Get(w, StackI(at + 1 + a));
                        else if (from == to) PushStack(from, at + 1 + a);
                        else { AsDouble(from, at + 1 + a); if (to == FloatType) RoundFloat(); }
                    }
                    SaveOuter();
                    if (tail) { Op(ReturnCall); U(w, function); break; }
                    Op(Call); U(w, function); Set(w, budget); PopStack(target.Signature.Result, at);
                    if (target.WritesOuter) LoadOuter();
                    if (target.ResizesArrays) Redescribe(stack.Take(at).ToArray());
                    else if (target.WritesArrays) RefreshValues(stack.Take(at).ToArray());
                    if (IsRef(target.Signature.Result)) Describe(StackI(at), StackCount(at));
                    Get(w, budget); I(w, int.MinValue); Op(0x46, If, 0x40);
                    // A raised error (5) unwinds every native activation with the signal kept.
                    Op(0x23); U(w, signal); I(w, 5); Op(0x46, If, 0x40); Zero(); I(w, int.MinValue); Op(Return); Op(End);
                    // The callee deoptimized: this activation becomes its caller's generic frame, resuming after the call.
                    ToHandler(CallSite, BlockOf(pc + 1), stack.Take(at).ToArray(), pc + 1);
                    Op(End);
                    break;
                }
                case IrOperation.Return: Exit(stack[top], top); break;
                default: throw new InvalidOperationException($"Unsupported native instruction {op.Code}.");
            }
        }
        }
        EmitOps(Start, Stop);

        // The result of the current body: returned, or (inlined) left in the caller's operand and continued after it.
        void Exit(int type, int k) {
            if (inlineReturn == null) { ExitWith(type, k); return; }
            inlineReturn(type, k);
        }
        // An inlined callee: the caller's arguments become its parameter slots, its body runs on its own locals, and
        // a return branches past it with the result in the caller's operand at.
        void Inline(WasmNativeFunction callee, int[] stack, int at, int count) {
            int i32 = first + i32Own + inlined.TakeWhile(f => f != callee).Sum(InlineI32);
            int f64 = f64First + f64Own + inlined.TakeWhile(f => f != callee).Sum(InlineF64);
            int calleeSlots = callee.plan.Locals, calleeCap = callee.capacity;
            Get(w, budget); I(w, WasmFuel.Checkpoints(callee.body)[0]); Op(0x6b); Set(w, budget);
            var outer = (cur, code, n, constructs, slotBase, flagBase, stackBase, slotCountBase, stackCountBase, slotFBase, stackFBase, inlineReturn);
            int[] arguments = stack.Skip(at + 1).Take(count).ToArray();
            int[] callerStack = Enumerable.Range(0, count).Select(a => outer.stackBase + at + 1 + a).ToArray();
            int[] callerStackF = Enumerable.Range(0, count).Select(a => outer.stackFBase + at + 1 + a).ToArray();
            int[] callerCounts = Enumerable.Range(0, count).Select(a => outer.stackCountBase + 2 * (at + 1 + a)).ToArray();
            int intoI = outer.stackBase + at, intoF = outer.stackFBase + at, intoCount = outer.stackCountBase + 2 * at;
            cur = callee; code = callee.body.Code; n = code.Length; constructs = callee.constructs!;
            slotBase = i32; flagBase = i32 + calleeSlots; stackBase = i32 + calleeSlots * 2;
            slotCountBase = i32 + calleeSlots * 2 + calleeCap; stackCountBase = slotCountBase + 2 * calleeSlots;
            slotFBase = f64; stackFBase = f64 + calleeSlots;
            for (int a = 0; a < count; a++) {
                int from = arguments[a], to = callee.Signature.Parameters[a];
                Local(from, callerStack[a], callerStackF[a]);
                if (from != to) { if (!Wide(from)) Op(0xb7); if (to == FloatType) RoundFloat(); }
                SetLocal(to, SlotI(a), SlotF(a));
                CopyCompanions(to, callerCounts[a], SlotCount(a));
            }
            for (int s = 0; s < calleeSlots; s++) { I(w, s < count ? 1 : 0); Set(w, Flag(s)); }
            Op(Block, 0x40); open.Add((-3, int.MaxValue - 1, false));
            inlineReturn = (type, k) => {
                Local(type, StackI(k), StackF(k)); SetLocal(type, intoI, intoF);
                CopyCompanions(type, StackCount(k), intoCount);
                Op(Br); U(w, Label(c => c.Start == -3));
            };
            EmitOps(0, n);
            if (open[^1].Start != -3) throw new InvalidOperationException("Inlined native control flow is not structured.");
            open.RemoveAt(open.Count - 1); Op(End);
            (cur, code, n, constructs, slotBase, flagBase, stackBase, slotCountBase, stackCountBase, slotFBase, stackFBase, inlineReturn) = outer;
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
            if (kind == ErrorSite) SaveOuter();
            if (kind == ErrorSite && region) { Execution(); I(w, message); Op(Call); U(w, runtime["rt_nativeFailure"]); Op(Drop); I(w, -3); Op(Return); continue; }
            if (kind == ErrorSite) { Raise(() => { Execution(); I(w, message); Op(Call); U(w, runtime["rt_nativeFailure"]); }); continue; }
            if (kind == IndexSite && region) { WriteOperands(operands); RegionExit(block, message); continue; }
            if (kind == IndexSite) { SaveBudget(); SaveOuter(); Materialize(block, message, operands); Raise(() => Get(w, frame)); continue; }
            if (region) { RegionExit(block, message); continue; } // A loop head: the generic body continues there and yields.
            // A checkpoint consumed nothing yet; after a call the callee already stored the budget.
            if (kind == CheckpointSite) { SaveBudget(); SaveOuter(); }
            if (kind is InlineSite or CheckSite) { SaveBudget(); SaveOuter(); EntryFrame(message, kind == InlineSite); message++; }
            Materialize(block, message, operands);
            // A checkpoint's frame resumes natively at the same site when the scheduler runs it again.
            Get(w, frame); I(w, SiteOf(message)); Memory(w, I32Store, 2, FrameNativeSite);
            if (kind is CallSite or InlineSite or CheckSite) { Op(0x23); U(w, signal); Get(w, frame); Memory(w, I32Store, 2, FrameParent); }
            Raise(() => Get(w, frame));
        }
        Zero(); if (!region) I(w, int.MinValue);
        Op(End);

        // The generic frame of a callee at its entry (in the signal, like a callee's deoptimization): its arguments in
        // its parameter slots, resuming natively at its entry (an inlined callee's checkpoint) or running its generic
        // body (an argument its native code cannot take).
        void EntryFrame(int pc, bool native) {
            var stack = states[pc]; int count = code[pc].Operand, at = stack.Length - count - 1;
            string name = callNames[stack[at] - CallableBase];
            bool own = !OwnPointers.ContainsKey(name);
            var callee = natives[own ? Body : OwnPointers[name].Callee].Native;
            Execution(); if (own) Get(w, selfParameter); else { Get(w, PointerOf(name)); Memory(w, I32Load, 2, ValueRef); } I(w, 0); I(w, 0);
            Op(Call); U(w, runtime["rt_nativeFrame"]); Set(w, frame);
            for (int a = 0; a < count; a++) {
                int from = stack[at + 1 + a], to = callee.Signature.Parameters[a];
                if (from == AnyValueType) { StoreAny(FrameValues + ValueSize * a, at + 1 + a); continue; }
                StoreValue(FrameValues + ValueSize * a, to, () => {
                    PushStack(from, at + 1 + a);
                    if (IsRef(to)) return;
                    if (from != to) { if (!Wide(from)) Op(0xb7); if (to == FloatType) RoundFloat(); }
                    else AsPayload(to);
                });
            }
            Get(w, frame); I(w, native ? 1 : 0); Memory(w, I32Store, 2, FrameNativeSite);
            Get(w, frame); Op(0x24); U(w, signal);
        }
        // ---- structs: field access through the Struct* in a local; a null one is the generic runtime's error ----
        void NullCheck(int k, int message) { Get(w, StackI(k)); Op(0x45, If, 0x40); Fail(message); Op(End); }
        void FieldAddress(int structLocal, int index) { Get(w, structLocal); I(w, StructFields + index * ValueSize); Op(0x6a); Set(w, tempI); }
        // Writes operand k (of native type value) into the field at tempI, annotated with code (0: any value).
        void StoreField(int code, int value, int k) {
            if (value == AnyValueType) {
                Get(w, tempI); Get(w, StackCount(k)); Memory(w, I32Store, 2, ValueTag);
                Get(w, tempI); Get(w, StackI(k)); Memory(w, I32Store, 2, ValueRef);
                Get(w, tempI); Get(w, StackF(k)); Memory(w, F64Store, 3, ValueNumber);
                return;
            }
            int stored = Numeric(code) ? code : value;
            Get(w, tempI);
            if (IsStructType(value)) { I(w, StructTag); I(w, NullTag); Get(w, StackI(k)); Op(0x1b); }
            else I(w, IsRef(value) ? ArrayTag : stored);
            Memory(w, I32Store, 2, ValueTag);
            Get(w, tempI); if (IsPointer(value)) Get(w, StackI(k)); else I(w, 0); Memory(w, I32Store, 2, ValueRef);
            Get(w, tempI);
            if (IsPointer(value)) F(w, 0);
            else if (stored != value) { AsDouble(value, k); if (stored == FloatType) RoundFloat(); }
            else { PushStack(value, k); AsPayload(value); }
            Memory(w, F64Store, 3, ValueNumber);
        }
        // A struct of a known type built natively from the operands above position at (by name, or positionally); the
        // type comes from its binding, and fields left out stay null.
        void Construct(int[] stack, int at, string[]? names) {
            StructCallee(stack[at], out int code);
            int[] arguments = stack.Skip(at + 1).ToArray(), fields = ConstructionFields(code, arguments, names)!;
            var shape = Shape(code)!;
            Get(w, PointerOf(callNames[stack[at] - CallableBase])); Memory(w, I32Load, 2, ValueRef);
            Op(Call); U(w, runtime["rt_nativeStruct"]); Set(w, resultI);
            for (int i = 0; i < arguments.Length; i++) { FieldAddress(resultI, fields[i]); StoreField(shape.Fields[fields[i]].Type?.Code ?? 0, arguments[i], at + 1 + i); }
            Get(w, resultI); Set(w, StackI(at));
        }
        void ClearScope(int scope, int keep) { foreach (int slot in cur.plan.Scopes[scope].SlotList) if (slot != keep) { I(w, 0); Set(w, Flag(slot)); } }
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
