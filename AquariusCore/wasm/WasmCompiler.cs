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
        var functions = bodies.Select(b => new WasmFunction(b.WrapReturn, b.Constants.Select(Constant).ToArray())).ToArray();
        var metadata = new WasmMetadata(WasmAbi.Version, entry, exports, functions,
            assets?.ToDictionary(p => p.Key, p => Convert.ToBase64String(p.Value)) ?? new());
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 });
        void Section(byte id, Action<BinaryWriter> write)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            write(writer); output.WriteByte(id); WriteUnsigned(output, (uint)bytes.Length); bytes.Position = 0; bytes.CopyTo(output);
        }
        // Types: ()->void, (i32)->void, ()->i32, (i32)->i32.
        Section(1, w =>
        {
            U(w, 4); foreach (var t in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                w.Write((byte)0x60); U(w, t.Item1); if (t.Item1 == 1) w.Write((byte)0x7f);
                U(w, t.Item2); if (t.Item2 == 1) w.Write((byte)0x7f);
            }
        });
        Section(2, w =>
        {
            U(w, WasmAbi.Imports.Length); foreach (var i in WasmAbi.Imports)
            {
                Text(w, WasmAbi.ImportModule); Text(w, i.Name); w.Write((byte)0); U(w, i.Result ? 2 : i.Arguments);
            }
        });
        Section(3, w => { U(w, bodies.Count); foreach (var _ in bodies) U(w, 3); });
        Section(7, w => { U(w, bodies.Count); for (int i = 0; i < bodies.Count; i++) { Text(w, $"aqua_f{i}"); w.Write((byte)0); U(w, WasmAbi.Imports.Length + i); } });
        Section(10, w =>
        {
            U(w, bodies.Count); foreach (var body in bodies)
            {
                using var bytes = new MemoryStream(); using var code = new BinaryWriter(bytes, Encoding.UTF8, true);
                EmitBody(code, body); U(w, (int)bytes.Length); w.Write(bytes.ToArray());
            }
        });
        Section(0, w => { Text(w, WasmAbi.MetadataSection); w.Write(WasmProgram.SerializeMetadata(metadata)); });
        return new WasmProgram(output.ToArray(), metadata);
    }

    private static void EmitBody(BinaryWriter w, LoweredProgram body)
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
            if (operand.HasValue) I(operand.Value);
            w.Write((byte)0x10); U(w, System.Array.FindIndex(WasmAbi.Imports, x => x.Name == name));
        }
        void Resume(int n) { I(n); w.Write((byte)0x0f); }
        U(w, 0); // No additional locals; parameter 0 is the continuation block.
        w.Write(new byte[] { 0x03, 0x40 }); // loop
        w.Write(new byte[] { 0x02, 0x40 }); // invalid continuation
        for (int i = count - 1; i >= 0; i--) w.Write(new byte[] { 0x02, 0x40 });
        w.Write(new byte[] { 0x20, 0, 0x0e }); U(w, count); for (int i = 0; i < count; i++) U(w, i); U(w, count);
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
                        w.Write(new byte[] { 0x04, 0x7f }); I(Block(op.Code == IrOperation.JumpIfBreak ? n : i + 1)); w.Write((byte)0x05); I(Block(op.Code == IrOperation.JumpIfBreak ? i + 1 : n)); w.Write((byte)0x0b); break;
                    case IrOperation.Break: Call("break"); I(Block(n)); break;
                    case IrOperation.Call: Call("call", n); Resume(Block(i + 1)); terminated = true; break;
                    case IrOperation.ResolveMemberFunction: Call("resolveMemberFunction", n); Resume(Block(i + 1)); terminated = true; break;
                    case IrOperation.Return: Call("returned"); Resume(-1); terminated = true; break;
                    default:
                        string name = op.Code == IrOperation.MemberFunction ? "member" : char.ToLowerInvariant(op.Code.ToString()[0]) + op.Code.ToString()[1..];
                        var import = WasmAbi.Imports.First(x => x.Name == name);
                        Call(name, import.Arguments == 0 ? null : n); continue;
                }
                if (!terminated) { w.Write(new byte[] { 0x21, 0, 0x0c }); U(w, count - b); terminated = true; }
            }
            if (!terminated) { I(b + 1); w.Write(new byte[] { 0x21, 0, 0x0c }); U(w, count - b); }
        }
        w.Write(new byte[] { 0x0b, 0x00, 0x0b, 0x00, 0x0b });
    }
    internal static void WriteUnsigned(Stream s, uint n) { do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
    private static void U(BinaryWriter w, int n) => WriteUnsigned(w.BaseStream, checked((uint)n));
    private static void Text(BinaryWriter w, string text) { var b = Encoding.UTF8.GetBytes(text); U(w, b.Length); w.Write(b); }
    private static void Signed(BinaryWriter w, int n) { bool more; do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more); }
}
