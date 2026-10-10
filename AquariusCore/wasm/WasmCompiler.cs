using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.Compiler;

namespace AquariusLang.Wasm;

/// <summary>Low-level IR is a compiler detail. Deployed modules contain native Wasm control flow and constants only.</summary>
public sealed class WasmCompiler
{
    public WasmProgram Compile(string source) => Compile(new LoweringCompiler().Compile(source));
    public WasmProgram Compile(INode tree) => Compile(new LoweringCompiler().Compile(tree));
    public WasmProgram Compile(LoweredProgram lowered) => Compile(new Dictionary<string, LoweredProgram> { ["main.aqua"] = lowered }, "main.aqua");

    public WasmProgram Compile(IReadOnlyDictionary<string, LoweredProgram> modules, string entry,
        IReadOnlyDictionary<string, byte[]>? assets = null)
    {
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
            FunctionCode f => new("function", Function: ids[f.BodyCode], Parameters: f.Parameters.Select(p => p.Value).ToArray(), Display: f.BodyDisplay ?? f.Body?.String() ?? ""),
            LoweredProgram b => new("program", Function: ids[b]),
            _ => throw new ArgumentException($"Unsupported constant {value.GetType().Name}")
        };
        var parameters = new Dictionary<LoweredProgram, string[]>();
        foreach (var body in bodies) foreach (var item in body.Constants)
            if (item is FunctionCode function) parameters[function.BodyCode] = function.Parameters.Select(p => p.Value).ToArray();
        var functions = bodies.Select(b => new WasmFunction(b.WrapReturn, b.Constants.Select(Constant).ToArray(),
            parameters.GetValueOrDefault(b, System.Array.Empty<string>()), WasmStackAnalysis.Capacity(b), b.Code.Any(op => op.Code == IrOperation.EnterLoop))).ToArray();
        var runtime = new WasmRuntimeImage();
        var data = new WasmRuntimeImage.Data(); data.Build(functions);
        var metadata = new WasmMetadata(WasmAbi.Version, entry, exports, functions,
            assets?.ToDictionary(p => p.Key, p => Convert.ToBase64String(p.Value)) ?? new(),
            ProgramAddress: WasmRuntimeImage.ProgramAddress, HeapStart: data.End);
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 });
        void Section(byte id, Action<BinaryWriter> write)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            write(writer); output.WriteByte(id); WriteUnsigned(output, (uint)bytes.Length); bytes.Position = 0; bytes.CopyTo(output);
        }
        runtime.Sections[1] = WasmRuntimeImage.Extend(runtime.Sections[1], 1, w => w.Write(new byte[] { 0x60, 1, 0x7f, 1, 0x7f }));
        using (var memory = new MemoryStream()) { using var w = new BinaryWriter(memory); U(w, 1); U(w, 1); U(w, Math.Max(256, checked((data.End + 65535) / 65536))); U(w, 8192); runtime.Sections[5] = memory.ToArray(); }
        runtime.Sections[3] = WasmRuntimeImage.Extend(runtime.Sections[3], bodies.Count, w => { foreach (var _ in bodies) U(w, runtime.TypeCount); });
        // Runtime function pointers dispatch compiled Aquarius functions directly.
        using (var table = new MemoryStream()) { using var w = new BinaryWriter(table); U(w, 1); w.Write((byte)0x70); U(w, 0); U(w, bodies.Count + 1); runtime.Sections[4] = table.ToArray(); }
        runtime.Sections[7] = WasmRuntimeImage.Extend(runtime.Sections[7], bodies.Count, w =>
            { for (int i = 0; i < bodies.Count; i++) { Text(w, $"aqua_f{i}"); w.Write((byte)0); U(w, runtime.FunctionCount + i); } });
        using (var elements = new MemoryStream()) { using var w = new BinaryWriter(elements); U(w, 1); U(w, 0); w.Write((byte)0x41); Signed(w, 1); w.Write((byte)0x0b); U(w, bodies.Count); for (int i = 0; i < bodies.Count; i++) U(w, runtime.FunctionCount + i); runtime.Sections[9] = elements.ToArray(); }
        runtime.Sections[10] = WasmRuntimeImage.Extend(runtime.Sections[10], bodies.Count, w =>
        {
            for (int f = 0; f < bodies.Count; f++)
            {
                using var bytes = new MemoryStream(); using var code = new BinaryWriter(bytes, Encoding.UTF8, true);
                EmitBody(code, bodies[f], runtime.Functions, data.Pools[f]); U(w, (int)bytes.Length); w.Write(bytes.ToArray());
            }
        });
        runtime.Sections[11] = WasmRuntimeImage.Extend(runtime.Sections[11], 1, w =>
            { U(w, 0); w.Write((byte)0x41); Signed(w, WasmRuntimeImage.ProgramAddress); w.Write((byte)0x0b); var payload = data.Bytes; U(w, payload.Length); w.Write(payload); });
        foreach (var section in runtime.Sections.OrderBy(p => p.Key)) Section(section.Key, w => w.Write(section.Value));
        Section(0, w => { Text(w, WasmAbi.MetadataSection); w.Write(WasmProgram.SerializeMetadata(metadata)); });
        return new WasmProgram(output.ToArray(), metadata);
    }

    private static void EmitBody(BinaryWriter w, LoweredProgram body, IReadOnlyDictionary<string, int> helpers, int pool)
    {
        var leaders = new SortedSet<int> { 0, body.Code.Length };
        for (int i = 0; i < body.Code.Length; i++)
        {
            var op = body.Code[i];
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.JumpIfBreak or IrOperation.LoopCondition or IrOperation.Break) leaders.Add(op.Operand);
            if (op.Code is IrOperation.Jump or IrOperation.JumpIfFalse or IrOperation.JumpIfBreak or IrOperation.LoopCondition or IrOperation.Break or IrOperation.Call or IrOperation.ResolveMemberFunction or IrOperation.Return) leaders.Add(i + 1);
        }
        var starts = leaders.ToArray(); int count = starts.Length;
        int Block(int position) => System.Array.BinarySearch(starts, position);
        void I(int n) { w.Write((byte)0x41); Signed(w, n); }
        void Call(string name, int? operand = null)
        {
            w.Write(new byte[] { 0x20, 0 }); I(operand ?? 0);
            w.Write((byte)0x10); U(w, helpers["rt_" + name]);
        }
        void Resume(int n) { I(n); w.Write((byte)0x0f); }
        void Get(int index) { w.Write((byte)0x20); U(w, index); }
        void Set(int index) { w.Write((byte)0x21); U(w, index); }
        void Memory(byte op, int align, int offset = 0) { w.Write(op); U(w, align); U(w, offset); }
        void F(double value) { w.Write((byte)0x44); w.Write(value); }
        void Checked() { Get(0); Memory(0x28, 2, 4); Memory(0x28, 2, 28); I(3); w.Write(new byte[] { 0x46, 0x04, 0x40 }); Resume(-1); w.Write((byte)0x0b); }
        void StackAddress(int offset) { Get(0); Get(0); Memory(0x28, 2, 28); I(4); w.Write(new byte[] { 0x74, 0x6a }); I(offset); w.Write((byte)0x6a); Set(2); }
        void StackChange(int change) { Get(0); Get(0); Memory(0x28, 2, 28); I(change); w.Write((byte)0x6a); Memory(0x36, 2, 28); }
        void Literal(int index) {
            StackAddress(56);
            for (int offset = 0; offset <= 8; offset += 8) { Get(2); I(checked(pool + index * 16)); Memory(0x29, 3, offset); Memory(0x37, 3, offset); }
            StackChange(1);
        }
        void Numeric(IrOperation op, string helper) {
            StackAddress(24); Get(2); I(16); w.Write((byte)0x6a); Set(3);
            Get(2); Memory(0x28, 2); Set(4); Get(3); Memory(0x28, 2); Set(5);
            foreach (int tag in new[] { 4, 5 }) { Get(tag); I(1); w.Write((byte)0x4f); Get(tag); I(3); w.Write(new byte[] { 0x4d, 0x71 }); }
            w.Write(new byte[] { 0x71, 0x04, 0x40 });
            Get(2); Memory(0x2b, 3, 8); Get(3); Memory(0x2b, 3, 8);
            bool comparison = op is IrOperation.Less or IrOperation.Greater or IrOperation.LessEqual or IrOperation.GreaterEqual or IrOperation.Equal or IrOperation.NotEqual;
            w.Write(op switch { IrOperation.Add => (byte)0xa0, IrOperation.Subtract => (byte)0xa1, IrOperation.Multiply => (byte)0xa2, IrOperation.Divide => (byte)0xa3,
                IrOperation.Less => (byte)0x63, IrOperation.Greater => (byte)0x64, IrOperation.LessEqual => (byte)0x65, IrOperation.GreaterEqual => (byte)0x66,
                IrOperation.Equal => (byte)0x61, IrOperation.NotEqual => (byte)0x62, _ => throw new InvalidOperationException("Invalid numeric operation.") });
            if (comparison) w.Write((byte)0xb7); Set(6);
            if (comparison) { I(4); Set(4); }
            else {
                Get(4); Get(5); w.Write(new byte[] { 0x4b, 0x04, 0x7f }); Get(4); w.Write((byte)0x05); Get(5); w.Write((byte)0x0b); Set(4);
                Get(4); I(1); w.Write(new byte[] { 0x46, 0x04, 0x40 });
                Get(6); F(-2147483648d); w.Write((byte)0x66); Get(6); F(2147483648d); w.Write(new byte[] { 0x63, 0x71, 0x04, 0x7c });
                Get(6); w.Write(new byte[] { 0xaa, 0xb7, 0x05 }); F(-2147483648d); w.Write((byte)0x0b); Set(6);
                w.Write((byte)0x05); Get(4); I(2); w.Write(new byte[] { 0x46, 0x04, 0x40 }); Get(6); w.Write(new byte[] { 0xb6, 0xbb }); Set(6); w.Write(new byte[] { 0x0b, 0x0b });
            }
            Get(2); Get(4); Memory(0x36, 2); Get(2); I(0); Memory(0x36, 2, 4); Get(2); Get(6); Memory(0x39, 3, 8); StackChange(-1);
            w.Write((byte)0x05); Call(helper); w.Write(new byte[] { 0x1a, 0x0b }); Checked();
        }
        // Parameter 0 is the Wasm-owned frame; local 1 is its continuation.
        U(w, 2); U(w, 5); w.Write((byte)0x7f); U(w, 1); w.Write((byte)0x7c);
        Call("pc"); w.Write(new byte[] { 0x21, 1 });
        w.Write(new byte[] { 0x03, 0x40 }); // loop
        w.Write(new byte[] { 0x02, 0x40 }); // invalid continuation
        for (int i = count - 1; i >= 0; i--) w.Write(new byte[] { 0x02, 0x40 });
        w.Write(new byte[] { 0x20, 1, 0x0e }); U(w, count); for (int i = 0; i < count; i++) U(w, i); U(w, count);
        for (int b = 0; b < count; b++)
        {
            w.Write((byte)0x0b);
            Call("checkpoint"); w.Write(new byte[] { 0x04, 0x40 }); Resume(b); w.Write((byte)0x0b);
            if (starts[b] == body.Code.Length) { Resume(-1); continue; }
            bool terminated = false;
            for (int i = starts[b]; i < starts[b + 1]; i++)
            {
                var op = body.Code[i]; int n = op.Operand;
                switch (op.Code)
                {
                    case IrOperation.Jump: I(Block(n)); break;
                    case IrOperation.JumpIfFalse:
                    case IrOperation.LoopCondition:
                    case IrOperation.JumpIfBreak:
                        Call(op.Code == IrOperation.JumpIfFalse ? "truth" : op.Code == IrOperation.LoopCondition ? "loopCondition" : "isBreak");
                        Checked();
                        w.Write(new byte[] { 0x04, 0x7f }); I(Block(op.Code == IrOperation.JumpIfBreak ? n : i + 1)); w.Write((byte)0x05); I(Block(op.Code == IrOperation.JumpIfBreak ? i + 1 : n)); w.Write((byte)0x0b); break;
                    case IrOperation.Break: Call("break"); w.Write((byte)0x1a); I(Block(n)); break;
                    case IrOperation.Call: Call("call", n); w.Write((byte)0x1a); Resume(Block(i + 1)); terminated = true; break;
                    case IrOperation.ResolveMemberFunction: Call("resolveMemberFunction", n); w.Write((byte)0x1a); Resume(Block(i + 1)); terminated = true; break;
                    case IrOperation.Return: Call("returned"); w.Write((byte)0x1a); Resume(-1); terminated = true; break;
                    case IrOperation.Constant: Literal(n); continue;
                    case IrOperation.Pop: StackChange(-1); continue;
                    case IrOperation.Add: case IrOperation.Subtract: case IrOperation.Multiply: case IrOperation.Divide:
                    case IrOperation.Less: case IrOperation.Greater: case IrOperation.LessEqual: case IrOperation.GreaterEqual:
                    case IrOperation.Equal: case IrOperation.NotEqual:
                        Numeric(op.Code, char.ToLowerInvariant(op.Code.ToString()[0]) + op.Code.ToString()[1..]); continue;
                    default:
                        string name = op.Code == IrOperation.MemberFunction ? "member" : char.ToLowerInvariant(op.Code.ToString()[0]) + op.Code.ToString()[1..];
                        Call(name, n); w.Write((byte)0x1a); Checked(); continue;
                }
                if (!terminated) { w.Write(new byte[] { 0x21, 1, 0x0c }); U(w, count - b); terminated = true; }
            }
            if (!terminated) { I(b + 1); w.Write(new byte[] { 0x21, 1, 0x0c }); U(w, count - b); }
        }
        w.Write(new byte[] { 0x0b, 0x00, 0x0b, 0x00, 0x0b });
    }
    internal static void WriteUnsigned(Stream s, uint n) { do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
    private static void U(BinaryWriter w, int n) => WriteUnsigned(w.BaseStream, checked((uint)n));
    private static void Text(BinaryWriter w, string text) { var b = Encoding.UTF8.GetBytes(text); U(w, b.Length); w.Write(b); }
    private static void Signed(BinaryWriter w, int n) { bool more; do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more); }
}
