using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AquariusLang.Wasm;

/// <summary>Links compiled application functions into the portable value runtime.</summary>
internal sealed class WasmRuntimeImage
{
    internal readonly Dictionary<byte, byte[]> Sections = new();
    internal readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal);
    internal readonly int FunctionCount;
    internal readonly int TypeCount;
    internal const int ProgramAddress = 4 * 1024 * 1024;

    internal WasmRuntimeImage()
    {
        using var input = typeof(WasmRuntimeImage).Assembly.GetManifestResourceStream("AquariusLang.Wasm.runtime.wasm")
            ?? throw new InvalidOperationException("Build the portable Wasm runtime before building the compiler.");
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        using var reader = new BinaryReader(new MemoryStream(bytes.ToArray())); reader.BaseStream.Position = 8;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte id = reader.ReadByte(); byte[] section = reader.ReadBytes(checked((int)ReadUnsigned(reader)));
            if (id != 0) Sections.Add(id, section);
        }
        int imports = Count(Sections[2]);
        FunctionCount = imports + Count(Sections[3]); TypeCount = Count(Sections[1]);
        using var exports = new BinaryReader(new MemoryStream(Sections[7]));
        uint count = ReadUnsigned(exports);
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.UTF8.GetString(exports.ReadBytes(checked((int)ReadUnsigned(exports))));
            byte kind = exports.ReadByte(); int index = checked((int)ReadUnsigned(exports));
            if (kind == 0) Functions.Add(name, index);
        }
    }
    internal static uint ReadUnsigned(BinaryReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = reader.ReadByte(); if (shift == 28 && (b & 0xf0) != 0) throw new InvalidDataException("Invalid Wasm integer.");
            value |= (uint)(b & 127) << shift; if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid Wasm integer.");
    }
    internal static int Count(byte[] section) { using var reader = new BinaryReader(new MemoryStream(section)); return checked((int)ReadUnsigned(reader)); }
    internal static byte[] Extend(byte[] section, int extra, Action<BinaryWriter> append)
    {
        using var reader = new BinaryReader(new MemoryStream(section)); uint count = ReadUnsigned(reader);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        WasmCompiler.WriteUnsigned(output, checked(count + (uint)extra));
        output.Write(section, (int)reader.BaseStream.Position, section.Length - (int)reader.BaseStream.Position);
        append(writer); return output.ToArray();
    }
    internal sealed class Data
    {
        private readonly MemoryStream bytes = new();
        private readonly BinaryWriter writer;
        private readonly Dictionary<string, int> strings = new(StringComparer.Ordinal);
        internal int[] Pools { get; private set; } = Array.Empty<int>();
        internal Data() { writer = new BinaryWriter(bytes, Encoding.Unicode, true); }
        internal int Reserve(int size, int align = 8)
        {
            while ((bytes.Length & (align - 1)) != 0) writer.Write((byte)0);
            int address = checked(ProgramAddress + (int)bytes.Length); writer.Write(new byte[size]); return address;
        }
        internal void Int(int address, int value) { long end = bytes.Length; bytes.Position = address - ProgramAddress; writer.Write(value); bytes.Position = end; }
        internal void Number(int address, double value) { long end = bytes.Length; bytes.Position = address - ProgramAddress; writer.Write(value); bytes.Position = end; }
        internal int Text(string text)
        {
            if (strings.TryGetValue(text, out int address)) return address;
            address = Reserve(4 + text.Length * 2, 4); Int(address, text.Length);
            long end = bytes.Length; bytes.Position = address + 4 - ProgramAddress; writer.Write(Encoding.Unicode.GetBytes(text)); bytes.Position = end;
            strings.Add(text, address); return address;
        }
        internal int Build(WasmFunction[] functions)
        {
            Pools = new int[functions.Length];
            int program = Reserve(8), definitions = Reserve(functions.Length * 24, 4);
            if (program != ProgramAddress) throw new InvalidOperationException("Invalid program data layout.");
            Int(program, functions.Length); Int(program + 4, definitions);
            for (int f = 0; f < functions.Length; f++)
            {
                var function = functions[f]; string[] parameters = function.Parameters ?? Array.Empty<string>();
                int definition = definitions + f * 24, names = Reserve(parameters.Length * 4, 4), constants = Reserve(function.Pool.Length * 16);
                Pools[f] = constants;
                Int(definition, f + 1); Int(definition + 4, parameters.Length); Int(definition + 8, function.StackCapacity);
                Int(definition + 12, checked((function.CacheBindings ? function.Pool.Length * 2 : 0) + (function.WrapReturn ? 1 : 0))); Int(definition + 16, names); Int(definition + 20, constants);
                for (int p = 0; p < parameters.Length; p++) Int(names + p * 4, Text(parameters[p]));
                for (int c = 0; c < function.Pool.Length; c++)
                {
                    var literal = function.Pool[c]; int slot = constants + c * 16;
                    int tag = literal.Type switch { "int" => 1, "float" => 2, "double" => 3, "bool" => 4, "name" or "string" or "assignment" => 5, "function" => 8, "null" => 11, "break" => 12, _ => 0 };
                    Int(slot, tag); if (literal.Text != null) Int(slot + 4, Text(literal.Text));
                    double number = literal.Type switch { "function" or "program" => literal.Function, "bool" => literal.Boolean ? 1 : 0,
                        "assignment" => literal.Operation switch { "Add" => 0, "Subtract" => 1, "Multiply" => 2, "Divide" => 3, _ => throw new InvalidDataException("Invalid assignment.") }, _ => literal.Number };
                    Number(slot + 8, number);
                }
            }
            return program;
        }
        internal byte[] Bytes => bytes.ToArray();
        internal int End => checked(ProgramAddress + (int)bytes.Length);
    }
}
