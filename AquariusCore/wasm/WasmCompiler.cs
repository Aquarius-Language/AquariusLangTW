using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.Compiler;
using static AquariusLang.Wasm.RuntimeLayout;
using static AquariusLang.Wasm.WasmEncoding;

namespace AquariusLang.Wasm;

/// <summary>Low-level IR is a compiler detail. Deployed modules contain native Wasm control flow and constants only.</summary>
public sealed class WasmCompiler
{
    private readonly bool optimize;
    /// <param name="optimize">Disable guarded fast paths for differential diagnostics.</param>
    public WasmCompiler() : this(true) { }
    public WasmCompiler(bool optimize) { this.optimize = optimize; }
    /// <summary>Receives "function N: tier: reason" when a body falls back to a slower tier. Defaults to standard
    /// error when AQUARIUS_WASM_TIER_DIAGNOSTICS=1.</summary>
    public static Action<string>? TierDiagnostics { get; set; } =
        System.Environment.GetEnvironmentVariable("AQUARIUS_WASM_TIER_DIAGNOSTICS") == "1" ? Console.Error.WriteLine : null;
    private static void Diagnose(int function, string tier, string reason) => TierDiagnostics?.Invoke($"function {function}: {tier}: {reason}");
    public WasmProgram Compile(string source) => Compile(new LoweringCompiler().Compile(source));
    public WasmProgram Compile(INode tree) => Compile(new LoweringCompiler().Compile(tree));
    public WasmProgram Compile(LoweredProgram lowered) => Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = lowered }, "main.aqua");

    public WasmProgram Compile(IReadOnlyDictionary<string, LoweredProgram> modules, string entry,
        IReadOnlyDictionary<string, byte[]>? assets = null, string[]? supportedPlatforms = null)
    {
        modules = AquariusLang.Graphics.PortableGraphicsLibraries.Link(modules);
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
            Locals: plans[f].Locals, PromotedScope: plans[f].PromotedFunction)).ToArray();
        // Constant pool addresses depend only on pools and names, never on the plan, so every body is emitted exactly
        // once, at its final address. A promoted plan the emitter rejects is replaced by the dynamic plan.
        var layout = new WasmRuntimeImage.Data(); layout.Build(Functions());
        var emitted = new byte[bodies.Count][];
        for (int f = 0; f < bodies.Count; f++)
        {
            byte[] Emit(WasmScopePlan plan)
            {
                using var bytes = new MemoryStream(); using var code = new BinaryWriter(bytes, Encoding.UTF8, true);
                WasmBodyEmitter.Emit(code, bodies[f], runtime.Functions, layout.Pools[f], plan, capacities[f], optimize); code.Flush(); return bytes.ToArray();
            }
            try { emitted[f] = Emit(plans[f]); }
            catch (InvalidOperationException e) when (plans[f].PromotedFunction || plans[f].Locals > 0) {
                Diagnose(f, "register emitter", e.Message);
                plans[f] = WasmScopePlan.Analyze(bodies[f], Arguments(bodies[f]), false, out _)!; emitted[f] = Emit(plans[f]);
            }
        }
        var functions = Functions();
        var numeric = new List<(int Body, WasmNumericFunction Plan)>();
        if (optimize) for (int f = 0; f < bodies.Count; f++)
            if (parameters.TryGetValue(bodies[f], out var args) && WasmNumericFunction.Analyze(bodies[f], args) is { } plan)
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
            adapterIndices[p] = runtime.FunctionCount + bodies.Count + extraFunctions;
            extraFunctions += integers[p] == null ? 2 : 3;
            if (integers[p] != null) integerTypes[p] = runtime.TypeCount + 2 + numeric.Count + extraTypes++;
        }
        var data = new WasmRuntimeImage.Data(); data.Build(functions);
        if (!data.Pools.SequenceEqual(layout.Pools)) throw new InvalidOperationException("Constant pool layout changed after tier selection.");
        var metadata = new WasmMetadata(WasmAbi.Version, entry, exports, bodies.Count, assets?.Keys.ToArray() ?? System.Array.Empty<string>(),
            ProgramAddress: WasmRuntimeImage.ProgramAddress, HeapStart: data.End, FunctionLayout: 40, SupportedPlatforms: supportedPlatforms);
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 });
        void Section(byte id, Action<BinaryWriter> write)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            write(writer); output.WriteByte(id); WasmEncoding.WriteUnsigned(output, (uint)bytes.Length); bytes.Position = 0; bytes.CopyTo(output);
        }
        runtime.Sections[1] = WasmRuntimeImage.Extend(runtime.Sections[1], 2 + numeric.Count + extraTypes, w => {
            w.Write(new byte[] { 0x60, 1, 0x7f, 1, 0x7f });
            w.Write(new byte[] { 0x60, 3, 0x7f, 0x7f, 0x7f, 1, 0x7f });
            foreach (var item in numeric) item.Plan.WriteType(w);
            foreach (var integer in integers) integer?.WriteType(w);
        });
        using (var memory = new MemoryStream()) { using var w = new BinaryWriter(memory); U(w, 1); U(w, 1); U(w, Math.Max(256, checked((data.End + 65535) / 65536))); U(w, 8192); runtime.Sections[5] = memory.ToArray(); }
        runtime.Sections[3] = WasmRuntimeImage.Extend(runtime.Sections[3], bodies.Count + extraFunctions, w => {
            foreach (var _ in bodies) U(w, runtime.TypeCount);
            for (int p = 0; p < numeric.Count; p++) {
                U(w, runtime.TypeCount + 1); U(w, runtime.TypeCount + 2 + p);
                if (integers[p] != null) U(w, integerTypes[p]);
            }
        });
        // Runtime function pointers dispatch compiled Aquarius functions directly.
        using (var table = new MemoryStream()) { using var w = new BinaryWriter(table); U(w, 1); w.Write((byte)0x70); U(w, 0); U(w, bodies.Count + 1 + numeric.Count); runtime.Sections[4] = table.ToArray(); }
        runtime.Sections[7] = WasmRuntimeImage.Extend(runtime.Sections[7], bodies.Count, w =>
            { for (int i = 0; i < bodies.Count; i++) { Name(w, $"aqua_f{i}"); w.Write((byte)0); U(w, runtime.FunctionCount + i); } });
        using (var elements = new MemoryStream()) { using var w = new BinaryWriter(elements); U(w, 1); U(w, 0); w.Write((byte)0x41); S(w, 1); w.Write((byte)0x0b); U(w, bodies.Count + numeric.Count); for (int i = 0; i < bodies.Count; i++) U(w, runtime.FunctionCount + i); foreach (int adapter in adapterIndices) U(w, adapter); runtime.Sections[9] = elements.ToArray(); }
        runtime.Sections[10] = WasmRuntimeImage.Extend(runtime.Sections[10], bodies.Count + extraFunctions, w =>
        {
            for (int f = 0; f < bodies.Count; f++)
            {
                U(w, emitted[f].Length); w.Write(emitted[f]);
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
        });
        runtime.Sections[11] = WasmRuntimeImage.Extend(runtime.Sections[11], 1, w =>
            { U(w, 0); w.Write((byte)0x41); S(w, WasmRuntimeImage.ProgramAddress); w.Write((byte)0x0b); var payload = data.Bytes; U(w, payload.Length); w.Write(payload); });
        foreach (var section in runtime.Sections.OrderBy(p => p.Key)) Section(section.Key, w => w.Write(section.Value));
        return WasmProgram.Create(output.ToArray(), metadata,
            assets?.ToDictionary(p => p.Key, p => (ReadOnlyMemory<byte>)p.Value) ?? new Dictionary<string, ReadOnlyMemory<byte>>());
    }
}
