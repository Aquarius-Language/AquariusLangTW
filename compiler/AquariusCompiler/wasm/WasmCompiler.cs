using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using static AquariusLang.Wasm.RuntimeLayout;
using static AquariusLang.Wasm.WasmEncoding;

namespace AquariusLang.Wasm;

/// <summary>Low-level IR is a compiler detail. Deployed modules contain native Wasm control flow and constants only.</summary>
public sealed class WasmCompiler
{
    private readonly bool optimize;
    private readonly LibrarySet? libraries;
    public WasmCompiler() : this(true) { }
    /// <param name="optimize">Disable guarded fast paths for differential diagnostics.</param>
    /// <param name="libraries">The libraries imports may name; the installation's default libraries when null.</param>
    public WasmCompiler(bool optimize, LibrarySet? libraries = null) { this.optimize = optimize; this.libraries = libraries; }
    public WasmCompiler(LibrarySet libraries) : this(true, libraries) { }
    /// <summary>Receives "function N: tier: reason" when a body falls back to a slower tier. Defaults to standard
    /// error when AQUARIUS_WASM_TIER_DIAGNOSTICS=1. A receiver set here applies to the setter's own execution context
    /// (its thread and the tasks it starts), so concurrent compilations do not report to each other's receivers.</summary>
    public static Action<string>? TierDiagnostics { get => tierDiagnostics.Value ?? environmentDiagnostics; set => tierDiagnostics.Value = value; }
    private static readonly System.Threading.AsyncLocal<Action<string>?> tierDiagnostics = new();
    private static readonly Action<string>? environmentDiagnostics =
        System.Environment.GetEnvironmentVariable("AQUARIUS_WASM_TIER_DIAGNOSTICS") == "1" ? Console.Error.WriteLine : null;
    private static void Diagnose(int function, string tier, string reason) => TierDiagnostics?.Invoke($"function {function}: {tier}: {reason}");
    /// <summary>Array kernels that call back into Aquarius (runtime/guest/arrays.h): their functions in the guest prelude
    /// (runtime/guest/prelude.aqua), numbered in source order, which the guest runtime finds by Function.flags bits 8..15.</summary>
    private static readonly HashSet<string> PreludeKernels = new(StringComparer.Ordinal) { "Sort", "排序", "Map", "映射", "Filter", "篩選", "Reduce", "歸約" };
    private static readonly Lazy<string> PreludeSource = new(() => {
        using var stream = typeof(WasmCompiler).Assembly.GetManifestResourceStream("AquariusLang.Wasm.prelude.aqua")
            ?? throw new InvalidOperationException("The guest prelude (runtime/guest/prelude.aqua) is not embedded.");
        return new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n");
    });
    public WasmProgram Compile(string source) => Compile(new LoweringCompiler().Compile(source));
    public WasmProgram Compile(INode tree) => Compile(new LoweringCompiler().Compile(tree));
    public WasmProgram Compile(LoweredProgram lowered) => Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = lowered }, "main.aqua");

    /// <param name="declaredLibraries">Libraries the project declares for imports by computed names.</param>
    public WasmProgram Compile(IReadOnlyDictionary<string, LoweredProgram> modules, string entry,
        IReadOnlyDictionary<string, byte[]>? assets = null, string[]? supportedPlatforms = null, IEnumerable<string>? declaredLibraries = null)
    {
        var linked = ApplicationLinker.Link(modules, libraries ?? LibrarySet.Default, declaredLibraries, supportedPlatforms);
        modules = linked.Modules;
        var bodies = new List<LoweredProgram>();
        var ids = new Dictionary<LoweredProgram, int>();
        int Add(LoweredProgram body)
        {
            if (ids.TryGetValue(body, out int found)) return found;
            int id = bodies.Count; ids.Add(body, id); bodies.Add(body);
            foreach (var value in body.Constants)
            {
                if (value is FunctionCode f) Add(f.BodyCode);
                if (value is LoweredProgram b) Add(b);
            }
            return id;
        }
        var exports = modules.ToDictionary(p => p.Key, p => Add(p.Value), StringComparer.OrdinalIgnoreCase);
        // The guest prelude's functions follow the program's bodies, only when a body names one of their kernels.
        var prelude = new Dictionary<int, int>();
        if (bodies.Any(b => b.Constants.Any(c => c is string name && PreludeKernels.Contains(name))))
        {
            var code = new LoweringCompiler().Compile(PreludeSource.Value);
            Add(code);
            foreach (var function in code.Constants.OfType<FunctionCode>()) prelude.Add(ids[function.BodyCode], prelude.Count + 1);
        }
        WasmConstant Constant(object value) => value switch
        {
            string s => new("name", Text: s),
            IntegerObj n => new("int", Number: n.Value),
            FloatObj n => new("float", Number: n.Value),
            DoubleObj n => new("double", Number: n.Value),
            StringObj s => new("string", Text: s.Value),
            BooleanObj b => new("bool", Boolean: b.Value),
            NullObj => new("null"),
            BreakObj => new("break"),
            Assignment a => new("assignment", Text: a.Name, Operation: a.Operation.ToString()),
            FunctionCode f => new("function", Function: ids[f.BodyCode], Parameters: f.Parameters.Select(p => p.Value).ToArray()),
            LoweredProgram b => new("program", Function: ids[b]),
            _ => throw new ArgumentException($"Unsupported constant {value.GetType().Name}")
        };
        var parameters = new Dictionary<LoweredProgram, string[]>();
        foreach (var body in bodies) foreach (var item in body.Constants)
            if (item is FunctionCode function) parameters[function.BodyCode] = function.Parameters.Select(p => p.Value).ToArray();
        // WasmScopePlan resolves every identifier: closure-free lexical scopes become frame slots (promotion), and the
        // remaining names get cached binding candidates. Unoptimized compilation keeps every scope dynamic.
        var runtime = new WasmRuntimeImage();
        var capacities = bodies.Select(WasmStackAnalysis.Capacity).ToArray();
        string[]? Arguments(LoweredProgram b) => parameters.TryGetValue(b, out var p) ? p : null;
        var plans = bodies.Select((b, f) => {
            if (!optimize) return WasmScopePlan.Analyze(b, Arguments(b), false, out _)!;
            var plan = WasmScopePlan.Analyze(b, Arguments(b), true, out string? rejection);
            if (plan == null) { Diagnose(f, "frame-slot promotion", rejection!); plan = WasmScopePlan.Analyze(b, Arguments(b), false, out _)!; }
            return plan;
        }).ToArray();
        WasmFunction[] Functions() => bodies.Select((b, f) => new WasmFunction(b.WrapReturn, b.Constants.Select(Constant).ToArray(),
            // Promoted calls share one binding cache per closure, so free-name lookups are cached across calls.
            parameters.GetValueOrDefault(b, System.Array.Empty<string>()), capacities[f],
            b.Code.Any(op => op.Code == IrOperation.EnterLoop) || plans[f].PromotedFunction,
            Locals: plans[f].Locals, PromotedScope: plans[f].PromotedFunction, Prelude: prelude.GetValueOrDefault(f))).ToArray();
        // Loops of untyped bodies whose code is native-eligible become native regions, entered from their loop heads.
        // Their functions follow the bodies, so their indices are known before the bodies are emitted.
        var codes = new FunctionCode?[bodies.Count];
        foreach (var body in bodies) foreach (var item in body.Constants) if (item is FunctionCode code) codes[ids[code.BodyCode]] = code;
        // A typed function the native tier rejects (for example one that calls a library function) keeps its loops as
        // region candidates.
        var nativeCandidates = optimize ? WasmNativeFunction.Select(bodies, codes, plans).Select(n => n.Body).ToHashSet() : new HashSet<int>();
        var regions = optimize ? WasmNativeFunction.SelectRegions(bodies, plans, nativeCandidates, (f, reason) => Diagnose(f, "native region", "rejected: " + reason))
            : new List<WasmNativeFunction>();
        int regionFunctions = runtime.FunctionCount + bodies.Count;
        var regionPlans = plans.ToArray();
        var regionsOf = regions.Select((region, k) => (region, k)).GroupBy(p => p.region.Body)
            .ToDictionary(g => g.Key, g => g.Select(p => (p.region.HeadSites, regionFunctions + p.k)).ToList());
        foreach (var region in regions) Diagnose(region.Body, "native region", $"accepted: loop at {region.Start}");
        // Loops the desktop runtime JIT may compile when they get hot (WasmJit): typed variants of loops of untyped code.
        var stencils = optimize ? WasmNativeFunction.SelectStencils(bodies, plans, nativeCandidates, regions) : new List<WasmNativeFunction.Stencil>();
        foreach (var group in stencils.GroupBy(s => s.Loop))
            Diagnose(group.First().Region.Body, "runtime JIT", $"loop at {group.First().Region.Start}: {group.Count()} variant(s)");
        // The native tier's globals (signal, execution), then two per JIT loop (its installed table index, its count).
        int nativeGlobals = runtime.Sections.TryGetValue(6, out var runtimeGlobals) ? WasmRuntimeImage.Count(runtimeGlobals) : 0;
        int jitLoops = stencils.Count == 0 ? 0 : stencils.Max(s => s.Loop) + 1;
        var jitOf = stencils.GroupBy(s => s.Loop).Select(g => g.First())
            .GroupBy(s => s.Region.Body).ToDictionary(g => g.Key, g => g.Select(s =>
                new WasmBodyEmitter.JitLoop(s.Loop, s.Region.HeadSites, nativeGlobals + 2 + 2 * s.Loop, runtime.TypeCount + 1)).ToList());
        // Constant pool addresses depend only on pools and names, never on the plan, so every body is emitted exactly
        // once, at its final address. A promoted plan the emitter rejects is replaced by the dynamic plan.
        var layout = new WasmRuntimeImage.Data(); layout.Build(Functions());
        var emitted = new byte[bodies.Count][];
        for (int f = 0; f < bodies.Count; f++)
        {
            byte[] Emit(WasmScopePlan plan)
            {
                using var bytes = new MemoryStream(); using var code = new BinaryWriter(bytes, Encoding.UTF8, true);
                WasmBodyEmitter.Emit(code, bodies[f], runtime.Functions, layout.Pools[f], plan, capacities[f], optimize,
                    plan == regionPlans[f] && regionsOf.TryGetValue(f, out var own) ? own : null,
                    plan == regionPlans[f] && jitOf.TryGetValue(f, out var jit) ? jit : null); code.Flush(); return bytes.ToArray();
            }
            try { emitted[f] = Emit(plans[f]); }
            catch (InvalidOperationException e) when (plans[f].PromotedFunction || plans[f].Locals > 0) {
                Diagnose(f, "register emitter", e.Message);
                plans[f] = WasmScopePlan.Analyze(bodies[f], Arguments(bodies[f]), false, out _)!; emitted[f] = Emit(plans[f]);
            }
        }
        var functions = Functions();
        // Fully typed functions get the native tier; the speculative scalar tiers handle the remaining ones.
        var natives = optimize ? WasmNativeFunction.Select(bodies, codes, plans, (f, reason) => Diagnose(f, "native", "rejected: " + reason)) : new List<WasmNativeFunction>();
        var nativeBodies = natives.Select(n => n.Body).ToHashSet();
        foreach (int f in nativeBodies) Diagnose(f, "native", "accepted");
        var numeric = new List<(int Body, WasmNumericFunction Plan)>();
        if (optimize) for (int f = 0; f < bodies.Count; f++)
            if (!nativeBodies.Contains(f) && parameters.TryGetValue(bodies[f], out var args) && WasmNumericFunction.Analyze(bodies[f], args) is { } plan)
                numeric.Add((f, plan));
        for (int p = 0; p < numeric.Count; p++) {
            functions[numeric[p].Body] = functions[numeric[p].Body] with {
                NumericTable = bodies.Count + 1 + p, NumericRecursiveName = numeric[p].Plan.RecursiveName };
        }
        var integers = numeric.Select(item => WasmIntegerFunction.Analyze(item.Plan)).ToArray();
        var adapterIndices = new int[numeric.Count];
        var integerTypes = new int[numeric.Count];
        int extraFunctions = 0, extraTypes = 0;
        for (int p = 0; p < numeric.Count; p++) {
            adapterIndices[p] = runtime.FunctionCount + bodies.Count + regions.Count + extraFunctions;
            extraFunctions += integers[p] == null ? 2 : 3;
            if (integers[p] != null) integerTypes[p] = runtime.TypeCount + 2 + numeric.Count + extraTypes++;
        }
        int nativeFunctions = runtime.FunctionCount + bodies.Count + regions.Count + extraFunctions;
        int nativeTypes = runtime.TypeCount + 2 + numeric.Count + extraTypes;
        for (int k = 0; k < natives.Count; k++)
            functions[natives[k].Body] = functions[natives[k].Body] with { NumericTable = bodies.Count + 1 + numeric.Count + 2 * k, NumericRecursiveName = null, Native = true };
        var data = new WasmRuntimeImage.Data(); data.Build(functions);
        if (!data.Pools.SequenceEqual(layout.Pools)) throw new InvalidOperationException("Constant pool layout changed after tier selection.");
        // Names the native adapters look up, resolved to their texts before the heap start is fixed.
        var texts = natives.Concat(regions).Concat(stencils.Select(s => s.Region)).SelectMany(n => n.Pointers.Concat(n.Guards.Keys).Concat(n.ClosureGuards.Keys))
            .Distinct().ToDictionary(name => name, data.Text, StringComparer.Ordinal);
        // Per native function: entry adapter, implementation, resume adapter, resume implementation.
        var nativeByBody = natives.Select((native, k) => (native, k)).ToDictionary(p => p.native.Body, p => (Function: nativeFunctions + 4 * p.k + 1, Native: p.native));
        // The native tier's signal global (WasmNativeFunction): 0, 5 after an error, or a deoptimized frame.
        int signal = runtime.Sections.TryGetValue(6, out var globals) ? WasmRuntimeImage.Count(globals) : 0;
        int execution = signal + 1; // The execution the adapter entered, for fuel, deoptimization and errors.
        if (natives.Count + regions.Count + stencils.Count > 0) {
            runtime.Sections[6] = globals == null ? new byte[] { 2, 0x7f, 1, 0x41, 0, 0x0b, 0x7f, 1, 0x41, 0, 0x0b }
                : WasmRuntimeImage.Extend(globals, 2, w => w.Write(new byte[] { 0x7f, 1, 0x41, 0, 0x0b, 0x7f, 1, 0x41, 0, 0x0b }));
            runtime.Sections[6] = WasmRuntimeImage.Extend(runtime.Sections[6], 2 * jitLoops, w => {
                for (int k = 0; k < 2 * jitLoops; k++) w.Write(new byte[] { 0x7f, 1, 0x41, 0, 0x0b });
            });
        }
        if (signal != nativeGlobals) throw new InvalidOperationException("The native tier's globals moved.");
        var metadata = new WasmMetadata(WasmAbi.Version, entry, exports, bodies.Count, assets?.Keys.ToArray() ?? System.Array.Empty<string>(),
            ProgramAddress: WasmRuntimeImage.ProgramAddress, HeapStart: data.End, FunctionLayout: 40, SupportedPlatforms: linked.Platforms,
            Imports: linked.Imports, Libraries: linked.NativeLibraries);
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 });
        void Section(byte id, Action<BinaryWriter> write)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            write(writer); output.WriteByte(id); WasmEncoding.WriteUnsigned(output, (uint)bytes.Length); bytes.Position = 0; bytes.CopyTo(output);
        }
        runtime.Sections[1] = WasmRuntimeImage.Extend(runtime.Sections[1], 2 + numeric.Count + extraTypes + 2 * natives.Count, w => {
            w.Write(new byte[] { 0x60, 1, 0x7f, 1, 0x7f });
            w.Write(new byte[] { 0x60, 3, 0x7f, 0x7f, 0x7f, 1, 0x7f });
            foreach (var item in numeric) item.Plan.WriteType(w);
            foreach (var integer in integers) integer?.WriteType(w);
            foreach (var native in natives) { native.WriteType(w); native.WriteResumeType(w); }
        });
        // Memory may grow to 2 GiB: library data (Processing pixel arrays, images, tensors) lives in the guest's memory
        // now, and every u32 address plus size stays below 4 GiB.
        using (var memory = new MemoryStream()) { using var w = new BinaryWriter(memory); U(w, 1); U(w, 1); U(w, Math.Max(256, checked((data.End + 65535) / 65536))); U(w, 32768); runtime.Sections[5] = memory.ToArray(); }
        runtime.Sections[3] = WasmRuntimeImage.Extend(runtime.Sections[3], bodies.Count + regions.Count + extraFunctions + 4 * natives.Count, w => {
            foreach (var _ in bodies) U(w, runtime.TypeCount);
            foreach (var _ in regions) U(w, runtime.TypeCount + 1);
            for (int p = 0; p < numeric.Count; p++) {
                U(w, runtime.TypeCount + 1); U(w, runtime.TypeCount + 2 + p);
                if (integers[p] != null) U(w, integerTypes[p]);
            }
            for (int k = 0; k < natives.Count; k++) { U(w, runtime.TypeCount + 1); U(w, nativeTypes + 2 * k); U(w, runtime.TypeCount + 1); U(w, nativeTypes + 2 * k + 1); }
        });
        // Runtime function pointers dispatch compiled Aquarius functions directly.
        using (var table = new MemoryStream()) { using var w = new BinaryWriter(table); U(w, 1); w.Write((byte)0x70); U(w, 0); U(w, bodies.Count + 1 + numeric.Count + 2 * natives.Count); runtime.Sections[4] = table.ToArray(); }
        runtime.Sections[7] = WasmRuntimeImage.Extend(runtime.Sections[7], bodies.Count + (stencils.Count > 0 ? 2 : 0), w => {
            for (int i = 0; i < bodies.Count; i++) { Name(w, $"aqua_f{i}"); w.Write((byte)0); U(w, runtime.FunctionCount + i); }
            // The runtime JIT's modules import the native tier's globals (WasmJit).
            if (stencils.Count > 0) { Name(w, WasmJit.SignalExport); w.Write((byte)3); U(w, signal); Name(w, WasmJit.ExecutionExport); w.Write((byte)3); U(w, execution); }
        });
        using (var elements = new MemoryStream()) { using var w = new BinaryWriter(elements); U(w, 1); U(w, 0); w.Write((byte)0x41); S(w, 1); w.Write((byte)0x0b); U(w, bodies.Count + numeric.Count + 2 * natives.Count); for (int i = 0; i < bodies.Count; i++) U(w, runtime.FunctionCount + i); foreach (int adapter in adapterIndices) U(w, adapter); for (int k = 0; k < natives.Count; k++) { U(w, nativeFunctions + 4 * k); U(w, nativeFunctions + 4 * k + 2); } runtime.Sections[9] = elements.ToArray(); }
        runtime.Sections[10] = WasmRuntimeImage.Extend(runtime.Sections[10], bodies.Count + regions.Count + extraFunctions + 4 * natives.Count, w =>
        {
            for (int f = 0; f < bodies.Count; f++)
            {
                U(w, emitted[f].Length); w.Write(emitted[f]);
            }
            foreach (var region in regions) {
                using var bytes = new MemoryStream();
                using (var code = new BinaryWriter(bytes, Encoding.UTF8, true))
                    region.EmitImplementation(code, new Dictionary<int, (int, WasmNativeFunction)>(), runtime.Functions, signal, execution, text: name => texts[name]);
                U(w, (int)bytes.Length); w.Write(bytes.ToArray());
            }
            for (int p = 0; p < numeric.Count; p++) {
                int implementation = adapterIndices[p] + 1;
                int? integerImplementation = integers[p] == null ? null : implementation + 1;
                using var adapter = new MemoryStream(); using (var code = new BinaryWriter(adapter, Encoding.UTF8, true)) numeric[p].Plan.EmitAdapter(code, implementation, integerImplementation, integers[p]?.ResultType ?? 1);
                U(w, (int)adapter.Length); w.Write(adapter.ToArray());
                using var typed = new MemoryStream(); using (var code = new BinaryWriter(typed, Encoding.UTF8, true)) numeric[p].Plan.EmitImplementation(code, implementation);
                U(w, (int)typed.Length); w.Write(typed.ToArray());
                if (integers[p] != null) {
                    // A promoted generic body is a deoptimization target for the integer specialization.
                    WasmIntegerFunction.Deopt? deopt = null;
                    if (plans[numeric[p].Body] is { PromotedFunction: true } generic) {
                        var slots = numeric[p].Plan.Variables.Select(name => generic.Scopes[0].Slots.TryGetValue(name, out int slot) ? slot : -1).ToArray();
                        if (slots.All(slot => slot >= 0))
                            deopt = new(slots, generic.Locals, WasmBodyEmitter.BlockStarts(bodies[numeric[p].Body], generic),
                                WasmFuel.Checkpoints(bodies[numeric[p].Body])[0], runtime.Functions["rt_numericFrame"]);
                    }
                    using var integer = new MemoryStream(); using (var code = new BinaryWriter(integer, Encoding.UTF8, true)) integers[p]!.EmitImplementation(code, integerImplementation!.Value, deopt);
                    U(w, (int)integer.Length); w.Write(integer.ToArray());
                }
            }
            var nativeOf = natives.ToDictionary(n => n.Body);
            void Body(Action<BinaryWriter> emit) {
                using var bytes = new MemoryStream(); using (var code = new BinaryWriter(bytes, Encoding.UTF8, true)) emit(code);
                U(w, (int)bytes.Length); w.Write(bytes.ToArray());
            }
            for (int k = 0; k < natives.Count; k++) {
                int implementation = nativeFunctions + 4 * k + 1;
                Body(code => natives[k].EmitAdapter(code, implementation, runtime.Functions, name => texts[name], nativeOf, signal, execution));
                Body(code => natives[k].EmitImplementation(code, nativeByBody, runtime.Functions, signal, execution));
                Body(code => natives[k].EmitAdapter(code, implementation, runtime.Functions, name => texts[name], nativeOf, signal, execution, implementation + 2));
                Body(code => natives[k].EmitImplementation(code, nativeByBody, runtime.Functions, signal, execution, resume: true));
            }
        });
        runtime.Sections[11] = WasmRuntimeImage.Extend(runtime.Sections[11], 1, w =>
            { U(w, 0); w.Write((byte)0x41); S(w, WasmRuntimeImage.ProgramAddress); w.Write((byte)0x0b); var payload = data.Bytes; U(w, payload.Length); w.Write(payload); });
        foreach (var section in runtime.Sections.OrderBy(p => p.Key)) Section(section.Key, w => w.Write(section.Value));
        if (stencils.Count > 0) Section(0, w => WasmJit.WriteSection(w, stencils, runtime, name => texts[name]));
        return WasmProgram.Create(output.ToArray(), metadata,
            assets?.ToDictionary(p => p.Key, p => (ReadOnlyMemory<byte>)p.Value) ?? new Dictionary<string, ReadOnlyMemory<byte>>());
    }
}
